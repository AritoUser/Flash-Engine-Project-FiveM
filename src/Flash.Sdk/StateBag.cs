using System;
using System.Runtime.InteropServices;

namespace Flash;

/// <summary>
/// A state bag (replicated key/value store) — server-authoritative: the server sets
/// values, replicated values reach the clients (they read them e.g. via
/// LocalPlayer.state / Entity(x).state / GlobalState). Fits the OneSync server
/// authority vision: the server is the source of truth, clients cannot forge state.
///
/// Bag names: "global", "player:&lt;netId&gt;", "entity:&lt;netId&gt;".
/// </summary>
public sealed class StateBag
{
    private readonly string _bagName;

    internal StateBag(string bagName) => _bagName = bagName;

    /// <summary>The replicated state bag of a network entity ("entity:&lt;netId&gt;") —
    /// e.g. to attach server-authoritative state (vehicle mods, door state) that a client
    /// script reads via <c>Entity(netId).state</c>. Server-authoritative.</summary>
    public static StateBag ForEntity(int netId) => new StateBag($"entity:{netId}");

    /// <summary>The replicated state bag of a player ("player:&lt;netId&gt;"). Same value
    /// as <see cref="ServerPlayer.State"/>, usable without a ServerPlayer handle.</summary>
    public static StateBag ForPlayer(int netId) => new StateBag($"player:{netId}");

    /// <summary>Sets a value. replicated=true → replicated to clients.</summary>
    public unsafe void Set(string key, object? value, bool replicated = true)
    {
        ThreadGuard.AssertScriptThread("StateBag.Set"); // native SET_STATE_BAG_VALUE (#209)
        // Value as a SINGLE msgpack value (not an array). Raw through Native.Invoke with
        // a byte pointer -- the generated SetStateBagValue(string,...) would corrupt
        // binary data.
        byte[] payload = Msgpack.EncodeValue(value);
        nint bag = Marshal.StringToCoTaskMemUTF8(_bagName ?? "");
        nint k = Marshal.StringToCoTaskMemUTF8(key ?? "");
        try
        {
            fixed (byte* p = payload)
            {
                Span<nuint> a = stackalloc nuint[5];
                a[0] = (nuint)bag;
                a[1] = (nuint)k;
                a[2] = (nuint)p;
                a[3] = (nuint)payload.Length;
                a[4] = replicated ? 1u : 0u;
                global::Flash.Native.Invoke(0x8D50E33AUL, a); // SET_STATE_BAG_VALUE
            }
        }
        finally { Marshal.FreeCoTaskMem(bag); Marshal.FreeCoTaskMem(k); }
    }

    /// <summary>Reads a value (server-side) — or null if not set.</summary>
    public unsafe object? Get(string key)
    {
        ThreadGuard.AssertScriptThread("StateBag.Get"); // native GET_STATE_BAG_VALUE (#209)
        nint bag = Marshal.StringToCoTaskMemUTF8(_bagName ?? "");
        nint k = Marshal.StringToCoTaskMemUTF8(key ?? "");
        try
        {
            Span<nuint> a = stackalloc nuint[2];
            a[0] = (nuint)bag;
            a[1] = (nuint)k;
            byte[]? raw = global::Flash.Native.InvokeObject(0x637F4C75UL, a); // GET_STATE_BAG_VALUE
            if (raw == null) return null;
            // Guard the decode: a client-owned bag (player/entity state) can be replicated with
            // MALFORMED msgpack by a cheater (mod menu) when strict mode is off. An unguarded
            // decode would throw (EOF/bad header) straight into the calling resource and crash
            // it -- a remote DoS. Treat a bad payload as "absent" and log it. (#152)
            try { return Msgpack.DecodeValue(raw); }
            catch (Exception ex)
            {
                Log.Warn($"[SECURITY] malformed state-bag msgpack for '{_bagName}'/'{key}' -- ignored ({ex.Message}).");
                return null;
            }
        }
        finally { Marshal.FreeCoTaskMem(bag); Marshal.FreeCoTaskMem(k); }
    }

    /// <summary>
    /// Reads a value typed. Values cross the client/replication boundary as msgpack, which
    /// widens/narrows numbers (an <c>int</c> set here can come back as <c>long</c>/<c>double</c>),
    /// so a plain <c>value is T</c> check would wrongly return default for a numerically valid
    /// value. Coerce through <see cref="Args.To{T}"/> (same path as event/DB args) so
    /// <c>Get&lt;int&gt;</c>/<c>Get&lt;float&gt;</c>/nullable primitives work regardless of the
    /// wire type. Returns default if absent or not convertible. (#155)
    /// </summary>
    public T? Get<T>(string key) => Args.To<T>(Get(key));

    /// <summary>bag["key"] = value (always replicated) or var x = bag["key"].</summary>
    public object? this[string key]
    {
        get => Get(key);
        set => Set(key, value, replicated: true);
    }
}

/// <summary>
/// The global state bag (bag "global") — server-wide state replicated to all clients
/// (e.g. weather, time of day). NOT to be confused with Flash.State (the reactive
/// in-core store); GlobalState is FiveM's replicated state bag.
/// </summary>
public static class GlobalState
{
    private static readonly StateBag s_bag = new("global");

    /// <summary>Sets a global value replicated to all clients.</summary>
    public static void Set(string key, object? value, bool replicated = true)
        => s_bag.Set(key, value, replicated);

    /// <summary>Reads a global value (server-side).</summary>
    public static object? Get(string key) => s_bag.Get(key);

    /// <summary>Reads a global value typed.</summary>
    public static T? Get<T>(string key) => s_bag.Get<T>(key);
}
