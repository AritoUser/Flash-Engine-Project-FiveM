using System;
using System.Collections.Generic;

namespace Flash;

/// <summary>
/// Programmatic hook for otherwise-swallowed errors (#24). The framework isolates
/// every handler (a throwing event handler cannot kill the frame or its siblings)
/// and logs to the console — but a 24/7 server owner does not stare at the console.
/// Register here to forward errors to Discord/Sentry/your monitoring:
///
///   Diagnostics.OnUnhandled((context, ex) =>
///       _ = Http.Post(webhookUrl, ...));   // fire-and-forget, don't block
///
/// Fires for: isolated event-handler exceptions and errors in scheduled async
/// continuations. The hook itself is isolated too — a throwing hook is dropped
/// from that report (no recursion).
/// </summary>
public static class Diagnostics
{
    // Partitioned per resource (shared SDK): a handler of an unloaded resource must not
    // keep the collectible ALC alive -> ClearResource.
    private static readonly Dictionary<string, List<Action<string, Exception>>> s_handlers = new();

    /// <summary>Registers an error hook: handler(context, exception). Context names
    /// the source (e.g. "event:playerDropped" or "scheduler").</summary>
    public static void OnUnhandled(Action<string, Exception> handler)
    {
        string res = global::Flash.Natives.Cfx.GetCurrentResourceName() ?? "";
        if (!s_handlers.TryGetValue(res, out var list))
        {
            list = new List<Action<string, Exception>>();
            s_handlers[res] = list;
        }
        list.Add(handler);
    }

    /// <summary>Framework-internal: reports an isolated error to all registered hooks.</summary>
    internal static void Report(string context, Exception ex)
    {
        if (s_handlers.Count == 0) return;
        foreach (var list in s_handlers.Values)
        {
            foreach (var h in list.ToArray())
            {
                try { h(context, ex); }
                catch { /* a throwing hook must not trigger infinite recursion */ }
            }
        }
    }

    internal static void ClearResource(string resource) => s_handlers.Remove(resource);
}
