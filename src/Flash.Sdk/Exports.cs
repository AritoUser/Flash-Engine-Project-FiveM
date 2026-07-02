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
public static class Exports
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

    /// <summary>Like Call, with a typed return value.</summary>
    public static T? Call<T>(string resource, string name, params object?[] args)
    {
        object? result = Call(resource, name, args);
        return result is T t ? t : default;
    }

    // On resource stop, remove all exports of the resource (frees captured refs).
    internal static void ClearResource(string resource) => s_exports.Remove(resource);
}
