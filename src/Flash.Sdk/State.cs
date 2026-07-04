using System.Runtime.InteropServices;
using System.Text.Json;

namespace Flash;

/// <summary>
/// Reactive state store — the core is the source of truth. Set/Get go through the
/// contract into the core; OnChange registers a handler the core calls back with the
/// key on every change (the handler then reads the value via GetInt/GetFloat/GetBool).
/// Deliberately key-only notify → slim contract, no value marshalling on the callback
/// path.
///
/// TYPED VALUES (why):
///   The core stores (tag, num) per key: 64 raw bits plus a type tag. Only HERE, in the
///   public C# API, does the value get its type back — the bits are read as long /
///   double / bool depending on the tag. This keeps the contract minimal (one set, one
///   get for all scalars) and the core type-agnostic. Strings + tables have their own
///   paths (pointer/lifetime and structure respectively).
/// </summary>
public static unsafe class State
{
    // Type tags. Must match the core exactly.
    private const byte TagAbsent = 0;
    private const byte TagInt = 1;
    private const byte TagFloat = 2;
    private const byte TagBool = 3;
    private const byte TagString = 4;
    private const byte TagBytes = 5;

    // Wired by FlashBridge.Initialize from the FlashApi (core functions).
    // Set_(key, tag, num); Get_(key, out num) -> tag (0=absent).
    internal static delegate* unmanaged<byte*, byte, ulong, void> Set_;
    internal static delegate* unmanaged<byte*, ulong*, byte> Get_;
    internal static delegate* unmanaged<delegate* unmanaged<byte*, void>, void> Subscribe_;
    // String path (v5): SetStr_(key, value); GetStr_(key) -> byte* (or null).
    internal static delegate* unmanaged<byte*, byte*, void> SetStr_;
    internal static delegate* unmanaged<byte*, byte*> GetStr_;
    // Byte path (v6): SetBytes_(key, ptr, len); GetBytes_(key, out len) -> byte* (null).
    internal static delegate* unmanaged<byte*, byte*, nuint, void> SetBytes_;
    internal static delegate* unmanaged<byte*, nuint*, byte*> GetBytes_;
    // Delete path (v12): remove a key entirely (the core frees the payload).
    internal static delegate* unmanaged<byte*, void> Delete_;

    // Change handlers partitioned PER RESOURCE (key = resource name). Why not a single
    // static event: the SDK lives in the shared Default ALC, so a static delegate would
    // survive resource unload -> it pins the collectible ALC and keeps running deleted
    // code on every state change. ClearResource drops a resource's handlers on unload,
    // exactly like Events/Exports/Rpc. (#83)
    private static readonly System.Collections.Generic.Dictionary<string,
        System.Collections.Generic.List<Action<string>>> s_onChange = new();
    private static bool s_subscribed;

    // --- Setters (typed) -----------------------------------------------------
    // Each type is packed losslessly into the 64 bits: long directly (bit cast), double
    // via its IEEE bits, bool as 0/1. The tag remembers the type.

    public static void SetInt(string key, long value)
        => SetRaw(key, TagInt, unchecked((ulong)value));

    public static void SetFloat(string key, double value)
        => SetRaw(key, TagFloat, BitConverter.DoubleToUInt64Bits(value));

    public static void SetBool(string key, bool value)
        => SetRaw(key, TagBool, value ? 1UL : 0UL);

    /// <summary>Removes a key entirely (whatever its type). Subscribers are notified;
    /// getters then return their default (0 / false / null). No-op if the key does not
    /// exist.</summary>
    public static void Delete(string key)
    {
        nint k = Utf8(key);
        try { Delete_((byte*)k); }
        finally { Marshal.FreeCoTaskMem(k); }
    }

    // --- Getters (typed) -----------------------------------------------------
    // Each getter checks the tag: if the stored type doesn't match (or the key is
    // missing), it returns the default. Deliberately tolerant instead of throwing —
    // resources should handle not-yet-set keys gracefully.

    public static long GetInt(string key)
        => GetRaw(key, out ulong num) == TagInt ? unchecked((long)num) : 0L;

    public static double GetFloat(string key)
        => GetRaw(key, out ulong num) == TagFloat ? BitConverter.UInt64BitsToDouble(num) : 0.0;

    public static bool GetBool(string key)
        => GetRaw(key, out ulong num) == TagBool && num != 0UL;

    // --- Strings (own path: pointer instead of 64 bits) ----------------------
    // Strings don't fit into num. Set marshals key AND value as UTF-8 across the
    // boundary; the core copies the value (owns it afterwards), then we free both
    // buffers.

    public static void SetString(string key, string value)
    {
        nint k = Utf8(key);
        nint v = Utf8(value);
        try { SetStr_((byte*)k, (byte*)v); }
        finally
        {
            Marshal.FreeCoTaskMem(k);
            Marshal.FreeCoTaskMem(v);
        }
    }

