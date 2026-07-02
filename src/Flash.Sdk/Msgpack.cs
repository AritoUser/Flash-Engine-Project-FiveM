using System;
using System.Collections.Generic;
using System.Text;

namespace Flash;

/// <summary>
/// Minimal, INTEROP-correct MessagePack codec for event arguments.
///
/// WHY a custom codec (instead of NuGet): FiveM's event bus serializes arguments as a
/// msgpack array. For Flash resources to talk to lua/v8/mono resources AND the network,
/// our format MUST match the standard byte for byte. We keep the public SDK's
/// dependencies slim and the path traceable → a small, well-tested implementation of
/// the common types (nil/bool/int/float/double/string/array/map).
///
/// Covered: what FiveM events actually use. bin/ext decode to null (they practically
/// never occur in event args) — except funcref ext types, which decode to Flash.Funcref.
/// Encode uses the most compact form per value.
/// </summary>
internal static class Msgpack
{
    // === Encode =============================================================

    /// <summary>Serializes the argument list as ONE msgpack array (as the bus expects).</summary>
    public static byte[] EncodeArray(object?[] items)
    {
        var b = new List<byte>(64);
        WriteArrayHeader(b, items.Length);
        foreach (var it in items) WriteValue(b, it);
        return b.ToArray();
    }

    /// <summary>Serializes ONE value (no wrapping array) — e.g. for state bags.</summary>
    public static byte[] EncodeValue(object? value)
    {
        var b = new List<byte>(32);
        WriteValue(b, value);
        return b.ToArray();
    }

    /// <summary>Reads ONE msgpack value (counterpart to EncodeValue).</summary>
    public static object? DecodeValue(byte[] data)
    {
        if (data.Length == 0) return null;
        int i = 0;
        return ReadValue(data, ref i);
    }

    private static void WriteValue(List<byte> b, object? v)
    {
        switch (v)
        {
            case null: b.Add(0xc0); break;
            case bool x: b.Add(x ? (byte)0xc3 : (byte)0xc2); break;
            case string s: WriteString(b, s); break;
            case float f: b.Add(0xca); WriteU32(b, BitConverter.SingleToUInt32Bits(f)); break;
            case double d: b.Add(0xcb); WriteU64(b, BitConverter.DoubleToUInt64Bits(d)); break;
            // Unify all common integer types via long.
            case sbyte or short or int or long or byte or ushort or uint:
                WriteInt(b, Convert.ToInt64(v));
                break;
            // ulong above long.MaxValue doesn't fit int64 -> encode explicitly as uint64
            // (0xcf), otherwise the value would arrive as a negative number.
            case ulong u when u <= long.MaxValue: WriteInt(b, (long)u); break;
            case ulong u: b.Add(0xcf); WriteU64(b, u); break;
            case object?[] arr:
                WriteArrayHeader(b, arr.Length);
                foreach (var e in arr) WriteValue(b, e);
                break;
            case IDictionary<string, object?> map:
                WriteMapHeader(b, map.Count);
                foreach (var kv in map) { WriteString(b, kv.Key); WriteValue(b, kv.Value); }
                break;
            default: WriteString(b, v.ToString() ?? ""); break; // fallback: as string
        }
    }

    private static void WriteInt(List<byte> b, long n)
    {
        if (n >= 0)
        {
            if (n < 0x80) b.Add((byte)n);                                   // positive fixint
            else if (n <= 0xff) { b.Add(0xcc); b.Add((byte)n); }            // uint8
            else if (n <= 0xffff) { b.Add(0xcd); WriteU16(b, (ushort)n); }  // uint16
            else if (n <= 0xffffffff) { b.Add(0xce); WriteU32(b, (uint)n); }// uint32
            else { b.Add(0xcf); WriteU64(b, (ulong)n); }                    // uint64
        }
        else
        {
            if (n >= -32) b.Add((byte)n);                                   // negative fixint
            else if (n >= -128) { b.Add(0xd0); b.Add((byte)n); }            // int8
            else if (n >= -32768) { b.Add(0xd1); WriteU16(b, (ushort)(short)n); } // int16
            else if (n >= -2147483648) { b.Add(0xd2); WriteU32(b, (uint)(int)n); } // int32
            else { b.Add(0xd3); WriteU64(b, (ulong)n); }                    // int64
        }
    }

    private static void WriteString(List<byte> b, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        int n = bytes.Length;
        if (n < 32) b.Add((byte)(0xa0 | n));                       // fixstr
        else if (n <= 0xff) { b.Add(0xd9); b.Add((byte)n); }       // str8
        else if (n <= 0xffff) { b.Add(0xda); WriteU16(b, (ushort)n); } // str16
        else { b.Add(0xdb); WriteU32(b, (uint)n); }               // str32
        b.AddRange(bytes);
    }

    private static void WriteArrayHeader(List<byte> b, int n)
    {
        if (n < 16) b.Add((byte)(0x90 | n));
        else if (n <= 0xffff) { b.Add(0xdc); WriteU16(b, (ushort)n); }
        else { b.Add(0xdd); WriteU32(b, (uint)n); }
    }

    private static void WriteMapHeader(List<byte> b, int n)
    {
        if (n < 16) b.Add((byte)(0x80 | n));
        else if (n <= 0xffff) { b.Add(0xde); WriteU16(b, (ushort)n); }
        else { b.Add(0xdf); WriteU32(b, (uint)n); }
    }

