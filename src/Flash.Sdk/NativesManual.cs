using System;
using System.Runtime.InteropServices;

namespace Flash.Natives;

// =====================================================================================
//  Hand-written natives -- the few the generator (1 param = 1 slot) cannot express:
//  object-by-value params (= 2 slots: msgpack bytes ptr + length) and Vector3-by-value
//  params (= 3 float slots). They live in the SAME Flash.Natives.Cfx class (partial)
//  as the generated ones -> no difference for users.
//
//  ABI verified against the FiveM source tree: object param via _obj(v) -> (bytes, len)
//  as 2 args; Vector3 param as 3 consecutive float args; object return = scrObject
//  (InvokeObject + msgpack decode). This makes native coverage effectively 100%.
// =====================================================================================
public static unsafe partial class Cfx
{
    // Float -> 32-bit slot (like the generator: packFloat).
    private static nuint F(float f) => (nuint)BitConverter.SingleToUInt32Bits(f);

    /// <summary>Returns entities (1=Ped, 2=Vehicle, 3=Object) within the radius.
    /// <paramref name="models"/> optionally filters by model hashes (null = all).
    /// Returns a list of entity handles.</summary>
    public static object? GetEntitiesInRadius(float x, float y, float z, float radius,
        int entityType, bool sortByDistance, object? models = null)
    {
        byte[] mb = global::Flash.Msgpack.EncodeValue(models); // null -> msgpack nil
        fixed (byte* mp = mb)
        {
            Span<nuint> a = stackalloc nuint[8];
            a[0] = F(x); a[1] = F(y); a[2] = F(z); a[3] = F(radius);
            a[4] = (nuint)(uint)entityType;
            a[5] = (nuint)(sortByDistance ? 1u : 0u);
            a[6] = (nuint)mp; a[7] = (nuint)mb.Length; // object param = 2 slots
            var b = global::Flash.Native.InvokeObject(0xDFFBA12FUL, a);
            return b == null ? null : global::Flash.Msgpack.DecodeValue(b);
        }
    }

    /// <summary>Track nodes (+ track ids) near the position.</summary>
    public static object? GetClosestTrackNodes(global::Flash.Vector3 position, float radius)
    {
        Span<nuint> a = stackalloc nuint[4];
        a[0] = F(position.X); a[1] = F(position.Y); a[2] = F(position.Z); a[3] = F(radius);
        var b = global::Flash.Native.InvokeObject(0x59FC24A7UL, a);
        return b == null ? null : global::Flash.Msgpack.DecodeValue(b);
    }

    /// <summary>Handling override for ONE vehicle (vector field, e.g. CentreOfMassOffset).</summary>
    public static void SetVehicleHandlingVector(global::Flash.Vehicle vehicle, string handlingClass,
        string fieldName, global::Flash.Vector3 value)
    {
        nint c = Marshal.StringToCoTaskMemUTF8(handlingClass);
        nint f = Marshal.StringToCoTaskMemUTF8(fieldName);
        try
        {
            Span<nuint> a = stackalloc nuint[6];
            a[0] = (nuint)(uint)vehicle.Value;
            a[1] = (nuint)c; a[2] = (nuint)f;
            a[3] = F(value.X); a[4] = F(value.Y); a[5] = F(value.Z); // Vector3 = 3 slots
            global::Flash.Native.Invoke(0x12497890UL, a);
        }
        finally { Marshal.FreeCoTaskMem(c); Marshal.FreeCoTaskMem(f); }
    }

    /// <summary>Global handling override for a vehicle class (vector field).</summary>
    public static void SetHandlingVector(string vehicleName, string handlingClass,
        string fieldName, global::Flash.Vector3 value)
    {
        nint v = Marshal.StringToCoTaskMemUTF8(vehicleName);
        nint c = Marshal.StringToCoTaskMemUTF8(handlingClass);
        nint f = Marshal.StringToCoTaskMemUTF8(fieldName);
        try
        {
            Span<nuint> a = stackalloc nuint[6];
            a[0] = (nuint)v; a[1] = (nuint)c; a[2] = (nuint)f;
            a[3] = F(value.X); a[4] = F(value.Y); a[5] = F(value.Z);
            global::Flash.Native.Invoke(0x7F9D543UL, a);
        }
        finally { Marshal.FreeCoTaskMem(v); Marshal.FreeCoTaskMem(c); Marshal.FreeCoTaskMem(f); }
    }

    /// <summary>HTTP request (msgpack variant). Note: usually prefer <c>Flash.Http</c>.</summary>
    public static int PerformHttpRequestInternalEx(object? requestData)
    {
        byte[] rb = global::Flash.Msgpack.EncodeValue(requestData);
        fixed (byte* rp = rb)
        {
            Span<nuint> a = stackalloc nuint[2];
            a[0] = (nuint)rp; a[1] = (nuint)rb.Length;
            return unchecked((int)global::Flash.Native.Invoke(0x6B171E87UL, a));
        }
    }

    /// <summary>Formats a stack trace object into a string (internal/debug).</summary>
    public static string? FormatStackTrace(object? traceData)
    {
        byte[] tb = global::Flash.Msgpack.EncodeValue(traceData);
        fixed (byte* tp = tb)
        {
            Span<nuint> a = stackalloc nuint[2];
            a[0] = (nuint)tp; a[1] = (nuint)tb.Length;
            ulong r = global::Flash.Native.Invoke(0xD70C3BCAUL, a);
            return Marshal.PtrToStringUTF8((nint)r);
        }
    }

    /// <summary>Transiently updates a mapdata entity (SDK infrastructure, rarely used directly).</summary>
    public static void UpdateMapdataEntity(int mapdata, int entity, object? entityDef)
    {
        byte[] eb = global::Flash.Msgpack.EncodeValue(entityDef);
        fixed (byte* ep = eb)
        {
            Span<nuint> a = stackalloc nuint[4];
            a[0] = (nuint)(uint)mapdata; a[1] = (nuint)(uint)entity;
            a[2] = (nuint)ep; a[3] = (nuint)eb.Length;
            global::Flash.Native.Invoke(0xFC52CB91UL, a);
        }
    }
}
