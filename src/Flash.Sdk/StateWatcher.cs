using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace Flash;

// =====================================================================================
//  Reactive state-bag bindings (#120).
//
//  Subscribing to state-bag changes normally means low-level, string-based native handlers
//  plus manual parsing of the bag name ("player:15") and untyped values. This layer lets a
//  resource decorate methods with [StateWatcher("key")] and register them with
//  StateWatchers.RegisterAll(this): the dispatcher resolves the changed bag to its owner,
//  coerces the new/old value to the method's parameter types, and invokes it.
//
//  BINDING (unambiguous, reuses [FromSource] from the event router):
//    - a [FromSource] parameter receives the bag owner: ServerPlayer (from "player:<id>"),
//      int (the netId) or string (the raw bag name, e.g. "entity:12").
//    - the remaining parameters bind positionally to (newValue, oldValue), numeric-safe.
//  OLD VALUE: the native only delivers the new value, so the previous value is cached per
//  (bag,key) and handed in as oldValue. The cache is cleaned on resource unload and when a
//  player disconnects (its "player:<id>" entries) so it can't leak.
//
//  ZERO OVERHEAD WHEN INACTIVE: one native handler is registered per WATCHED KEY only (keys
//  with no [StateWatcher] never install a handler). Handlers are removed on unload.
// =====================================================================================

/// <summary>
/// Runs the decorated method whenever a state-bag key <see cref="Key"/> changes (server-set
/// or replicated from a client). Register the containing object with
/// <see cref="StateWatchers.RegisterAll"/>. A <see cref="FromSourceAttribute"/> parameter
/// receives the bag owner (ServerPlayer / netId / raw bag name); the remaining parameters bind
/// to (newValue, oldValue) with numeric-safe coercion. Multiple attributes watch several keys. (#120)
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class StateWatcherAttribute : Attribute
{
    /// <summary>The state-bag key to watch (e.g. "job", "fuel", "isHandcuffed").</summary>
    public string Key { get; }
    public StateWatcherAttribute(string key) => Key = key;
}

/// <summary>Registers <see cref="StateWatcherAttribute"/> methods against the native
/// state-bag change handler. (#120)</summary>
public static class StateWatchers
{
    // Per resource: native handler cookies (to remove on unload) and the previous value per
    // (bagName + '\0' + key) for oldValue. Written on the script thread (register/unload) and
    // read/written in the change dispatch (also marshalled onto the script thread).
    private static readonly Dictionary<string, List<int>> s_cookies = new();
    private static readonly Dictionary<string, Dictionary<string, object?>> s_lastValues = new();
    private static readonly HashSet<string> s_dropWired = new();
    // Guards the three dictionaries above. The change callback runs INLINE on whatever
    // thread invokes the funcref — thread-pool delivery is possible (replicated changes /
    // off-thread setters), so an unlocked Dictionary would race the script-thread mutations
    // in RegisterAll/ClearResource/drop-cleanup ("Collection was modified" or corruption).
    // Same decision as State.cs's s_onChangeLock (#148). (#175)
    private static readonly object s_lock = new();

