# Bug: StateWatcher Fails to Validate [FromSource] Parameter Types, Causing Server Script Thread Crash

## Component
* **Path:** `src/Flash.Sdk/StateWatcher.cs`
* **Vulnerability Type:** Reliability / Reflection Invocation Crash
* **Severity:** High

---

## Description

The C# Event Router (`Events.Router.cs`) implements a fail-fast mechanism at startup using `ValidateFromSourceParams(m, ps)`. If a developer incorrectly decorates a parameter with `[FromSource]` using an unsupported type (such as `double` or a custom class), the resource will fail to load immediately on startup with a clear `InvalidOperationException`.

However, the newly introduced `StateWatcher.cs` (`[StateWatcher]` reactive state bag bindings) **does not validate parameters at startup**.

If a developer writes an invalid signature, such as:
```csharp
[StateWatcher("job")]
public void OnJobChange([FromSource] double invalidParam, string job) 
{
    // ...
}
```

The registration via `StateWatchers.RegisterAll(this)` succeeds silently. Later, when the state-bag changes, the native callback triggers `StateWatcher.Dispatch`:

1. `Bind` is executed. Because `double` is not `int`, `string`, or `ServerPlayer`, `call[i]` is assigned `null`.
2. `method.Invoke(..., call)` is executed. Because `null` cannot be passed to a non-nullable value type (`double`), the reflection layer directly throws an `ArgumentException` (which is thrown *by the reflection system*, not from inside the invoked method).
3. The try-catch block inside `Dispatch` only catches `TargetInvocationException`:
   ```csharp
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
   ```
4. The `ArgumentException` escapes the try-catch block, bubbles up, and **crashes the main server scripting thread**, causing the resource (or the entire FXServer scripting environment) to freeze or crash.

---

## Proposed Fix

### 1. Fail Fast at Registration
Add parameter validation inside `StateWatchers.RegisterAll` (just like the event router does) so that invalid signatures are rejected during resource initialization:

```csharp
public static void RegisterAll(object target)
{
    string resource = global::Flash.Natives.Cfx.GetCurrentResourceName() ?? "";

    var byKey = new Dictionary<string, List<(MethodInfo Method, object Target)>>();
    foreach (var m in target.GetType().GetMethods(
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
    {
        var watchers = m.GetCustomAttributes<StateWatcherAttribute>().ToArray();
        if (watchers.Length == 0) continue;

        // Fail fast if [FromSource] is applied to an unsupported type
        ValidateFromSourceParams(m, m.GetParameters());

        foreach (var w in watchers)
        {
            if (!byKey.TryGetValue(w.Key, out var list)) byKey[w.Key] = list = new();
            list.Add((m, target));
        }
    }
    
    // ... rest of RegisterAll logic ...
}

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
```

### 2. Harden the Dispatch Invocation
In `Dispatch`, catch general exceptions (or specifically handle reflection argument exceptions) so that a bad invocation can never crash the scheduler context:

```csharp
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
    Log.Error($"StateWatcher '{key}' invocation failed: {ex.Message}");
    Diagnostics.Report($"statewatcher:{key}", ex);
}
```
