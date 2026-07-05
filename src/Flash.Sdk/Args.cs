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
        // uint SEPARATELY: can be > int.MaxValue (MySQL INT UNSIGNED via MySqlConnector) ->
        // Convert.ToInt32 would throw and break the "never throws" contract (#85).
        uint u => u <= int.MaxValue ? (int)u : def,
        byte or sbyte or short or ushort => Convert.ToInt32(v), // always fit in int32
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
        => i >= 0 && i < args.Length ? Float(args[i], def) : def;

    /// <summary>Single value as float; default on invalid (NaN/Infinity count as invalid).</summary>
    public static float Float(object? v, float def = 0f)
    {
        float f = v switch
        {
            float x => x,
            double x => (float)x,
            int x => x,
            long x => x,
            decimal m => (float)m,
            string s => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? p : def,
            _ => def,
        };
        return float.IsNaN(f) || float.IsInfinity(f) ? def : f;
    }

    /// <summary>
    /// Single value as <typeparamref name="T"/> with safe numeric coercion — msgpack
    /// and DB layers deliver numbers as long/double/decimal; a plain cast to int/float
    /// would throw or silently fail. Non-convertible values yield default.
    /// </summary>
    public static T? To<T>(object? v)
    {
        if (v is T t) return t;
        // A null input maps to default(T): null for a nullable/reference T, 0/false for a
        // non-nullable primitive (unchanged) -- do NOT coerce null through Int()/Float() to 0,
        // which would wrongly turn To<int?>(null) into 0 instead of null.
        if (v is null) return default;
        // Resolve Nullable<T> to its underlying type: a caller asking for int?/double?/... must
        // still get numbers coerced (msgpack/DB deliver them as long/double). Without this none
        // of the exact-type checks matched a nullable T, so the method returned null (#153).
        Type target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        object? boxed = ToType(v, target);
        return boxed is T c ? c : default;
    }

    /// <summary>
    /// Non-generic sibling of <see cref="To{T}"/>: coerce <paramref name="v"/> to
    /// <paramref name="target"/> with the same safe numeric semantics. Used where the
    /// destination type is only known at runtime (reflection-based DB row mapping,
    /// typed state-bag reads). Returns null for a null input or a non-convertible value
    /// (callers treat that as "leave the default / absent"). Never throws.
    /// </summary>
    internal static object? ToType(object? v, Type target)
    {
        if (v is null) return null;
        // Nullable<T> destinations coerce to the underlying value type (a boxed int is a
        // valid value for an int? property/field).
        target = Nullable.GetUnderlyingType(target) ?? target;
        // Already the right type (or assignable, e.g. a stored POCO/reference) -> pass through.
        if (target.IsInstanceOfType(v)) return v;
        if (target == typeof(int)) return Int(v);
        if (target == typeof(long)) return Long(v);
        if (target == typeof(bool)) return Long(v) != 0;
        if (target == typeof(float)) return Float(v);
        // double loss-free when the source really was double (Float() would clip it otherwise).
        if (target == typeof(double)) return v is double d ? d : (double)Float(v);
        if (target == typeof(string)) return v.ToString();
        if (target.IsEnum)
        {
            try { return v is string es ? Enum.Parse(target, es, ignoreCase: true) : Enum.ToObject(target, Long(v)); }
            catch { return null; }
        }
        // Everything else (decimal, DateTime, short/byte, Guid via string, ...) through the
        // framework converter, invariant culture. A failed conversion is swallowed to null
        // to keep the "never throws" contract.
        try { return System.Convert.ChangeType(v, target, CultureInfo.InvariantCulture); }
        catch { return null; }
    }

    /// <summary>args[i] as string; default on missing/null.</summary>
    public static string Str(object?[] args, int i, string def = "")
        => i >= 0 && i < args.Length ? args[i]?.ToString() ?? def : def;

    /// <summary>
    /// args[i] as a VALIDATED string: returns the value only if it is at most
    /// <paramref name="maxLength"/> characters and (when given) fully matches the allow-list
    /// <paramref name="pattern"/>; otherwise <paramref name="def"/>. Use at the RPC/event
    /// boundary for user-generated content (names, plates, reasons) to reject injection
    /// payloads before they are stored or replicated to other players' UI. Never throws;
    /// a pathological pattern/input times out and is rejected. (#65)
    /// </summary>
    public static string StrRegex(object?[] args, int i, int maxLength = int.MaxValue,
        string? pattern = null, string def = "")
    {
        string s = Str(args, i, def);
        if (s.Length > maxLength) return def;
        if (pattern != null && !Security.IsMatch(s, pattern)) return def;
        return s;
    }

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
