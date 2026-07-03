using System;
using System.Globalization;

namespace Flash;

/// <summary>
/// Safe accessors for untrusted argument arrays (event payloads, export args,
/// command args). Msgpack delivers numbers as long/ulong/double -- a malicious
/// client can send values far outside int range, and a naive
/// <c>Convert.ToInt32(args[0])</c> then throws OverflowException inside the
/// handler. These helpers NEVER throw: missing index, wrong type or an
/// out-of-range value yield the given default (0/""), which downstream guards
/// reject cleanly (negative/zero amounts, unknown ids).
/// </summary>
public static class Args
{
    /// <summary>args[i] as int; default on missing/invalid/out-of-range.</summary>
    public static int Int(object?[] args, int i, int def = 0)
        => i >= 0 && i < args.Length ? Int(args[i], def) : def;

    /// <summary>Single value as int; default on invalid/out-of-range.</summary>
    public static int Int(object? v, int def = 0) => v switch
    {
        int n => n,
        long n => n >= int.MinValue && n <= int.MaxValue ? (int)n : def,
        ulong n => n <= int.MaxValue ? (int)n : def,
        double d => d >= int.MinValue && d <= int.MaxValue && !double.IsNaN(d) ? (int)d : def,
        float f => f >= int.MinValue && f <= int.MaxValue && !float.IsNaN(f) ? (int)f : def,
        bool b => b ? 1 : 0,
        string s => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : def,
        decimal m => m >= int.MinValue && m <= int.MaxValue ? (int)m : def, // DB-Scalars (MySQL DECIMAL)
        byte or sbyte or short or ushort or uint => Convert.ToInt32(v),
        _ => def,
    };

    /// <summary>args[i] as long; default on missing/invalid/out-of-range.</summary>
    public static long Long(object?[] args, int i, long def = 0)
        => i >= 0 && i < args.Length ? Long(args[i], def) : def;

    /// <summary>Single value as long; default on invalid/out-of-range.</summary>
    public static long Long(object? v, long def = 0) => v switch
    {
        long n => n,
        int n => n,
        ulong n => n <= long.MaxValue ? (long)n : def,
        double d => d >= long.MinValue && d <= long.MaxValue && !double.IsNaN(d) ? (long)d : def,
        float f => f >= long.MinValue && f <= long.MaxValue && !float.IsNaN(f) ? (long)f : def,
        bool b => b ? 1L : 0L,
        string s => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : def,
        decimal m => m >= long.MinValue && m <= long.MaxValue ? (long)m : def,
        byte or sbyte or short or ushort or uint => Convert.ToInt64(v),
        _ => def,
    };

    /// <summary>args[i] as float; default on missing/invalid (NaN/Infinity count as invalid).</summary>
    public static float Float(object?[] args, int i, float def = 0f)
    {
        if (i < 0 || i >= args.Length) return def;
        float f = args[i] switch
        {
            float x => x,
            double x => (float)x,
            int x => x,
            long x => x,
            string s => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? p : def,
            _ => def,
        };
        return float.IsNaN(f) || float.IsInfinity(f) ? def : f;
    }

    /// <summary>args[i] as string; default on missing/null.</summary>
    public static string Str(object?[] args, int i, string def = "")
        => i >= 0 && i < args.Length ? args[i]?.ToString() ?? def : def;

    /// <summary>args[i] as bool; default on missing/invalid.</summary>
    public static bool Bool(object?[] args, int i, bool def = false)
    {
        if (i < 0 || i >= args.Length) return def;
        return args[i] switch
        {
            bool b => b,
            int n => n != 0,
            long n => n != 0,
            string s => bool.TryParse(s, out var p) ? p : def,
            _ => def,
        };
    }
}
