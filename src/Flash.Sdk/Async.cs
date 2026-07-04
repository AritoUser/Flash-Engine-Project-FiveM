using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Flash;

// =====================================================================================
//  Async/scheduler -- async/await on the FiveM script thread.
//
//  WHY:
//    FiveM scripting runs single-threaded on the script thread. Natives + state may only
//    be touched there. Plain C# async/await would resume continuations on thread-pool
//    threads -> forbidden off-thread access. Solution: one SynchronizationContext per
//    resource whose scheduled work the host drains PER FRAME (in the existing
//    per-resource tick) on the script thread. An `await` continuation is thus guaranteed
//    to land back on the script thread -- like FiveM's own C# runtime.
//
//  DECISION:
//    Exactly ONE FlashSyncContext per resource, centrally managed by Scheduler (key =
//    resource name). The host installs it as SynchronizationContext.Current around
//    OnStart/OnTick; events/funcrefs install it around handler/callback dispatch. So
//    `await` captures it everywhere automatically -- without the resource author wiring
//    anything.
// =====================================================================================

/// <summary>
/// Async helpers for Flash resources. Callable inside a resource (OnStart/OnTick/
/// handlers); continuations resume on the server script thread.
/// </summary>
public static class Async
{
    /// <summary>
    /// Waits <paramref name="milliseconds"/> ms WITHOUT blocking the server frame.
    /// <c>await Flash.Async.Delay(1000)</c> resumes on the script thread after ~1s.
    /// </summary>
    public static Task Delay(int milliseconds)
    {
        if (milliseconds < 0) milliseconds = 0;

        // The current context is the running resource's (set by the host/dispatch). If
        // it's missing, Delay was called outside a resource -> a clear error instead of
        // a silent off-thread continuation.
        if (SynchronizationContext.Current is not FlashSyncContext ctx)
            throw new InvalidOperationException(
                "Call Flash.Async.Delay only inside a Flash resource (OnStart/OnTick/handlers).");

        // RunContinuationsAsynchronously: SetResult must NOT start the continuation
        // inline but post it back through the captured (our) context.
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.ScheduleTimer(Environment.TickCount64 + milliseconds, () => tcs.TrySetResult());
        return tcs.Task;
    }

    /// <summary>
    /// Like <see cref="Delay(int)"/>, but cancellable (#6): cancelling makes the await
    /// throw <see cref="TaskCanceledException"/> instead of blindly resuming — e.g. with
    /// <see cref="ServerPlayer.DropToken"/> so a countdown dies with the player:
    /// <c>await Async.Delay(5000, player.DropToken());</c>
    /// </summary>
    public static Task Delay(int milliseconds, CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled) return Delay(milliseconds);
        if (milliseconds < 0) milliseconds = 0;
        if (SynchronizationContext.Current is not FlashSyncContext ctx)
            throw new InvalidOperationException(
                "Call Flash.Async.Delay only inside a Flash resource (OnStart/OnTick/handlers).");

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.ScheduleTimer(Environment.TickCount64 + milliseconds, () => tcs.TrySetResult());
        // TrySetCanceled posts the continuation through the captured context -> a cancel
        // continuation also runs on the script thread. If the timer fires later anyway,
        // TrySetResult on the already-cancelled TCS is a no-op.
        var reg = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
        // Dispose the registration once the delay settles. Without this a LONG-LIVED token
        // (e.g. player.DropToken(), alive until disconnect) keeps the callback -- and through
        // it the TCS, its Task and the captured async state machine -- rooted after the delay
        // completed normally. A per-second `await Delay(ms, dropToken)` loop then leaks one of
        // each per iteration. ExecuteSynchronously: the cleanup is trivial, no need to schedule. (#149)
        tcs.Task.ContinueWith(static (_, s) => ((CancellationTokenRegistration)s!).Dispose(),
            reg, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return tcs.Task;
    }

    /// <summary>Waits until the next server frame (tick). Useful to spread work across
    /// multiple frames.</summary>
    public static Task NextFrame() => Delay(0);

    /// <summary>
    /// Invokes <paramref name="callback"/> ONCE after <paramref name="milliseconds"/> ms
    /// (on the script thread). The returned handle cancels the timer via Dispose().
    /// </summary>
    public static IDisposable SetTimeout(int milliseconds, Action callback)
    {
        var h = new TimerHandle();
        _ = RunTimeout(milliseconds, callback, h);
        return h;
    }

    /// <summary>
    /// Invokes <paramref name="callback"/> REPEATEDLY every <paramref name="milliseconds"/> ms
    /// (on the script thread) until the returned handle is disposed.
    /// </summary>
    public static IDisposable SetInterval(int milliseconds, Action callback)
    {
        var h = new TimerHandle();
        _ = RunInterval(milliseconds, callback, h);
        return h;
    }