    // msgpack is BIG-ENDIAN (network byte order).
    private static void WriteU16(List<byte> b, ushort v) { b.Add((byte)(v >> 8)); b.Add((byte)v); }
    private static void WriteU32(List<byte> b, uint v) { b.Add((byte)(v >> 24)); b.Add((byte)(v >> 16)); b.Add((byte)(v >> 8)); b.Add((byte)v); }
    private static void WriteU64(List<byte> b, ulong v) { for (int s = 56; s >= 0; s -= 8) b.Add((byte)(v >> s)); }

    // === Decode =============================================================

    /// <summary>Reads a msgpack array (the event arguments). Non-array at the root →
    /// treated as a single-element list; empty payload → no arguments.</summary>
    public static object?[] DecodeArray(byte[] data)
    {
        if (data.Length == 0) return Array.Empty<object?>();
        int i = 0;
        object? v = ReadValue(data, ref i);
        if (v is object?[] arr) return arr;
        return v == null ? Array.Empty<object?>() : new[] { v };
    }

    private static object? ReadValue(byte[] d, ref int i)
    {
        byte c = d[i++];
        if (c <= 0x7f) return (long)c;                       // positive fixint
        if (c >= 0xe0) return (long)(sbyte)c;                // negative fixint
        if (c <= 0x8f) return ReadMap(d, ref i, c & 0x0f);   // fixmap
        if (c <= 0x9f) return ReadArray(d, ref i, c & 0x0f); // fixarray
        if (c <= 0xbf) return ReadStr(d, ref i, c & 0x1f);   // fixstr

        switch (c)
        {
            case 0xc0: return null;
            case 0xc2: return false;
            case 0xc3: return true;
            case 0xca: return BitConverter.UInt32BitsToSingle(ReadU32(d, ref i)); // float32
            case 0xcb: return BitConverter.UInt64BitsToDouble(ReadU64(d, ref i)); // float64
            case 0xcc: return (long)d[i++];                                  // uint8
            case 0xcd: return (long)ReadU16(d, ref i);                       // uint16
            case 0xce: return (long)ReadU32(d, ref i);                       // uint32
            case 0xcf: return (long)ReadU64(d, ref i);                       // uint64
            case 0xd0: return (long)(sbyte)d[i++];                           // int8
            case 0xd1: return (long)(short)ReadU16(d, ref i);                // int16
            case 0xd2: return (long)(int)ReadU32(d, ref i);                  // int32
            case 0xd3: return (long)ReadU64(d, ref i);                       // int64
            case 0xd9: return ReadStr(d, ref i, d[i++]);                     // str8
            case 0xda: return ReadStr(d, ref i, ReadU16(d, ref i));          // str16
            case 0xdb: return ReadStr(d, ref i, (int)ReadU32(d, ref i));     // str32
            case 0xdc: return ReadArray(d, ref i, ReadU16(d, ref i));        // array16
            case 0xdd: return ReadArray(d, ref i, (int)ReadU32(d, ref i));   // array32
            case 0xde: return ReadMap(d, ref i, ReadU16(d, ref i));          // map16
            case 0xdf: return ReadMap(d, ref i, (int)ReadU32(d, ref i));     // map32
            // Ext types: fixext1/2/4/8/16 + ext8/16/32. Relevant for us = funcref
            // (type 10 remote / 11 local) -> Flash.Funcref; other ext types -> null.
            case 0xd4: return ReadExt(d, ref i, 1);
            case 0xd5: return ReadExt(d, ref i, 2);
            case 0xd6: return ReadExt(d, ref i, 4);
            case 0xd7: return ReadExt(d, ref i, 8);
            case 0xd8: return ReadExt(d, ref i, 16);
            case 0xc7: { int len = d[i++]; return ReadExt(d, ref i, len); }
            case 0xc8: { int len = ReadU16(d, ref i); return ReadExt(d, ref i, len); }
            case 0xc9: { int len = (int)ReadU32(d, ref i); return ReadExt(d, ref i, len); }
            default: return null; // bin etc. -> not expected in event args
        }
    }

    // Ext block: type byte + len data. Funcref (type 10/11) -> Flash.Funcref (ref string
    // = UTF-8 of the payload), otherwise null (skip the payload).
    private static object? ReadExt(byte[] d, ref int i, int len)
    {
        byte type = d[i++];
        object? result = (type == 10 || type == 11) ? new Funcref(Encoding.UTF8.GetString(d, i, len)) : null;
        i += len;
        return result;
    }

    private static string ReadStr(byte[] d, ref int i, int n)
    {
        string s = Encoding.UTF8.GetString(d, i, n);
        i += n;
        return s;
    }

    private static object?[] ReadArray(byte[] d, ref int i, int n)
    {
        var arr = new object?[n];
        for (int k = 0; k < n; k++) arr[k] = ReadValue(d, ref i);
        return arr;
    }

    private static Dictionary<string, object?> ReadMap(byte[] d, ref int i, int n)
    {
        var map = new Dictionary<string, object?>(n);
        for (int k = 0; k < n; k++)
        {
            object? key = ReadValue(d, ref i);
            object? val = ReadValue(d, ref i);
            map[key?.ToString() ?? ""] = val;
        }
        return map;
    }

    private static ushort ReadU16(byte[] d, ref int i) { ushort v = (ushort)((d[i] << 8) | d[i + 1]); i += 2; return v; }
    private static uint ReadU32(byte[] d, ref int i) { uint v = ((uint)d[i] << 24) | ((uint)d[i + 1] << 16) | ((uint)d[i + 2] << 8) | d[i + 3]; i += 4; return v; }
    private static ulong ReadU64(byte[] d, ref int i) { ulong v = 0; for (int k = 0; k < 8; k++) v = (v << 8) | d[i + k]; i += 8; return v; }
}
