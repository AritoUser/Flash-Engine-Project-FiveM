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

    // Async continuations land here (instead of on a thread-pool thread).
    public override void Post(SendOrPostCallback d, object? state)
    {
        lock (_gate) _queue.Enqueue((d, state));
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

        // 2) Drain queued continuations. TIMERS scheduled while draining run next frame
        //    (they were added after the timer phase) -> e.g. `while(true){ await Delay(0); }`
        //    runs exactly once per frame, no hang.
        while (true)
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

    /// <summary>Discards all pending continuations/timers -- on resource unload, so
    /// captured delegates don't keep the collectible ALC alive.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _queue.Clear();
            _timers.Clear();
        }
    }

    private static void Run(Action a)
    {
        // An error in one continuation must not kill the frame (or the other
        // continuations) -> execute isolated + log.
        try { a(); }
        catch (Exception ex) { Log.Error($"Async scheduler: {ex.Message}"); }
    }
}