    /// <summary>
    /// Real-time scheduling (#13): invokes <paramref name="callback"/> every day at
    /// <paramref name="hour"/>:<paramref name="minute"/> SERVER-LOCAL time (on the
    /// script thread) until the handle is disposed. For "payout at 04:00", weekly
    /// resets etc. — interval loops can't hit wall-clock times.
    /// </summary>
    public static IDisposable DailyAt(int hour, int minute, Action callback)
    {
        var h = new TimerHandle();
        _ = RunClock(() => NextDailyDelayMs(DateTime.Now, hour, minute), callback, h);
        return h;
    }

    /// <summary>Invokes <paramref name="callback"/> every hour at minute
    /// <paramref name="minute"/> (server-local, script thread) until disposed.</summary>
    public static IDisposable HourlyAt(int minute, Action callback)
    {
        var h = new TimerHandle();
        _ = RunClock(() => NextHourlyDelayMs(DateTime.Now, minute), callback, h);
        return h;
    }

    // Next daily occurrence: today at hour:minute, or tomorrow if it already passed.
    // Pure + injectable "now" time -> deterministically testable.
    internal static long NextDailyDelayMs(DateTime now, int hour, int minute)
    {
        var next = new DateTime(now.Year, now.Month, now.Day,
            Math.Clamp(hour, 0, 23), Math.Clamp(minute, 0, 59), 0, now.Kind);
        if (next <= now) next = next.AddDays(1);
        return (long)(next - now).TotalMilliseconds;
    }

    internal static long NextHourlyDelayMs(DateTime now, int minute)
    {
        var next = new DateTime(now.Year, now.Month, now.Day, now.Hour,
            Math.Clamp(minute, 0, 59), 0, now.Kind);
        if (next <= now) next = next.AddHours(1);
        return (long)(next - now).TotalMilliseconds;
    }

    private static async Task RunClock(Func<long> nextDelayMs, Action cb, TimerHandle h)
    {
        while (!h.Cancelled)
        {
            // Wait toward the target in 60s chunks, recomputing against the wall clock
            // each time -- it can jump (NTP correction, DST change); a single 24h wait
            // would only notice that the next day.
            while (!h.Cancelled)
            {
                long remaining = nextDelayMs();
                if (remaining <= 1_000)
                {
                    await Delay((int)Math.Max(remaining, 1));
                    break; // target reached
                }
                await Delay((int)Math.Min(remaining - 500, 60_000));
            }
            if (h.Cancelled) break;
            cb(); // errors are caught/logged by the scheduler drain
            await Delay(1_500); // past the target second -> never fire twice
        }
    }

    private static async Task RunTimeout(int ms, Action cb, TimerHandle h)
    {
        await Delay(ms);
        if (!h.Cancelled) cb(); // errors here are caught + logged by the scheduler drain.
    }

    private static async Task RunInterval(int ms, Action cb, TimerHandle h)
    {
        while (!h.Cancelled)
        {
            await Delay(ms);
            if (h.Cancelled) break;
            cb();
        }
    }

    // Cancellation handle for SetTimeout/SetInterval. Dispose() only sets a flag -> the
    // running async loop ends itself at the next check.
    private sealed class TimerHandle : IDisposable
    {
        public bool Cancelled { get; private set; }
        public void Dispose() => Cancelled = true;
    }
}

/// <summary>
/// Central management of the per-resource SynchronizationContexts. Internal -- the host
/// creates/drives them, events/funcrefs install them around dispatch, resource authors
/// only see Flash.Async.
/// </summary>
internal static class Scheduler
{
    // Per-resource context (key = resource name). Accessed single-threaded (script
    // thread) -> no extra synchronization needed.
    private static readonly Dictionary<string, FlashSyncContext> s_byResource = new();

    public static FlashSyncContext GetOrCreate(string resource)
    {
        if (!s_byResource.TryGetValue(resource, out var ctx))
        {
            ctx = new FlashSyncContext();
            s_byResource[resource] = ctx;
        }
        return ctx;
    }

    public static FlashSyncContext? Get(string resource)
        => s_byResource.TryGetValue(resource, out var ctx) ? ctx : null;

    /// <summary>On resource unload: discard the context (Clear frees captured delegates).</summary>
    public static void Remove(string resource)
    {
        if (s_byResource.TryGetValue(resource, out var ctx))
        {
            ctx.Clear();
            s_byResource.Remove(resource);
        }
    }

    /// <summary>Runs <paramref name="body"/> with <paramref name="ctx"/> installed
    /// (save/restore) so an `await` inside captures the right resource context. With null,
    /// body runs unchanged (e.g. host-internal callbacks without a resource).</summary>
    public static void RunWith(FlashSyncContext? ctx, Action body)
    {
        if (ctx == null) { body(); return; }
        var prev = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(ctx);
        try { body(); }
        finally { SynchronizationContext.SetSynchronizationContext(prev); }
    }
}

