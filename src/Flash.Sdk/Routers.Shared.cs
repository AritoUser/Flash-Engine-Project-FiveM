using System;
using System.Reflection;

namespace Flash;

// =====================================================================================
//  Shared target resolution for the attribute routers (#180).
//
//  All four routers (Commands/Events/Exports/StateWatchers) accept `object target` in
//  RegisterAll. Passing a Type — the natural C# idiom for static/utility classes, e.g.
//  Commands.RegisterAll(typeof(AdminCommands)) — used to bind the Type OBJECT itself:
//  target.GetType() evaluated to System.RuntimeType, the scan found no annotated methods,
//  and registration silently did nothing. This helper makes a Type argument mean
//  "scan THIS type, invoke statics with a null instance" for every router uniformly.
// =====================================================================================
internal static class RouterTarget
{
    /// <summary>Resolves a RegisterAll argument into (type to scan, invocation instance).
    /// A <see cref="Type"/> argument scans that type with a null instance (statics only);
    /// anything else scans the object's runtime type and invokes on the object. (#180)</summary>
    public static (Type Type, object? Instance) Resolve(object target)
    {
        if (target == null) throw new ArgumentNullException(nameof(target));
        return target is Type t ? (t, null) : (target.GetType(), target);
    }

    /// <summary>Fail FAST when a Type was passed but an annotated method needs an instance —
    /// otherwise every dispatch would throw a TargetException deep in method.Invoke and the
    /// handler would silently never run. (#180)</summary>
    public static void RequireInvokable(MethodInfo m, object? instance, string registrar)
    {
        if (instance == null && !m.IsStatic)
            throw new InvalidOperationException(
                $"{registrar}.RegisterAll(typeof({m.DeclaringType?.Name})) found annotated " +
                $"instance method '{m.Name}'. Make it static or pass an object instance.");
    }
}
