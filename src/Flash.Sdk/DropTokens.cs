using System.Collections.Generic;
using System.Threading;

namespace Flash;

/// <summary>
/// CancellationTokens that fire when a player disconnects (#6) -- for
/// <c>await Async.Delay(ms, player.DropToken())</c>: a countdown/workflow dies with the
/// player instead of blindly running on and hitting an empty session.
/// Partitioned per resource (shared SDK, ALC unload) and wired lazily.
/// </summary>
internal static class DropTokens
{
    // resource -> (netId -> CTS). The playerDropped listener is registered once per
    // resource; Cancel therefore runs in the event dispatch on the script thread.
    private static readonly Dictionary<string, Dictionary<int, CancellationTokenSource>> s_byResource = new();

    public static CancellationToken Get(int netId)
    {
        // Already-disconnected player: playerDropped is already past for this netId, so a
        // CTS created here would never be cancelled/removed and would sit in the map until
        // resource stop (leak), with the awaiter hanging forever. Return an already-cancelled
        // token instead: the task cancels cleanly right away, no CTS. (#104)
        if (!Players.Get(netId).Connected)
            return new CancellationToken(canceled: true);

        string res = global::Flash.Natives.Cfx.GetCurrentResourceName() ?? "";
        if (!s_byResource.TryGetValue(res, out var byPlayer))
        {
            var map = new Dictionary<int, CancellationTokenSource>();
            s_byResource[res] = byPlayer = map;
            Events.On("playerDropped", _ =>
            {
                // Forgery guard: a client can TriggerServerEvent("playerDropped") with its own
                // stamped netId and would otherwise cancel its OWN drop token while still
                // connected -- escaping jail/countdown/cooldown tasks bound to it. Only the core
                // drop ("internal-net:<id>") is trusted (same guarantee as #73/#147). (#168)
                if (!Events.IsFromCore) return;
                if (map.Remove(Events.SourceNetId, out var cts))
                {
                    cts.Cancel(); // posts continuations through the captured context
                    cts.Dispose();
                }
            });
        }

        if (!byPlayer.TryGetValue(netId, out var c))
        {
            c = new CancellationTokenSource();
            byPlayer[netId] = c;
        }
        return c.Token;
    }

    /// <summary>On resource stop: dispose without cancelling — the resource's scheduler
    /// is being cleared anyway; firing continuations into a dying ALC helps nobody.</summary>
    internal static void ClearResource(string resource)
    {
        if (s_byResource.Remove(resource, out var byPlayer))
            foreach (var cts in byPlayer.Values) cts.Dispose();
    }
}
