using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace Flash;

// =====================================================================================
//  Attribute-based event routing (#111).
//
//  The imperative form -- Events.On("name", args => { var p = Players.Get(...); string s =
//  args[0]?.ToString(); ... }) -- is untyped, manual-slicing and easy to get wrong. This
//  layer lets a resource decorate methods with [EventHandler("name")] and register them all
//  with Events.RegisterAll(this): the dispatcher deserializes the msgpack args into the
//  method's typed parameters (via the shared Args.ToType), injects the caller for a
//  [FromSource] parameter, enforces the declarative authorization attributes (#131) BEFORE
//  the body runs, and safely awaits Task-returning handlers. It mirrors the command router
//  (#12) for events.
// =====================================================================================

/// <summary>
/// Marks a method as an event handler; register the containing object with
/// <see cref="Events.RegisterAll"/>. The event arguments are deserialized into the method's
/// parameters (numeric-safe coercion); a parameter marked <see cref="FromSourceAttribute"/>
/// receives the caller instead. Multiple attributes route several events to one method. (#111)
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class EventHandlerAttribute : Attribute
{
    /// <summary>The event name to handle (e.g. "playerConnecting", "shop:buyItem").</summary>
    public string EventName { get; }
    public EventHandlerAttribute(string eventName) => EventName = eventName;
}

/// <summary>
/// On a routed <see cref="EventHandlerAttribute"/> parameter: bind it to the event source
/// instead of a positional argument. Supported types: <see cref="int"/> (netId, -1 if not a
/// client), <see cref="string"/> (raw source, e.g. "net:1"), or <see cref="ServerPlayer"/>. (#111)
/// </summary>
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class FromSourceAttribute : Attribute { }

public static partial class Events
{
    /// <summary>
    /// Registers every <c>[EventHandler]</c>-annotated method of <paramref name="target"/>
    /// (public + non-public, instance + static). Parameters bind positionally from the event
    /// args with numeric-safe coercion (primitives, string, bool, enums, and matching
    /// reference types like arrays/maps); a <c>[FromSource]</c> parameter receives the caller
    /// (int netId / string source / <see cref="ServerPlayer"/>). Declarative authorization
    /// (<see cref="AuthorizeFactionAttribute"/>/<see cref="AuthorizeAdminAttribute"/>) is
    /// enforced before the body runs (server-originated events are trusted). Async handlers
    /// (Task return) are awaited safely.
    /// </summary>
    public static void RegisterAll(object target)
    {
        foreach (var m in target.GetType().GetMethods(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
        {
            var events = m.GetCustomAttributes<EventHandlerAttribute>().ToArray();
            if (events.Length == 0) continue;

            var method = m;                       // capture
            var ps = m.GetParameters();
            // Fail FAST on a misused [FromSource] (unsupported type) at registration, with the
            // exact method/parameter -- otherwise every dispatch would throw an ArgumentException
            // deep in method.Invoke and the handler would silently never run. (#171)
            ValidateFromSourceParams(m, ps);
            // Precompute the authorization policy ONCE (like the command router), including
            // class-level attributes (#162), so the per-dispatch path doesn't re-reflect.
            var authPolicy = Authorization.PolicyFor(m);

            foreach (var ev in events)
            {
                string name = ev.EventName;
                On(name, args =>
                {
                    // Authorization applies ONLY to genuine CLIENT-triggered events ("net:").
                    // Server-internal emits (source "") and core lifecycle events
                    // ("internal-net:", e.g. playerDropped) are server-originated and must NOT
                    // be gated -- otherwise a class-level [AuthorizeAdmin] would block the
                    // dropping player's own cleanup handler (#163). The "net:" prefix is stamped
                    // server-side and unspoofable (#73).
                    bool isClientEvent = Source.StartsWith("net:", StringComparison.Ordinal);
                    if (isClientEvent && authPolicy.HasAny && !authPolicy.Check(SourceNetId, out string deny))
                    {
                        Log.Warn($"[SECURITY] event '{name}' denied for src={SourceNetId}: {deny}.");
                        return;
                    }

                    object?[] call = BindEventArgs(ps, args);
                    try
                    {
                        object? result = method.Invoke(method.IsStatic ? null : target, call);
                        if (result is Task task) _ = ObserveAsync(task, name);
                    }
                    catch (TargetInvocationException tie)
                    {
                        Log.Error($"Event handler '{name}' threw: {tie.InnerException?.Message ?? tie.Message}");
                        Diagnostics.Report($"event:{name}", tie.InnerException ?? tie);
                    }
                });
            }
        }
    }

    // Rejects a [FromSource] parameter of an unsupported type at registration time (#171).
    // Only int (netId), string (raw source) and ServerPlayer are bindable from the source.
    private static void ValidateFromSourceParams(MethodInfo method, ParameterInfo[] ps)
    {
        foreach (var p in ps)
        {
            if (p.GetCustomAttribute<FromSourceAttribute>() == null) continue;
            var t = p.ParameterType;
            if (t != typeof(int) && t != typeof(string) && t != typeof(ServerPlayer))
                throw new InvalidOperationException(
                    $"[FromSource] on '{method.DeclaringType?.Name}.{method.Name}' parameter '{p.Name}' " +
                    $"has unsupported type '{t.Name}'. Use int (netId), string (source) or ServerPlayer.");
        }
    }

    // Binds decoded event args to the handler's parameters. [FromSource] params take the
    // caller and do NOT consume a positional arg; the rest are coerced through Args.ToType
    // (msgpack delivers numbers as long/double). PURE apart from reading the ambient source.
    private static object?[] BindEventArgs(ParameterInfo[] ps, object?[] args)
    {
        var call = new object?[ps.Length];
        int argIdx = 0;
        for (int i = 0; i < ps.Length; i++)
        {
            var p = ps[i];
            var t = p.ParameterType;

            if (p.GetCustomAttribute<FromSourceAttribute>() != null)
            {
                call[i] =
                    t == typeof(int) ? SourceNetId :
                    t == typeof(string) ? Source :
                    t == typeof(ServerPlayer) ? Players.Get(SourceNetId) :
                    null;
                continue;
            }

            object? raw = argIdx < args.Length ? args[argIdx++] : null;
            object? bound = Args.ToType(raw, t);
            // Missing/uncoercible arg: prefer the parameter's declared default -- this must work
            // for BOTH value AND reference types (e.g. `string reason = "none"`, which previously
            // stayed null and passed null to the handler -- #167). Only a non-nullable value type
            // WITHOUT a default falls back to the type's zero value (it can't hold null).
            if (bound == null)
            {
                if (p.HasDefaultValue) bound = p.DefaultValue;
                else if (t.IsValueType && Nullable.GetUnderlyingType(t) == null) bound = Activator.CreateInstance(t);
            }
            call[i] = bound;
        }
        return call;
    }

    private static async Task ObserveAsync(Task task, string name)
    {
        try { await task; }
        catch (Exception ex)
        {
            Log.Error($"Event handler '{name}' (async) threw: {ex.Message}");
            Diagnostics.Report($"event:{name}", ex);
        }
    }
}
