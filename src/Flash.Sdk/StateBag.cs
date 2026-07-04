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

    /// <summary>Sets a value. replicated=true → replicated to clients.</summary>
    public unsafe void Set(string key, object? value, bool replicated = true)
    {
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

    /// <summary>Reads a value typed (default if not set or the type does not match).</summary>
    public T? Get<T>(string key)
    {
        object? v = Get(key);
        return v is T t ? t : default;
    }

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
