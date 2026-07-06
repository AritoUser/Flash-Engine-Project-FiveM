# Bug: RegisterAll Fails Silently When Passed a Type (e.g. for Static / Utility Classes)

## Components
* **Paths:**
  * `src/Flash.Sdk/Commands.Router.cs`
  * `src/Flash.Sdk/Events.Router.cs`
  * `src/Flash.Sdk/Exports.Router.cs`
  * `src/Flash.Sdk/StateWatcher.cs`
* **Vulnerability Type:** Developer Experience (DX) / Reflection Bug
* **Severity:** Medium

---

## Description

In C#, developers often group related handlers (like admin commands, utility exports, or log event handlers) into `static class` structures to keep their codebase modular. Since static classes cannot be instantiated via `new`, the standard C# practice is to register their handlers by passing the class type:

```csharp
Commands.RegisterAll(typeof(AdminCommands));
```

However, across all four SDK attribute routers (`Commands`, `Events`, `Exports`, and `StateWatchers`), the `RegisterAll` method is defined as:

```csharp
public static void RegisterAll(object target)
{
    foreach (var m in target.GetType().GetMethods(...))
    {
        // ...
    }
}
```

Because `typeof(AdminCommands)` is a `Type` object (which inherits from `object`), it binds to the `target` parameter. However:
1. `target.GetType()` evaluates to `System.RuntimeType` (the type of the `Type` object itself), NOT `AdminCommands`.
2. The reflection loop scans `System.RuntimeType`'s own methods (such as `GetMethods`, `ToString`, etc.).
3. It finds no annotations (e.g. `[Command]`, `[EventHandler]`) and **silently registers zero handlers**.

The developer receives no warning or exception, and their handlers simply fail to work.

---

## Proposed Fix

Modify `RegisterAll` in all four routers to detect if a `Type` is passed, and if so, reflect on it directly (while passing `null` as the invocation target for static methods):

```csharp
public static void RegisterAll(object target)
{
    if (target == null) return;

    Type type;
    object? instance;

    if (target is Type t)
    {
        type = t;
        instance = null;
    }
    else
    {
        type = target.GetType();
        instance = target;
    }

    foreach (var m in type.GetMethods(
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
    {
        // ...
        // During invocation:
        method.Invoke(method.IsStatic ? null : instance, call);
    }
}
```

Additionally, providing a generic overload would improve DX:
```csharp
public static void RegisterAll<T>() => RegisterAll(typeof(T));
```
This allows developers to write:
```csharp
Commands.RegisterAll<AdminCommands>();
```
Which is clean, type-safe, and works perfectly for static classes.
