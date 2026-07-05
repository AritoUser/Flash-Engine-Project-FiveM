using System;
using System.Linq;
using System.Reflection;

namespace Flash;

// =====================================================================================
//  Declarative exports (#173, part 1).
//
//  Instead of hand-wiring `Exports.Register("name", args => handler((int)args[0], ...))`,
//  a resource decorates methods with [FlashExport("name")] and registers them all with
//  Exports.RegisterAll(this). The dispatcher binds the incoming args to the method's typed
//  parameters (numeric-safe coercion, declared defaults) and returns the method's result.
//  Mirrors the [Command]/[EventHandler] routers.
//
//  It's also the enabler for the EmmyLua .d.lua generator (#173, part 2): the attribute gives
//  a build task a stable, static list of exported methods with their parameter/return types
//  and XML docs to emit definitions from.
// =====================================================================================

/// <summary>
/// Marks a method as a C# export callable by other resources under <see cref="Name"/>;
/// register the containing object with <see cref="Exports.RegisterAll"/>. Arguments are bound
/// to the method's typed parameters (numeric-safe coercion) and the return value is the export
/// result. (#173)
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class FlashExportAttribute : Attribute
{
    /// <summary>The export name other resources call via <c>Exports.Call(resource, name, ...)</c>.</summary>
    public string Name { get; }
    public FlashExportAttribute(string name) => Name = name;
}

public static partial class Exports
{
    /// <summary>
    /// Registers every <c>[FlashExport]</c>-annotated method of <paramref name="target"/>
    /// (public + non-public, instance + static). Incoming arguments bind positionally to the
    /// method's parameters with numeric-safe coercion (primitives, string, bool, enums, and
    /// matching reference types); a missing/uncoercible argument uses the parameter's declared
    /// default. The method's return value becomes the export result.
    /// </summary>
    public static void RegisterAll(object target)
    {
        foreach (var m in target.GetType().GetMethods(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
        {
            var exports = m.GetCustomAttributes<FlashExportAttribute>().ToArray();
            if (exports.Length == 0) continue;

            var method = m;                       // capture
            var ps = m.GetParameters();

            foreach (var ex in exports)
            {
                string name = ex.Name;
                Register(name, args =>
                {
                    object?[] call = BindArgs(ps, args);
                    try
                    {
                        return method.Invoke(method.IsStatic ? null : target, call);
                    }
                    catch (TargetInvocationException tie)
                    {
                        Log.Error($"Export '{name}' threw: {tie.InnerException?.Message ?? tie.Message}");
                        Diagnostics.Report($"export:{name}", tie.InnerException ?? tie);
                        return null;
                    }
                });
            }
        }
    }

    // Binds the export args to the method's parameters (positional, numeric-safe coercion,
    // declared defaults) -- same rules as the event router's argument binding.
    private static object?[] BindArgs(ParameterInfo[] ps, object?[] args)
    {
        var call = new object?[ps.Length];
        for (int i = 0; i < ps.Length; i++)
        {
            var p = ps[i];
            var t = p.ParameterType;
            object? raw = i < args.Length ? args[i] : null;
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
}
