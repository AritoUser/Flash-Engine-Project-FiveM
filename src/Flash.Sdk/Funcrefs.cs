using System;
using System.Collections.Generic;
using System.Threading;

namespace Flash;

/// <summary>
/// Registry for local function references: maps a refIdx to a C# callback. Needed
/// whenever the core calls C# back (commands, exports, deferrals): we register the
/// delegate, hand the native the refIdx via the canonical ref string, and the core
/// invokes it through IScriptRefRuntime.CallRef → back to here.
///
/// Internal (plumbing). The callback receives the decoded arguments (object?[]) and
/// may return a value (for later RPC/exports; commands return null).
/// </summary>
internal static class Funcrefs
{
    // Per ref: the callback + the resource's scheduler context captured at Register
    // time + the resource name. Context: so the callback (command/deferral) later runs
    // in the right context → an `await` inside resumes on the script thread. Name: so
    // ClearResource can drop ALL refs of a resource on unload — otherwise the delegates
    // would keep the collectible ALC alive forever (and a command handler of the OLD
    // instance would remain callable after a restart).
    private readonly record struct Entry(Func<object?[], object?> Fn, FlashSyncContext? Ctx, string Resource);

    private static readonly Dictionary<int, Entry> s_refs = new();
    private static int s_next = 1; // 0 reserved/invalid

    /// <summary>Registers a callback → new refIdx. Captures the current resource context.</summary>
    public static int Register(Func<object?[], object?> fn)
    {
        // Resource identity like Events/Exports: correct because the active runtime is
        // set while registering (OnStart/handler/command).
        string res = global::Flash.Natives.Cfx.GetCurrentResourceName() ?? "";
        int id = s_next++;
        s_refs[id] = new Entry(fn, SynchronizationContext.Current as FlashSyncContext, res);
        return id;
    }

    /// <summary>Invokes the callback for refIdx (from the host via CallRef) — in the captured context.</summary>
    public static object? Invoke(int refIdx, object?[] args)
    {
        if (!s_refs.TryGetValue(refIdx, out var e)) return null;
        object? result = null;
        Scheduler.RunWith(e.Ctx, () => result = e.Fn(args));
        return result;
    }

    /// <summary>Duplicates a ref (the core keeps its own lifetime) → new refIdx to the
    /// same callback. Both indices can be removed independently.</summary>
    public static int Duplicate(int refIdx)
    {
        if (!s_refs.TryGetValue(refIdx, out var e)) return -1;
        int id = s_next++;
        s_refs[id] = e;
        return id;
    }

    /// <summary>Releases a ref.</summary>
    public static void Remove(int refIdx) => s_refs.Remove(refIdx);

    /// <summary>On resource stop: drop all refs of the resource (frees captured delegates
    /// + contexts → prerequisite for collecting the collectible ALC).</summary>
    internal static void ClearResource(string resource)
    {
        List<int>? drop = null;
        foreach (var kv in s_refs)
            if (kv.Value.Resource == resource) (drop ??= new List<int>()).Add(kv.Key);
        if (drop != null)
            foreach (var id in drop) s_refs.Remove(id);
    }
}
