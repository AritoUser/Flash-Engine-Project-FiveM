using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Flash;

// =====================================================================================
//  Structured audit trail (#144).
//
//  Busy RP servers must log every meaningful player interaction (money moves, item
//  hand-overs, admin actions, kills) for moderation and economy forensics. The two
//  traditional approaches both hurt: synchronous Discord webhooks rate-limit and lose
//  logs; INSERTs into the GAME database contend with vital queries (position saves)
//  and cause visible lag spikes.
//
//  This pipeline never touches the game DB and never blocks the script thread:
//    Audit.Log(...) -> lock-free in-memory queue -> one background writer task that
//    batches entries once per second into daily JSON-Lines files (one JSON object per
//    line — directly ingestible by Loki/Elastic/jq). Custom sinks (e.g. a Discord or
//    Loki forwarder) subscribe per resource via AddSink and are fed from the writer
//    task in the same batch rhythm.
//
//  THREADING: Log(...) with plain strings is safe from ANY thread (no natives). The
//  ServerPlayer overload reads Name/license and therefore must run on the script
//  thread. Sinks run on the background writer task — no natives inside sinks.
// =====================================================================================

/// <summary>One structured audit record (what happened, who did it, to whom, details).</summary>
public readonly struct AuditEntry
{
    /// <summary>Unix timestamp in milliseconds (UTC).</summary>
    public long TsMs { get; init; }
    /// <summary>Action category, free-form (e.g. "money:transfer", "admin:ban").</summary>
    public string Action { get; init; }
    /// <summary>Who acted (name, license, account id — whatever identifies the origin).</summary>
    public string Origin { get; init; }
    /// <summary>Optional action target (player, entity, account, ...).</summary>
    public string? Target { get; init; }
    /// <summary>Optional structured metadata, already serialized as JSON.</summary>
    public string? DetailsJson { get; init; }
}

/// <summary>
/// Non-blocking structured audit logger: entries are queued in memory and written by a
/// background task as daily JSON-Lines files under <c>flash-audit/</c> in the server
/// data directory (override via <see cref="Configure"/>). The game database is never
/// touched. (#144)
/// </summary>
public static class Audit
{
    private static readonly ConcurrentQueue<AuditEntry> s_queue = new();
    // Per-resource sinks (partitioned like Events so an unloading resource's delegates
    // don't pin its collectible ALC). Each sink remembers the resource's script-thread
    // context captured at AddSink -> the writer marshals the invocation back onto it, so a
    // sink may safely call natives (#203). Guarded by s_lock; snapshot for the writer task.
    private static readonly Dictionary<string, List<(Action<AuditEntry> Sink, FlashSyncContext? Ctx)>> s_sinks = new();
    private static readonly object s_lock = new();
    private static string? s_dir;                 // resolved lazily on first flush
    private static int s_writerStarted;           // 0/1 via Interlocked (start exactly once)
    private static long s_dropped;                // entries dropped due to overflow
    private const int MaxQueued = 100_000;        // hard cap: an insane spam loop must not OOM the host

    /// <summary>Overrides the output directory (default: "flash-audit" under the server
    /// data directory). Call once at startup if needed.</summary>
    public static void Configure(string directory) => s_dir = directory;

    /// <summary>Queues a structured audit entry (any thread, never blocks, no natives).
    /// <paramref name="details"/> is serialized to JSON on the background writer.</summary>
    public static void Log(string action, string origin, string? target = null, object? details = null)
    {
        if (s_queue.Count >= MaxQueued) { Interlocked.Increment(ref s_dropped); return; }

        // Serialize details HERE (caller thread) — the object may be mutated afterwards,
        // and resource objects must not be retained by the shared queue (ALC pinning).
        string? json = null;
        if (details != null)
        {
            try { json = JsonSerializer.Serialize(details); }
            catch (Exception ex) { json = JsonSerializer.Serialize(ex.Message); }
        }

        s_queue.Enqueue(new AuditEntry
        {
            TsMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Action = action,
            Origin = origin,
            Target = target,
            DetailsJson = json,
        });

        if (s_writerStarted == 0 && Interlocked.Exchange(ref s_writerStarted, 1) == 0)
            _ = Task.Run(WriterLoop);
    }

    /// <summary>Queues an audit entry for a player origin ("Name (license)"). Reads
    /// natives — script thread only; use the string overload off-thread.</summary>
    public static void Log(ServerPlayer origin, string action, string? target = null, object? details = null)
    {
        ThreadGuard.AssertScriptThread("Audit.Log(ServerPlayer)"); // reads natives (#198)
        string lic = origin.IdentifierOfType("license") ?? origin.Endpoint;
        Log(action, $"{origin.Name} ({lic})", target, details);
    }