    // Get returns a pointer to the CORE-owned copy. We copy it IMMEDIATELY
    // (synchronously, before anything could overwrite the key) into a managed string.
    // null = key missing or not a string.
    public static string? GetString(string key)
    {
        nint k = Utf8(key);
        try
        {
            byte* p = GetStr_((byte*)k);
            return p == null ? null : Marshal.PtrToStringUTF8((nint)p);
        }
        finally { Marshal.FreeCoTaskMem(k); }
    }

    // --- Bytes + tables ------------------------------------------------------
    // Bytes are the substrate for structured values. SetTable/GetTable serialize any
    // object to UTF-8 JSON and store the bytes in the core — the core stays
    // type-agnostic (doesn't know the shape), the shape lives here in the SDK.
    // Note: JsonSerializer uses reflection -> fits CoreCLR (JIT). A future NativeAOT SDK
    // would need a JsonSerializerContext (source gen) instead.

    public static void SetTable<T>(string key, T value)
        => SetBytes(key, JsonSerializer.SerializeToUtf8Bytes(value));

    public static T? GetTable<T>(string key)
    {
        byte[]? json = GetBytes(key);
        return json == null ? default : JsonSerializer.Deserialize<T>(json);
    }

    // Raw bytes: marshal the key, pass the payload straight from the managed span to the
    // core (which copies it). 'fixed' pins the span for the duration of the call.
    public static void SetBytes(string key, ReadOnlySpan<byte> value)
    {
        nint k = Utf8(key);
        try
        {
            fixed (byte* p = value)
                SetBytes_((byte*)k, p, (nuint)value.Length);
        }
        finally { Marshal.FreeCoTaskMem(k); }
    }

    // Get returns pointer + length to the CORE-owned copy -> copy IMMEDIATELY,
    // synchronously, into a managed byte[]. null = key missing or not a byte value.
    public static byte[]? GetBytes(string key)
    {
        nint k = Utf8(key);
        try
        {
            nuint len;
            byte* p = GetBytes_((byte*)k, &len);
            if (p == null) return null;
            var result = new byte[(int)len];
            new ReadOnlySpan<byte>(p, (int)len).CopyTo(result);
            return result;
        }
        finally { Marshal.FreeCoTaskMem(k); }
    }

    // --- shared marshalling path ---------------------------------------------
    // Marshal the key once as null-terminated UTF-8 across the boundary, then free the
    // buffer. Centralized here so every setter/getter marshals identically (and
    // leak-free).

    // Null-safe UTF-8 marshalling: a null key/value would become IntPtr.Zero and the
    // non-nullable native signature would dereference it -> segfault. Coerce to "". (#95)
    private static nint Utf8(string? s) => Marshal.StringToCoTaskMemUTF8(s ?? "");

    private static void SetRaw(string key, byte tag, ulong num)
    {
        nint k = Utf8(key);
        try { Set_((byte*)k, tag, num); }
        finally { Marshal.FreeCoTaskMem(k); }
    }

    private static byte GetRaw(string key, out ulong num)
    {
        nint k = Utf8(key);
        try
        {
            ulong local;
            byte tag = Get_((byte*)k, &local);
            num = local;
            return tag;
        }
        finally { Marshal.FreeCoTaskMem(k); }
    }

    /// <summary>Registers a change handler (key-only; the handler reads the value typed
    /// via Get*). NOTE: handlers run SYNCHRONOUSLY on the setter's thread — if state is
    /// set from a thread-pool thread (async), the handler runs there too. Only read/pass
    /// values along in the handler; don't fire natives.</summary>
    public static void OnChange(Action<string> handler)
    {
        string res = global::Flash.Natives.Cfx.GetCurrentResourceName() ?? "";
        if (!s_onChange.TryGetValue(res, out var list))
        {
            list = new System.Collections.Generic.List<Action<string>>();
            s_onChange[res] = list;
        }
        list.Add(handler);
        // Register the one native callback with the core only on the FIRST subscriber.
        if (!s_subscribed)
        {
            Subscribe_(&Trampoline);
            s_subscribed = true;
        }
    }

    /// <summary>On resource stop: drop the resource's change handlers so their delegates
    /// don't pin the collectible ALC (called by the host). (#83)</summary>
    internal static void ClearResource(string resource) => s_onChange.Remove(resource);

    // The single native entry point the core calls on changes. Converts the UTF-8 key to
    // a string and fans out to every resource's handlers (each isolated -- a throwing
    // handler must not stop the others or the setter).
    [UnmanagedCallersOnly]
    private static void Trampoline(byte* key)
    {
        string? k = Marshal.PtrToStringUTF8((nint)key);
        if (k == null) return;
        foreach (var list in s_onChange.Values)
            foreach (var h in list.ToArray())
            {
                try { h(k); }
                catch (System.Exception ex) { Log.Error($"State.OnChange handler threw: {ex.Message}"); }
            }
    }
}
