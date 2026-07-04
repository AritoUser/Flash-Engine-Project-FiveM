namespace Flash;

// Distinct game handles -- the compiler prevents mixing up Ped and Vehicle.
public readonly record struct Player(int Value);
public readonly record struct Ped(int Value);
public readonly record struct Vehicle(int Value);
public readonly record struct Entity(int Value);
public readonly record struct Object(int Value);
public readonly record struct Blip(int Value);
public readonly record struct Cam(int Value);
public readonly record struct Pickup(int Value);
public readonly record struct ScrHandle(int Value);
public readonly record struct FireId(int Value);
public readonly record struct Interior(int Value);
public readonly record struct Hash(int Value);

public struct Vector3
{
    public float X;
    public float Y;
    public float Z;

    public Vector3(float x, float y, float z) { X = x; Y = y; Z = z; }

    // --- Math helpers (#35): the everyday spatial toolbox for scripts. ---------------

    public static Vector3 operator +(Vector3 a, Vector3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vector3 operator -(Vector3 a, Vector3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vector3 operator *(Vector3 a, float s) => new(a.X * s, a.Y * s, a.Z * s);
    public static Vector3 operator /(Vector3 a, float s) => new(a.X / s, a.Y / s, a.Z / s);

    /// <summary>Squared length — prefer this (or <see cref="DistanceSquared"/>) in loops:
    /// no square root.</summary>
    public readonly float LengthSquared() => X * X + Y * Y + Z * Z;

    public readonly float Length() => System.MathF.Sqrt(LengthSquared());

    public readonly float Distance(Vector3 other) => (this - other).Length();

    /// <summary>Squared distance — for range checks compare against radius*radius.</summary>
    public readonly float DistanceSquared(Vector3 other) => (this - other).LengthSquared();

    /// <summary>2D distance (X/Y only) — the usual choice for map ranges, ignores height.</summary>
    public readonly float Distance2D(Vector3 other)
    {
        float dx = X - other.X, dy = Y - other.Y;
        return System.MathF.Sqrt(dx * dx + dy * dy);
    }

    public readonly float Dot(Vector3 other) => X * other.X + Y * other.Y + Z * other.Z;

    /// <summary>Unit vector (the zero vector stays zero instead of NaN).</summary>
    public readonly Vector3 Normalized()
    {
        float len = Length();
        return len > 0f ? this / len : default;
    }

    public override readonly string ToString() => $"({X:0.##}, {Y:0.##}, {Z:0.##})";
}

// Internal buffer for Vector3* out params (FiveM scrVector: each float + 4 bytes pad).
internal struct ScrVector
{
    public float X;
    public uint _p0;
    public float Y;
    public uint _p1;
    public float Z;
    public uint _p2;
}

/// <summary>
/// Entry point of the generated natives into the core. InvokeNative is set from the
/// FlashApi at startup (see FlashBridge.Initialize).
/// </summary>
public static unsafe class Native
{
    internal static delegate* unmanaged<ulong, nuint*, int, ulong> InvokeNative;
    internal static delegate* unmanaged<ulong, nuint*, int, float*, void> InvokeNativeVec3;
    // Funcrefs: returns the canonical ref string for a local refIdx (into an out buffer).
    internal static delegate* unmanaged<int, byte*, int, void> CanonicalizeRefPtr;
    // Funcrefs: invokes a RECEIVED function reference (refId string) with msgpack args.
    internal static delegate* unmanaged<byte*, byte*, int, void> InvokeFunctionReferencePtr;

    /// <summary>Invokes a received function reference through the host (refId + msgpack args).</summary>
    internal static void InvokeFunctionRef(string refId, byte[] argsMsgpack)
    {
        nint id = System.Runtime.InteropServices.Marshal.StringToCoTaskMemUTF8(refId);
        try
        {
            fixed (byte* p = argsMsgpack)
                InvokeFunctionReferencePtr((byte*)id, p, argsMsgpack.Length);
        }
        finally { System.Runtime.InteropServices.Marshal.FreeCoTaskMem(id); }
    }

    /// <summary>Fetches the canonical funcref string for a local refIdx (from the host).</summary>
    internal static string Canonicalize(int refIdx)
    {
        byte* buf = stackalloc byte[512]; // "<resource>:<instanceId>:<refIdx>" is short
        CanonicalizeRefPtr(refIdx, buf, 512);
        return System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)buf) ?? "";
    }

    // Object return: a native returning scrObject{data,length} (out ptr + len).
    internal static delegate* unmanaged<ulong, nuint*, int, ulong*, ulong*, void> InvokeNativeObjectPtr;

    public static ulong Invoke(ulong hash, System.Span<nuint> args)
    {
        fixed (nuint* p = args)
        {
            return InvokeNative(hash, p, args.Length);
        }
    }

    /// <summary>Invokes a native that returns an object (msgpack) and returns the raw
    /// msgpack bytes (or null for an empty result). The caller decodes via Msgpack.</summary>
    internal static byte[]? InvokeObject(ulong hash, System.Span<nuint> args)
    {
        ulong ptr = 0, len = 0;
        fixed (nuint* p = args)
        {
            InvokeNativeObjectPtr(hash, p, args.Length, &ptr, &len);
        }
        if (ptr == 0 || len == 0) return null;
        var buf = new byte[(int)len];
        System.Runtime.InteropServices.Marshal.Copy((nint)ptr, buf, 0, (int)len);
        return buf;
    }

    public static Vector3 InvokeVec3(ulong hash, System.Span<nuint> args)
    {
        float* o = stackalloc float[3];
        fixed (nuint* p = args)
        {
            InvokeNativeVec3(hash, p, args.Length, o);
        }
        return new Vector3 { X = o[0], Y = o[1], Z = o[2] };
    }
}