    /// <summary>
    /// Registers every <c>[StateWatcher]</c>-annotated method of <paramref name="target"/>
    /// (public + non-public, instance + static). One native handler is installed per distinct
    /// watched key; changes are routed to all methods watching that key.
    /// </summary>
    public static void RegisterAll(object target)
    {
        string resource = global::Flash.Natives.Cfx.GetCurrentResourceName() ?? "";

        // A Type argument (static/utility classes) scans that type directly; an object
        // scans its runtime type — previously typeof(X) silently registered nothing (#180).
        var (type, instance) = RouterTarget.Resolve(target);

        // Group methods by watched key so one native handler serves all watchers of a key.
        var byKey = new Dictionary<string, List<(MethodInfo Method, object? Target)>>();
        foreach (var m in type.GetMethods(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
        {
            var watchers = m.GetCustomAttributes<StateWatcherAttribute>().ToArray();
            if (watchers.Length == 0) continue;
            RouterTarget.RequireInvokable(m, instance, "StateWatchers");
            // Fail FAST on a misused [FromSource] (unsupported type) at registration — same
            // rule as the event router (#171). Without this, Bind hands null to a non-nullable
            // parameter and method.Invoke throws an ArgumentException on EVERY dispatch (#177).
            Events.ValidateFromSourceParams(m, m.GetParameters());
            foreach (var w in watchers)
            {
                if (!byKey.TryGetValue(w.Key, out var list)) byKey[w.Key] = list = new();
                list.Add((m, instance));
            }
        }
        if (byKey.Count == 0) return;

        EnsureDropCleanup(resource);

        foreach (var (key, methods) in byKey)
        {
            // keyFilter = key (only this key fires), bagFilter = null (any bag: global/player/entity).
            int cookie = global::Flash.Natives.Cfx.AddStateBagChangeHandler(key, null!, callArgs =>
            {
                Dispatch(resource, key, methods, callArgs);
                return null;
            });
            lock (s_lock)
            {
                if (!s_cookies.TryGetValue(resource, out var cookies)) s_cookies[resource] = cookies = new();
                cookies.Add(cookie);
            }
        }
    }

    // The native change callback: [bagName, key, value, reserved, replicated].
    private static void Dispatch(string resource, string key,
        List<(MethodInfo Method, object? Target)> methods, object?[] callArgs)
    {
        string bagName = callArgs.Length > 0 ? callArgs[0]?.ToString() ?? "" : "";
        object? newValue = callArgs.Length > 2 ? callArgs[2] : null;

        // Old value from the per-resource cache, then store the new one for next time.
        // Under s_lock: this can run on a thread-pool thread concurrently with the
        // script-thread cleanup paths (#175).
        string cacheKey = bagName + "\0" + key;
        object? oldValue;
        lock (s_lock)
        {
            if (!s_lastValues.TryGetValue(resource, out var cache)) s_lastValues[resource] = cache = new();
            cache.TryGetValue(cacheKey, out oldValue);
            cache[cacheKey] = newValue;
        }

        int netId = ParseNetId(bagName);

        // Run in the resource's scheduler context so an `await` in an (async) watcher resumes
        // on the script thread, and isolate each watcher (a throwing one can't kill the others).
        Scheduler.RunWith(Scheduler.Get(resource), () =>
        {
            foreach (var (method, tgt) in methods.ToArray())
            {
                try
                {
                    object?[] call = Bind(method.GetParameters(), bagName, netId, newValue, oldValue);
                    object? result = method.Invoke(method.IsStatic ? null : tgt, call);
                    if (result is Task task) _ = Observe(task, key);
                }
                catch (TargetInvocationException tie)
                {
                    Log.Error($"StateWatcher '{key}' threw: {tie.InnerException?.Message ?? tie.Message}");
                    Diagnostics.Report($"statewatcher:{key}", tie.InnerException ?? tie);
                }
                catch (Exception ex)
                {
                    // Reflection-layer failures (e.g. an ArgumentException from a signature the
                    // binder can't satisfy) are NOT TargetInvocationException — without this
                    // they'd escape the dispatch and crash the script thread (#177).
                    Log.Error($"StateWatcher '{key}' invocation failed: {ex.Message}");
                    Diagnostics.Report($"statewatcher:{key}", ex);
                }
            }
        });
    }

    // [FromSource] params take the bag owner; the rest bind to (newValue, oldValue) in order.
    private static object?[] Bind(ParameterInfo[] ps, string bagName, int netId,
        object? newValue, object? oldValue)
    {
        var call = new object?[ps.Length];
        int valueSlot = 0; // 0 -> newValue, 1 -> oldValue, >=2 -> default
        for (int i = 0; i < ps.Length; i++)
        {
            var p = ps[i];
            var t = p.ParameterType;

            if (p.GetCustomAttribute<FromSourceAttribute>() != null)
            {
                call[i] =
                    t == typeof(ServerPlayer) ? Players.Get(netId) :
                    t == typeof(int) ? netId :
                    t == typeof(string) ? bagName :
                    null;
                continue;
            }

            object? raw = valueSlot == 0 ? newValue : valueSlot == 1 ? oldValue : null;
            valueSlot++;
            object? bound = Args.ToType(raw, t);
            if (bound == null)
            {
                if (p.HasDefaultValue) bound = p.DefaultValue;
                else if (t.IsValueType && Nullable.GetUnderlyingType(t) == null) bound = Activator.CreateInstance(t);
            }
            call[i] = bound;
        }
        return call;
    }

    // "player:15" / "entity:12" -> 15 / 12; "global" or anything else -> -1.
    private static int ParseNetId(string bagName)
    {
        int colon = bagName.LastIndexOf(':');
        return colon >= 0 && int.TryParse(bagName.AsSpan(colon + 1), out int id) ? id : -1;
    }

    // One playerDropped listener per resource purges the disconnected player's cached values
    // so the oldValue cache can't grow with stale "player:<id>" entries. Core-origin only (#168).
    private static void EnsureDropCleanup(string resource)
    {
        lock (s_lock) { if (!s_dropWired.Add(resource)) return; }
        Events.On("playerDropped", _ =>
        {
            if (!Events.IsFromCore) return;
            lock (s_lock)
            {
                if (!s_lastValues.TryGetValue(resource, out var cache)) return;
                string prefix = "player:" + Events.SourceNetId + "\0";
                var stale = cache.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
                foreach (var k in stale) cache.Remove(k);
            }
        });
    }

    private static async Task Observe(Task task, string key)
    {
        try { await task; }
        catch (Exception ex)
        {
            Log.Error($"StateWatcher '{key}' (async) threw: {ex.Message}");
            Diagnostics.Report($"statewatcher:{key}", ex);
        }
    }

    /// <summary>On resource stop: remove the native handlers and drop the caches (frees the
    /// captured watcher delegates so the collectible ALC can unload).</summary>
    internal static void ClearResource(string resource)
    {
        // Mutate the registries under the lock, but fire the removal natives OUTSIDE it
        // (no lock held across a host call). (#175)
        List<int>? cookies;
        lock (s_lock)
        {
            s_cookies.Remove(resource, out cookies);
            s_lastValues.Remove(resource);
            s_dropWired.Remove(resource);
        }
        if (cookies != null)
            foreach (int cookie in cookies)
                try { global::Flash.Natives.Cfx.RemoveStateBagChangeHandler(cookie); } catch { }
    }
}
