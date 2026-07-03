using System;
using System.Collections.Generic;

namespace Flash;

// =====================================================================================
//  Rate limiter for incoming CLIENT events -- protection against event flooding
//  (a mod menu spamming TriggerServerEvent tens of thousands of times per second).
//
//  PLACEMENT: inside Events.Dispatch, BEFORE the msgpack decode -- decode + handler
//  dispatch is the expensive part on our side, so a rejected event only costs this
//  bucket check. (FiveM ships its own coarse net-event limits; this is the
//  fine-grained framework layer: per player AND event name.)
//
//  MODEL: token bucket per (netId, eventName). Capacity = burst, refill = rate/s.
//  Fan-out note: FiveM delivers a client event to EVERY listening resource
//  separately; we count per delivery. The default limits are deliberately generous --
//  this guards against flooding (10^4/s), not against legitimate UI bursts.
//
//  KICK: whoever keeps flooding despite drops (many drops in a short window) gets
//  kicked -- otherwise the server still pays FiveM-side cost per packet.
//
//  server.cfg:
//    set flash_event_rate_limit      "32"   # events/s per (player, event); 0 = off
//    set flash_event_rate_burst      "64"   # bucket capacity (short bursts are fine)
//    set flash_event_rate_kick_after "500"  # drops within 10s until kick; 0 = never
// =====================================================================================
internal static class RateLimit
{
    internal enum Verdict { Allow, Drop, Kick }

    private struct Bucket { public float Tokens; public long LastMs; }
    private struct Abuse { public int Drops; public long WindowStartMs; }

    private static readonly Dictionary<(int NetId, string Ev), Bucket> s_buckets = new();
    private static readonly Dictionary<int, Abuse> s_abuse = new();

    // Config (convars, read once on first check -- that runs on the script thread).
    private static bool s_configured;
    private static int s_rate;
    private static int s_burst;
    private static int s_kickAfter;

    private static int s_calls; // for the opportunistic sweep

    /// <summary>
    /// Checks an incoming event delivery. Only "net:" sources (real clients) are
    /// limited; server-internal events run unthrottled. <paramref name="firstDropInWindow"/>
    /// lets the caller log ONCE per abuse window instead of per dropped event
    /// (log spam would be its own DoS vector).
    /// </summary>
    public static Verdict Check(string source, string eventName, out int netId, out bool firstDropInWindow)
    {
        netId = -1;
        firstDropInWindow = false;
        if (!source.StartsWith("net:", StringComparison.Ordinal)) return Verdict.Allow;
        if (!int.TryParse(source.AsSpan(4), out netId)) return Verdict.Allow;

        if (!s_configured)
        {
            s_configured = true;
            s_rate = global::Flash.Natives.Cfx.GetConvarInt("flash_event_rate_limit", 32);
            // Burst never below the rate (otherwise a 1-second burst could not even
            // reach the sustained rate).
            s_burst = Math.Max(global::Flash.Natives.Cfx.GetConvarInt("flash_event_rate_burst", 64), Math.Max(1, s_rate));
            s_kickAfter = global::Flash.Natives.Cfx.GetConvarInt("flash_event_rate_kick_after", 500);
        }
        if (s_rate <= 0) return Verdict.Allow;

        return CheckCore(netId, eventName, Environment.TickCount64, out firstDropInWindow);
    }

    // Pure core logic with injectable time -- deterministically testable, no natives.
    internal static Verdict CheckCore(int netId, string eventName, long nowMs, out bool firstDropInWindow)
    {
        firstDropInWindow = false;
        MaybeSweep(nowMs);

        var key = (netId, eventName);
        if (!s_buckets.TryGetValue(key, out var b))
            b = new Bucket { Tokens = s_burst, LastMs = nowMs };

        // Refill (rate tokens/s), capped at burst.
        float refill = (nowMs - b.LastMs) / 1000f * s_rate;
        b.Tokens = Math.Min(s_burst, b.Tokens + Math.Max(0f, refill));
        b.LastMs = nowMs;

        if (b.Tokens >= 1f)
        {
            b.Tokens -= 1f;
            s_buckets[key] = b;
            return Verdict.Allow;
        }
        s_buckets[key] = b;

        // Drop -- track the abuse window (kick on sustained flooding).
        if (!s_abuse.TryGetValue(netId, out var a) || nowMs - a.WindowStartMs > 10_000)
        {
            a = new Abuse { Drops = 0, WindowStartMs = nowMs };
            firstDropInWindow = true;
        }
        a.Drops++;
        if (s_kickAfter > 0 && a.Drops >= s_kickAfter)
        {
            s_abuse.Remove(netId);
            ClearPlayer(netId);
            return Verdict.Kick;
        }
        s_abuse[netId] = a;
        return Verdict.Drop;
    }

    /// <summary>Frees all buckets/abuse counters of a player (after a kick; dropped
    /// players are otherwise reclaimed by the idle sweep below).</summary>
    internal static void ClearPlayer(int netId)
    {
        List<(int, string)>? drop = null;
        foreach (var k in s_buckets.Keys)
            if (k.NetId == netId) (drop ??= new List<(int, string)>()).Add(k);
        if (drop != null)
            foreach (var k in drop) s_buckets.Remove(k);
        s_abuse.Remove(netId);
    }

    // Every ~4096 checks: evict entries idle for >60s (player left or went quiet).
    private static void MaybeSweep(long nowMs)
    {
        if ((++s_calls & 4095) != 0) return;
        List<(int, string)>? drop = null;
        foreach (var kv in s_buckets)
            if (nowMs - kv.Value.LastMs > 60_000) (drop ??= new List<(int, string)>()).Add(kv.Key);
        if (drop != null)
            foreach (var k in drop) s_buckets.Remove(k);
    }

    // Deterministic test hook: fixed config + clean state, no convar reads.
    internal static void ConfigureForTest(int rate, int burst, int kickAfter)
    {
        s_configured = true;
        s_rate = rate;
        s_burst = burst;
        s_kickAfter = kickAfter;
        s_buckets.Clear();
        s_abuse.Clear();
        s_calls = 0;
    }
}
