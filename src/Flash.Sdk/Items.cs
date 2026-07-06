using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace Flash;

// =====================================================================================
//  Attribute-driven item action registry (#159).
//
//  Whatever inventory a server runs (a future Flash inventory, a custom C# one, or a
//  bridge from an external system), "player uses item X" always ends in gameplay code.
//  This layer decouples the two sides:
//    - gameplay resources declare handlers with [ItemHandler("bread")] and register them
//      via Items.RegisterAll(this) — grouped wherever they belong (food logic in the
//      restaurant resource, drug logic in the gang resource, ...);
//    - the INVENTORY side reports a use with Items.Use(netId, "bread", extra...) — the
//      registry is shared across all Flash resources (like Exports), so the inventory
//      and the handlers can live in different resources.
//
//  BINDING (mirrors the other routers, no magic): parameters bind by type/position —
//  ServerPlayer / a first int receive the using player, a first string receives the item
//  name, everything else binds positionally from the extra args (numeric-safe coercion,
//  declared defaults). Task-returning handlers are awaited safely; a throwing handler
//  cannot take down the inventory call. Handlers are removed on resource stop.
// =====================================================================================

/// <summary>
/// Runs the decorated method when a player uses the item <see cref="ItemName"/>.
/// Register the containing object (or a static class type) with
/// <see cref="Items.RegisterAll"/>; the inventory side triggers via
/// <see cref="Items.Use"/>. Multiple attributes handle several items. (#159)
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class ItemHandlerAttribute : Attribute
{
    /// <summary>The item name this handler reacts to (e.g. "bread", "repairkit").</summary>
    public string ItemName { get; }
    public ItemHandlerAttribute(string itemName) => ItemName = itemName;
}

/// <summary>Item-use routing: <see cref="RegisterAll"/> wires up
/// <see cref="ItemHandlerAttribute"/> methods, <see cref="Use"/> dispatches a use to
/// every registered handler (across all Flash resources). (#159)</summary>
public static class Items
{
    // item name -> handlers (method + instance + owning resource for unload cleanup).
    // Guarded by s_lock: unlike the other routers (whose Register/dispatch are both on the
    // script thread), Items.Use is a cross-resource dispatch entry that an inventory may
    // call from an off-thread continuation, so registration/cleanup/dispatch can overlap
    // and must not race the plain dictionary. (#202)
    private static readonly Dictionary<string, List<(MethodInfo Method, object? Target, string Resource)>> s_handlers
        = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object s_lock = new();

    /// <summary>
    /// Registers every <c>[ItemHandler]</c>-annotated method of <paramref name="target"/>
    /// (public + non-public, instance + static; a <see cref="Type"/> registers a
    /// static/utility class, #180).
    /// </summary>
    public static void RegisterAll(object target)
    {
        var (type, instance) = RouterTarget.Resolve(target);
        string resource = global::Flash.Natives.Cfx.GetCurrentResourceName() ?? "";

        foreach (var m in type.GetMethods(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
        {
            var items = m.GetCustomAttributes<ItemHandlerAttribute>().ToArray();
            if (items.Length == 0) continue;
            RouterTarget.RequireInvokable(m, instance, "Items");

            foreach (var it in items)
                lock (s_lock)
                {
                    if (!s_handlers.TryGetValue(it.ItemName, out var list))
                        s_handlers[it.ItemName] = list = new();
                    list.Add((m, instance, resource));
                }
        }
    }

    /// <summary>
    /// Dispatches an item use to all registered handlers (any resource). Returns true if
    /// at least one handler ran — the inventory typically consumes the item only then.
    /// Handlers are isolated: one throwing handler neither stops the others nor the caller.
    /// </summary>
    public static bool Use(int netId, string itemName, params object?[] extra)
    {
        (MethodInfo Method, object? Target, string Resource)[] snapshot;
        lock (s_lock)
        {
            if (!s_handlers.TryGetValue(itemName, out var list) || list.Count == 0) return false;
            snapshot = list.ToArray(); // snapshot under the lock, invoke outside it (#202)
        }

        foreach (var (method, tgt, _) in snapshot)
        {
            try
            {
                object?[] call = Bind(method.GetParameters(), netId, itemName, extra);
                object? result = method.Invoke(method.IsStatic ? null : tgt, call);
                if (result is Task task) _ = Observe(task, itemName);
            }
            catch (TargetInvocationException tie)
            {
                Log.Error($"Item handler '{itemName}' threw: {tie.InnerException?.Message ?? tie.Message}");
                Diagnostics.Report($"item:{itemName}", tie.InnerException ?? tie);
            }
            catch (Exception ex)
            {
                Log.Error($"Item handler '{itemName}' invocation failed: {ex.Message}");
                Diagnostics.Report($"item:{itemName}", ex);
            }
        }
        return true;
    }

    /// <summary>Convenience overload for handler code that already holds a player.</summary>
    public static bool Use(ServerPlayer player, string itemName, params object?[] extra)
        => Use(player.NetId, itemName, extra);

    // ServerPlayer / first int -> the using player; first string -> the item name;
    // everything else binds positionally from the extra args (numeric-safe, defaults).
    private static object?[] Bind(ParameterInfo[] ps, int netId, string itemName, object?[] extra)
    {
        var call = new object?[ps.Length];
        bool netIdUsed = false, nameUsed = false;
        int extraIdx = 0;

        for (int i = 0; i < ps.Length; i++)
        {
            var p = ps[i];
            var t = p.ParameterType;

            // The player slot is delivered EITHER as ServerPlayer OR as the first int —
            // never both, or a later int argument would silently receive the netId.
            if (t == typeof(ServerPlayer) && !netIdUsed) { call[i] = Players.Get(netId); netIdUsed = true; continue; }
            if (t == typeof(int) && !netIdUsed) { call[i] = netId; netIdUsed = true; continue; }
            if (t == typeof(string) && !nameUsed) { call[i] = itemName; nameUsed = true; continue; }

            object? raw = extraIdx < extra.Length ? extra[extraIdx++] : null;
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

    private static async Task Observe(Task task, string itemName)
    {
        try { await task; }
        catch (Exception ex)
        {
            Log.Error($"Item handler '{itemName}' (async) threw: {ex.Message}");
            Diagnostics.Report($"item:{itemName}", ex);
        }
    }

    /// <summary>On resource stop: drop the resource's handlers (frees captured refs so
    /// the collectible ALC can unload). Called by the host.</summary>
    internal static void ClearResource(string resource)
    {
        lock (s_lock)
            foreach (var list in s_handlers.Values)
                list.RemoveAll(h => h.Resource == resource);
    }
}
