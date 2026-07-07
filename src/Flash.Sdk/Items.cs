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
    /// <summary>The item name this handler reacts to (e.g. "bread", "repairkit").
    /// Null when the handler binds by <see cref="Category"/> instead.</summary>
    public string? ItemName { get; }

    /// <summary>Category binding (#232): the handler runs for EVERY item carrying this
    /// registered category (e.g. Category = "food" handles bread/sandwich/apple). Name
    /// and category handlers both run (multi-dispatch, like everything in this router);
    /// a method matched via name AND category runs only once.</summary>
    public string? Category { get; init; }

    public ItemHandlerAttribute(string itemName) => ItemName = itemName;
    public ItemHandlerAttribute() { } // category-only: [ItemHandler(Category = "food")]
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
    // Category handlers (#232): category name -> handlers. Separate from the name dict,
    // because resolution happens at USE time (item -> its categories -> handlers);
    // at registration time the item registry may not be populated yet.
    private static readonly Dictionary<string, List<(MethodInfo Method, object? Target, string Resource)>> s_categoryHandlers
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
            {
                // An attribute needs a name OR a category -- an empty one would be a silent
                // handler that never fires. Fail loudly; registration time is debug time.
                if (string.IsNullOrEmpty(it.ItemName) && string.IsNullOrEmpty(it.Category))
                    throw new InvalidOperationException($"[ItemHandler] on {type.Name}.{m.Name}: specify ItemName or Category.");

                lock (s_lock)
                {
                    if (!string.IsNullOrEmpty(it.ItemName))
                    {
                        if (!s_handlers.TryGetValue(it.ItemName!, out var list))
                            s_handlers[it.ItemName!] = list = new();
                        list.Add((m, instance, resource));
                    }
                    if (!string.IsNullOrEmpty(it.Category))
                    {
                        if (!s_categoryHandlers.TryGetValue(it.Category!, out var list))
                            s_categoryHandlers[it.Category!] = list = new();
                        list.Add((m, instance, resource));
                    }
                }
            }
        }
    }

    /// <summary>
    /// Dispatches an item use to all registered handlers (any resource). Returns true if
    /// at least one handler ran — the inventory typically consumes the item only then.
    /// Handlers are isolated: one throwing handler neither stops the others nor the caller.
    /// </summary>
    public static bool Use(int netId, string itemName, params object?[] extra)
        => Dispatch(netId, itemName, null, null, extra);

    /// <summary>Convenience overload for handler code that already holds a player.</summary>
    public static bool Use(ServerPlayer player, string itemName, params object?[] extra)
        => Use(player.NetId, itemName, extra);

    /// <summary>
    /// Dispatches the use of a UNIQUE item instance (#214/#232): resolves the item name
    /// from the instance, and hands the handlers the instance id plus its metadata —
    /// a handler parameter of a matching class type receives the DESERIALIZED metadata
    /// (e.g. <c>void OnUse(ServerPlayer p, WeaponMetadata meta)</c>), a <c>ulong</c>
    /// parameter receives the instance id (to write metadata back after mutating it).
    /// </summary>
    public static bool UseUnique(int netId, ulong instanceId, params object?[] extra)
    {
        ulong container = Inventory.ContainerOfUnique(instanceId);
        if (container == 0) return false;
        uint itemId = 0;
        foreach (var (inst, iid) in Inventory.ListUnique(container))
            if (inst == instanceId) { itemId = iid; break; }
        string? name = Inventory.NameOf(itemId);
        if (name == null) return false; // unregistered legacy item -> no handler
        return Dispatch(netId, name, instanceId, Inventory.GetMetadata(instanceId), extra);
    }

    // Shared dispatch for name AND category handlers (#232). Multi-dispatch like the rest
    // of the router: ALL matching handlers run; a method that matches via name AND category
    // runs exactly once (deduped by MethodInfo).
    private static bool Dispatch(int netId, string itemName, ulong? instanceId, string? metadataJson, object?[] extra)
    {
        var snapshot = new List<(MethodInfo Method, object? Target)>();
        lock (s_lock)
        {
            if (s_handlers.TryGetValue(itemName, out var byName))
                foreach (var h in byName) snapshot.Add((h.Method, h.Target));
            // Resolve the item's categories at use time (registry lives in the Inventory mirror).
            foreach (string cat in Inventory.CategoriesOf(itemName))
                if (s_categoryHandlers.TryGetValue(cat, out var byCat))
                    foreach (var h in byCat)
                    {
                        bool dup = false;
                        foreach (var s in snapshot) if (ReferenceEquals(s.Method, h.Method)) { dup = true; break; }
                        if (!dup) snapshot.Add((h.Method, h.Target));
                    }
        }
        if (snapshot.Count == 0) return false;

        foreach (var (method, tgt) in snapshot)
        {
            try
            {
                object?[] call = Bind(method.GetParameters(), netId, itemName, instanceId, metadataJson, extra);
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

    // ServerPlayer / first int -> the using player; first string -> the item name;
    // first ulong -> the unique instance id (#232); a class-typed parameter receives the
    // deserialized instance metadata; everything else binds positionally from the extra
    // args (numeric-safe, defaults).
    private static object?[] Bind(ParameterInfo[] ps, int netId, string itemName, ulong? instanceId, string? metadataJson, object?[] extra)
    {
        var call = new object?[ps.Length];
        bool netIdUsed = false, nameUsed = false, instUsed = false, metaUsed = false;
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
            // Unique context (#232): first ulong = instance id (the handler needs it,
            // to write mutated metadata BACK via SetMetadata).
            if (t == typeof(ulong) && !instUsed && instanceId.HasValue) { call[i] = instanceId.Value; instUsed = true; continue; }
            // Metadata injection (#232): the first CLASS parameter (not string, not
            // ServerPlayer) receives the deserialized metadata DTO. Broken/missing
            // metadata -> null (the handler checks itself; no dispatch crash).
            if (!metaUsed && metadataJson != null && t.IsClass && t != typeof(string) && t != typeof(object) && !t.IsArray)
            {
                metaUsed = true;
                try { call[i] = System.Text.Json.JsonSerializer.Deserialize(metadataJson, t); }
                catch { call[i] = null; }
                continue;
            }

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
        {
            foreach (var list in s_handlers.Values)
                list.RemoveAll(h => h.Resource == resource);
            foreach (var list in s_categoryHandlers.Values)
                list.RemoveAll(h => h.Resource == resource);
        }
    }
}