    /// <summary>Registers a custom sink (e.g. a Loki/Discord forwarder). Invoked in batch
    /// rhythm, marshalled back onto THIS resource's script thread (so it may call natives).
    /// Removed automatically when the registering resource stops.</summary>
    public static void AddSink(Action<AuditEntry> sink)
    {
        string res = global::Flash.Natives.Cfx.GetCurrentResourceName() ?? "";
        var ctx = SynchronizationContext.Current as FlashSyncContext; // capture the script thread
        lock (s_lock)
        {
            if (!s_sinks.TryGetValue(res, out var list)) s_sinks[res] = list = new();
            list.Add((sink, ctx));
        }
    }

    // One writer for the process: batches the queue once per second into the daily
    // JSONL file + feeds the sinks. IO errors are logged (throttled) but never throw
    // into the game — losing an audit line must not hurt the server.
    private static async Task WriterLoop()
    {
        long lastIoError = 0;
        var sb = new StringBuilder();
        while (true)
        {
            try { await Task.Delay(1000).ConfigureAwait(false); } catch { }

            if (s_queue.IsEmpty) continue;

            (Action<AuditEntry> Sink, FlashSyncContext? Ctx)[] sinks;
            lock (s_lock)
                sinks = s_sinks.Count == 0
                    ? Array.Empty<(Action<AuditEntry>, FlashSyncContext?)>()
                    : s_sinks.Values.SelectManyToArray();

            sb.Clear();
            while (s_queue.TryDequeue(out var e))
            {
                sb.Append("{\"ts\":").Append(e.TsMs)
                  .Append(",\"action\":").Append(JsonSerializer.Serialize(e.Action))
                  .Append(",\"origin\":").Append(JsonSerializer.Serialize(e.Origin));
                if (e.Target != null)
                    sb.Append(",\"target\":").Append(JsonSerializer.Serialize(e.Target));
                if (e.DetailsJson != null)
                    sb.Append(",\"details\":").Append(e.DetailsJson);
                sb.Append("}\n");

                foreach (var (sink, ctx) in sinks)
                {
                    var entry = e;
                    // Marshal onto the registering resource's script thread so a sink may
                    // call natives safely (#203). No context (registered off-thread) -> run
                    // inline (the sink then owns the off-thread contract).
                    if (ctx != null) ctx.Post(_ => { try { sink(entry); } catch { } }, null);
                    else { try { sink(entry); } catch { } }
                }
            }

            long dropped = Interlocked.Exchange(ref s_dropped, 0);
            if (dropped > 0)
                sb.Append("{\"ts\":").Append(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                  .Append(",\"action\":\"audit:overflow\",\"origin\":\"flash-sdk\",\"details\":{\"dropped\":")
                  .Append(dropped).Append("}}\n");

            try
            {
                string dir = s_dir ??= Path.Combine(Environment.CurrentDirectory, "flash-audit");
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, $"audit-{DateTime.UtcNow:yyyy-MM-dd}.jsonl");
                await File.AppendAllTextAsync(file, sb.ToString()).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Throttle to one complaint per minute — a full disk must not spam the log.
                long now = Environment.TickCount64;
                if (now - lastIoError > 60_000)
                {
                    lastIoError = now;
                    System.Console.WriteLine($"[Flash.Audit] write failed: {ex.Message}");
                }
            }
        }
    }

    // Flattens the sink lists into one array under the caller's lock (tiny helper to
    // keep the lock section allocation-light and obvious).
    private static (Action<AuditEntry> Sink, FlashSyncContext? Ctx)[] SelectManyToArray(
        this Dictionary<string, List<(Action<AuditEntry> Sink, FlashSyncContext? Ctx)>>.ValueCollection values)
    {
        int n = 0;
        foreach (var l in values) n += l.Count;
        var arr = new (Action<AuditEntry>, FlashSyncContext?)[n];
        int i = 0;
        foreach (var l in values)
            foreach (var s in l) arr[i++] = s;
        return arr;
    }

    /// <summary>On resource stop: drop the resource's sinks (frees captured delegates so
    /// the collectible ALC can unload). Called by the host.</summary>
    internal static void ClearResource(string resource)
    {
        lock (s_lock) s_sinks.Remove(resource);
    }
}
