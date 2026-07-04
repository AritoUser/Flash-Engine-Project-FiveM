using System.Collections.Generic;

namespace Flash;

/// <summary>
/// Server-side player — a thin, convenient wrapper over the FiveM server natives
/// (which expect a "playerSrc" string). Holds the numeric NetID; the properties call
/// the respective native on access (no caching → always current).
///
/// Deliberately NOT named "Player": Flash.Player is already the GTA5 native handle
/// (player index). ServerPlayer is the higher-level, server-side view.
/// </summary>
public readonly struct ServerPlayer
{
    /// <summary>The player's network ID (unique server-side).</summary>
    public int NetId { get; }

    public ServerPlayer(int netId) => NetId = netId;

    // FiveM server natives take the source as a string.
    private string Src => NetId.ToString();

    /// <summary>The player's display name.</summary>
    public string Name => global::Flash.Natives.Cfx.GetPlayerName(Src) ?? "";

    /// <summary>The player's endpoint (IP).</summary>
    public string Endpoint => global::Flash.Natives.Cfx.GetPlayerEndpoint(Src) ?? "";

    /// <summary>true while the player is connected. Important after an `await` in a
    /// join/handler path: the player may have dropped in the meantime -- check before
    /// caching per-player state, otherwise ghost entries survive the drop.</summary>
    public bool Connected => !string.IsNullOrEmpty(global::Flash.Natives.Cfx.GetPlayerEndpoint(Src));

    /// <summary>Current ping in ms.</summary>
    public int Ping => global::Flash.Natives.Cfx.GetPlayerPing(Src);

    /// <summary>All identifiers of the player (license:, steam:, discord:, ip: ...).</summary>
    public string[] Identifiers
    {
        get
        {
            int n = global::Flash.Natives.Cfx.GetNumPlayerIdentifiers(Src);
            var result = new string[n];
            for (int i = 0; i < n; i++)
                result[i] = global::Flash.Natives.Cfx.GetPlayerIdentifier(Src, i) ?? "";
            return result;
        }
    }

    /// <summary>Returns the first identifier of a type (e.g. "license"), or null.</summary>
    public string? IdentifierOfType(string type)
        => global::Flash.Natives.Cfx.GetPlayerIdentifierByType(Src, type);

    /// <summary>Kicks the player from the server.</summary>
    public void Kick(string reason = "")
        => global::Flash.Natives.Cfx.DropPlayer(Src, reason);

    /// <summary>Sends an event to exactly this player (server → client).</summary>
    public void Emit(string eventName, params object?[] args)
        => global::Flash.Events.EmitClient(NetId, eventName, args);

    /// <summary>This player's state bag ("player:&lt;netId&gt;"). Replicated values are
    /// readable on the client via LocalPlayer.state. Server-authoritative.</summary>
    public StateBag State => new StateBag($"player:{NetId}");

    /// <summary>A CancellationToken that fires when this player disconnects — pair it
    /// with <c>Async.Delay(ms, token)</c> so waits die with the player instead of
    /// resuming against an empty session.</summary>
    public System.Threading.CancellationToken DropToken() => DropTokens.Get(NetId);
}

/// <summary>
/// Access to connected players (server-side). Built on the index natives
/// (GetNumPlayerIndices/GetPlayerFromIndex).
/// </summary>
public static class Players
{
    /// <summary>Player by NetID. (Does not check whether they are connected —
    /// properties then return empty values.)</summary>
    public static ServerPlayer Get(int netId) => new ServerPlayer(netId);

    /// <summary>Number of currently connected players.</summary>
    public static int Count => global::Flash.Natives.Cfx.GetNumPlayerIndices();

    /// <summary>All currently connected players.</summary>
    public static IReadOnlyList<ServerPlayer> All
    {
        get
        {
            int n = global::Flash.Natives.Cfx.GetNumPlayerIndices();
            var list = new List<ServerPlayer>(n);
            for (int i = 0; i < n; i++)
            {
                string? src = global::Flash.Natives.Cfx.GetPlayerFromIndex(i);
                if (src != null && int.TryParse(src, out int id))
                    list.Add(new ServerPlayer(id));
            }
            return list;
        }
    }
}
