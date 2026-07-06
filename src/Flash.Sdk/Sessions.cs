using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace Flash;

// =====================================================================================
//  Ephemeral session identity tokens (#183).
//
//  FiveM recycles NetIDs: when player A (netId 5) leaves and player B joins, B may get
//  netId 5. Any late async work keyed on the netId then hits the WRONG player (state
//  poisoning). A SessionKey is a cryptographically random token that is unique per
//  CONNECTION — it never survives a reconnect, so it is safe to key async state on and
//  to hand to the owning client (NUI auth) as a bearer credential.
//
//  ROBUSTNESS: the mapping is validated against the player's stable identity (identifier
//  chain) on EVERY read, not just cleaned on playerDropped — so even if a drop event is
//  missed, a recycled netId can never inherit the previous owner's key (reconnect
//  reentrancy, rule 3 of the proposal). Drop cleanup additionally runs at the host's
//  global playerDropped choke point (EventBridge, like Rpc.ClearPlayer).
//
//  SECURITY (documented in docs/api.md): the key is a SECRET between server and owning
//  client. Never put it in replicated state bags and never broadcast it.
// =====================================================================================

/// <summary>
/// Per-connection session tokens: <see cref="ServerPlayer.SessionKey"/> returns a
/// cryptographically random GUID that is stable for one connection and invalid after
/// the player disconnects. <see cref="Players.GetBySession"/> resolves a token back to
/// the player. Script-thread only (reads natives to validate ownership). (#183)
/// </summary>
public static class Sessions
{
    // netId -> (key, stable owner identity at generation time); key -> netId reverse.
    // Guarded by s_lock: reads/writes happen on the script thread, but the registry is
    // global shared state and cheap to lock — consistent with the #175/#148 decisions.
    private static readonly Dictionary<int, (string Key, string Owner)> s_byNet = new();
    private static readonly Dictionary<string, int> s_byKey = new(StringComparer.Ordinal);
    private static readonly object s_lock = new();

    /// <summary>The session key for a connected player — generated on first access per
    /// connection. Returns null when the player is not connected. A reconnect (or a
    /// recycled netId with a different owner) always yields a FRESH key.</summary>
    public static string? GetKey(int netId)
    {
        ThreadGuard.AssertScriptThread("ServerPlayer.SessionKey"); // OwnerIdentity reads natives (#201)
        string? owner = OwnerIdentity(netId);
        if (owner == null) return null; // not connected

        lock (s_lock)
        {
            if (s_byNet.TryGetValue(netId, out var e))
            {
                if (e.Owner == owner) return e.Key;
                // NetID was recycled to a different player -> the old key is dead.
                s_byKey.Remove(e.Key);
                s_byNet.Remove(netId);
            }
            // Cryptographically secure (proposal rule 2): never Random/timestamps.
            Span<byte> raw = stackalloc byte[16];
            RandomNumberGenerator.Fill(raw);
            string key = new Guid(raw).ToString();
            s_byNet[netId] = (key, owner);
            s_byKey[key] = netId;
            return key;
        }
    }

    /// <summary>Resolves a session key back to its player, or null if the key is unknown,
    /// the player disconnected, or the netId meanwhile belongs to someone else.</summary>
    public static ServerPlayer? GetPlayer(string sessionKey)
    {
        ThreadGuard.AssertScriptThread("Players.GetBySession"); // OwnerIdentity reads natives (#201)
        if (string.IsNullOrEmpty(sessionKey)) return null;
        int netId;
        string expectedOwner;
        lock (s_lock)
        {
            if (!s_byKey.TryGetValue(sessionKey, out netId)) return null;
            expectedOwner = s_byNet[netId].Owner;
        }
        // Validate OUTSIDE the lock (native reads): the key is only valid while the
        // same physical player still owns the netId.
        if (OwnerIdentity(netId) != expectedOwner)
        {
            ClearPlayer(netId);
            return null;
        }
        return Players.Get(netId);
    }

    // Stable identity of the CURRENT owner of a netId: first identifier (license comes
    // first in FiveM's identifier order for licensed clients), endpoint as LAN fallback.
    // null = not connected.
    private static string? OwnerIdentity(int netId)
    {
        var p = Players.Get(netId);
        if (!p.Connected) return null;
        string? lic = p.IdentifierOfType("license");
        if (!string.IsNullOrEmpty(lic)) return "license:" + lic;
        string ep = p.Endpoint;
        return string.IsNullOrEmpty(ep) ? null : "ip:" + ep;
    }

    /// <summary>Invalidates the session of a dropped player. Called from the host's
    /// global playerDropped choke point; safe to call for players without a key.</summary>
    internal static void ClearPlayer(int netId)
    {
        lock (s_lock)
        {
            if (s_byNet.Remove(netId, out var e))
                s_byKey.Remove(e.Key);
        }
    }
}
