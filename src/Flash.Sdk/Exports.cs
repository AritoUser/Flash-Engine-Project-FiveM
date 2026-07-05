using System;
using System.Collections.Generic;

namespace Flash;

/// <summary>
/// Exports: a resource provides functions that OTHER resources can call synchronously
/// — including a return value (unlike events, which are fire-and-forget).
///
/// CURRENT SCOPE: C#&lt;-&gt;C# (between Flash resources), direct + synchronous. Interop
/// with LUA/JS resources (FiveM's __cfx_export_* protocol) is a later step — it needs
/// the full funcref system (encoding local funcrefs + call return values).
/// </summary>
public static partial class Exports
{
    // Per resource (key = resource name) → per export name → handler.
    // Partitioned per resource (like Events) → cleanly removable on unload; identity
    // via GetCurrentResourceName at register time (correct because the active runtime is set).
    private static readonly Dictionary<string, Dictionary<string, Func<object?[], object?>>> s_exports = new();

    /// <summary>Provides an export with a return value.</summary>
    public static void Register(string name, Func<object?[], object?> handler)
    {
        string res = global::Flash.Natives.Cfx.GetCurrentResourceName() ?? "";
        if (!s_exports.TryGetValue(res, out var byName))
        {
            byName = new Dictionary<string, Func<object?[], object?>>();
            s_exports[res] = byName;
        }
        byName[name] = handler;
    }

    /// <summary>Provides an export without a return value.</summary>
    public static void Register(string name, Action<object?[]> handler)
        => Register(name, args => { handler(args); return null; });

    // --- Strongly typed overloads (#10): no manual object?[] indexing/casting. -------
    // Arguments are coerced via Args.To<T> (numbers may arrive as long/double when the
    // caller got them from msgpack payloads) -- absurd values become default instead
    // of throwing inside the export.

    /// <summary>Typed export without parameters.</summary>
    public static void Register<TResult>(string name, Func<TResult> handler)
        => Register(name, _ => handler());

    /// <summary>Typed export with 1 parameter.</summary>
    public static void Register<T1, TResult>(string name, Func<T1?, TResult> handler)
        => Register(name, a => handler(Args.To<T1>(At(a, 0))));

    /// <summary>Typed export with 2 parameters.</summary>
    public static void Register<T1, T2, TResult>(string name, Func<T1?, T2?, TResult> handler)
        => Register(name, a => handler(Args.To<T1>(At(a, 0)), Args.To<T2>(At(a, 1))));

    /// <summary>Typed export with 3 parameters.</summary>
    public static void Register<T1, T2, T3, TResult>(string name, Func<T1?, T2?, T3?, TResult> handler)
        => Register(name, a => handler(Args.To<T1>(At(a, 0)), Args.To<T2>(At(a, 1)), Args.To<T3>(At(a, 2))));

    /// <summary>Typed export with 4 parameters.</summary>
    public static void Register<T1, T2, T3, T4, TResult>(string name, Func<T1?, T2?, T3?, T4?, TResult> handler)
        => Register(name, a => handler(Args.To<T1>(At(a, 0)), Args.To<T2>(At(a, 1)), Args.To<T3>(At(a, 2)), Args.To<T4>(At(a, 3))));

    private static object? At(object?[] a, int i) => i < a.Length ? a[i] : null;

    /// <summary>Calls an export of another resource (synchronously, with return value).
    /// Throws if the export does not exist.
    /// NOTE: The handler runs in the CALLER's context (its active runtime +
    /// SynchronizationContext): GetCurrentResourceName/Events.On/Log inside the handler
    /// refer to the CALLING resource, and an `await` inside the handler resumes on THAT
    /// resource's scheduler. Irrelevant for synchronous handlers (the normal case).</summary>
    public static object? Call(string resource, string name, params object?[] args)
    {
        if (s_exports.TryGetValue(resource, out var byName) && byName.TryGetValue(name, out var handler))
            return handler(args ?? Array.Empty<object?>());
        throw new InvalidOperationException($"Export '{name}' of resource '{resource}' not found.");
    }

    /// <summary>Like Call, with a typed return value (safe numeric coercion via
    /// <see cref="Args.To{T}"/> — an export returning int can be read as long etc.).</summary>
    public static T? Call<T>(string resource, string name, params object?[] args)
        => Args.To<T>(Call(resource, name, args));

    // On resource stop, remove all exports of the resource (frees captured refs).
    internal static void ClearResource(string resource) => s_exports.Remove(resource);
}