/// <summary>
/// Per-resource SynchronizationContext: collects scheduled continuations (Post) and
/// timers and drains them batched in the per-frame Drain() on the script thread.
/// </summary>
internal sealed class FlashSyncContext : SynchronizationContext
{
    private readonly object _gate = new();
    private readonly Queue<(SendOrPostCallback cb, object? state)> _queue = new();
    private readonly List<(long dueMs, Action complete)> _timers = new();

    // Set on unload (Clear): a dead context is never drained again, so late continuations
    // must be DROPPED rather than enqueued -- otherwise they pin the collectible ALC (#82).
    private bool _dead;

    // Async continuations land here (instead of on a thread-pool thread).
    public override void Post(SendOrPostCallback d, object? state)
    {
        lock (_gate)
        {
            // After the resource unloaded this context is dead and TickResource/Drain will
            // never run again. A late continuation (e.g. a Db.QueryAsync Task.Run completing
            // AFTER the resource stopped) would otherwise sit in _queue forever, and its
            // captured delegate would keep the collectible ALC alive -> memory creep on
            // repeated restarts. Drop it -- the awaiting code is gone with the resource. (#82)
            if (_dead) return;
            _queue.Enqueue((d, state));
        }
    }

    // Send (synchronous) is rare; on the single-thread model, execute directly.
    public override void Send(SendOrPostCallback d, object? state) => d(state);

    /// <summary>Schedules an action due at <paramref name="dueMs"/> (Environment.TickCount64 base).</summary>
    public void ScheduleTimer(long dueMs, Action complete)
    {
        lock (_gate) _timers.Add((dueMs, complete));
    }

    /// <summary>
    /// Called by the host once per frame: fire due timers, then drain all queued
    /// continuations (including those just posted by the timers).
    /// </summary>
    public void Drain(long nowMs)
    {
        // 1) Collect + fire due timers. Their action (tcs.TrySetResult) typically posts
        //    the await continuation into the queue below.
        List<Action>? due = null;
        lock (_gate)
        {
            for (int i = _timers.Count - 1; i >= 0; i--)
            {
                if (_timers[i].dueMs <= nowMs)
                {
                    (due ??= new List<Action>()).Add(_timers[i].complete);
                    _timers.RemoveAt(i);
                }
            }
        }
        if (due != null)
            foreach (var c in due) Run(c);

        // 2) Drain queued continuations in a BOUNDED batch. Snapshot the count AFTER the
        //    timer phase (so THIS frame's timer -> await-continuation handoff still runs
        //    now), then process only that many. Continuations POSTED WHILE draining wait for
        //    the next frame. This matters for continuations that re-post themselves straight
        //    into _queue: `await Task.Yield()` (and any await on a task that completes inline
        //    on our context) enqueues a new continuation immediately -- an unbounded loop
        //    would drain it in the same frame forever, hijacking the server thread until the
        //    FiveM watchdog kills the process. Bounded, `while(true){ await Task.Yield(); }`
        //    advances one step per frame instead. (#72)
        //    (Delay(0)/timers already deferred to the next frame via _timers, so their
        //    once-per-frame behaviour is unchanged.)
        int batch;
        lock (_gate) batch = _queue.Count;
        for (int i = 0; i < batch; i++)
        {
            (SendOrPostCallback cb, object? state) item;
            lock (_gate)
            {
                if (_queue.Count == 0) break;
                item = _queue.Dequeue();
            }
            Run(() => item.cb(item.state));
        }
    }

    /// <summary>Discards all pending continuations/timers and marks the context dead -- on
    /// resource unload, so captured delegates don't keep the collectible ALC alive (and any
    /// late continuation posted after this is dropped, not re-queued). (#82)</summary>
    public void Clear()
    {
        List<Action> pending;
        lock (_gate)
        {
            _dead = true;
            _queue.Clear();
            // Snapshot the pending timer completions, then clear.
            pending = new List<Action>(_timers.Count);
            foreach (var (_, complete) in _timers) pending.Add(complete);
            _timers.Clear();
        }

        // COMPLETE the pending timers (outside the lock) instead of just dropping them. Each is
        // `() => tcs.TrySetResult()` for a suspended `await Async.Delay(...)`. If left incomplete,
        // the Task never finishes and its captured async state machine stays rooted -> the
        // collectible ALC is pinned on every hot-reload. Completing them lets the state machines
        // release; the continuations they post are DROPPED here because _dead is already set, so
        // no resource code runs after stop. (#150)
        foreach (var complete in pending)
        {
            try { complete(); }
            catch { /* a completion callback must not break the unload path */ }
        }
    }

    private static void Run(Action a)
    {
        // An error in one continuation must not kill the frame (or the other
        // continuations) -> execute isolated + log + report to the Diagnostics hook.
        try { a(); }
        catch (Exception ex)
        {
            Log.Error($"Async scheduler: {ex.Message}");
            Diagnostics.Report("scheduler", ex);
        }
    }
}
