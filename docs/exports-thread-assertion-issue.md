# Bug: Exports.Call and Exports.Register Lack Script-Thread Assertions

## Component
* **Path:** `src/Flash.Sdk/Exports.cs`
* **Vulnerability Type:** Thread-Safety / Race Condition Prevention
* **Severity:** Medium

---

## Description

The Flash-Engine SDK registries are designed to be script-thread-only. To prevent developers from incorrectly invoking non-thread-safe methods off-thread, APIs like `Rpc.Client` (fixed in #169) and database operations implement explicit thread assertions:

```csharp
if (System.Threading.SynchronizationContext.Current is not FlashSyncContext)
    throw new InvalidOperationException("Method must run on the script thread.");
```

However, `Exports.Call` and `Exports.Register` lack these assertions. 

`s_exports` is a plain static `Dictionary<string, Dictionary<string, Func<object?[], object?>>>`. It is modified on the main script thread during resource start/stop via `Register` and `ClearResource`.

If a developer invokes `Exports.Call` from a background task (e.g. inside `Task.Run` or an asynchronous continuation returning off-thread):
1. The background thread reads the non-thread-safe `s_exports` dictionary.
2. If this occurs concurrently while a resource is starting or stopping (modifying `s_exports`), a **race condition** occurs.
3. This can lead to an unhandled `InvalidOperationException` ("Collection was modified") or dictionary memory corruption.

Consistent with the rest of the framework (and the decision in #169 / #89), these entry points should fail fast with a script-thread assertion.

---

## Proposed Fix

Add synchronization context assertions to `Exports.Register` and `Exports.Call`:

```csharp
public static class Exports
{
    // ...

    public static void Register(string name, Func<object?[], object?> handler)
    {
        if (System.Threading.SynchronizationContext.Current is not FlashSyncContext)
            throw new InvalidOperationException(
                "Exports.Register must run on the script thread. " +
                "Dispatch background work to the script thread first.");

        string res = global::Flash.Natives.Cfx.GetCurrentResourceName() ?? "";
        if (!s_exports.TryGetValue(res, out var byName))
        {
            byName = new Dictionary<string, Func<object?[], object?>>();
            s_exports[res] = byName;
        }
        byName[name] = handler;
    }

    public static object? Call(string resource, string name, params object?[] args)
    {
        if (System.Threading.SynchronizationContext.Current is not FlashSyncContext)
            throw new InvalidOperationException(
                "Exports.Call must run on the script thread. " +
                "Dispatch background work to the script thread first.");

        if (s_exports.TryGetValue(resource, out var byName) && byName.TryGetValue(name, out var handler))
            return handler(args ?? Array.Empty<object?>());
        throw new InvalidOperationException($"Export '{name}' of resource '{resource}' not found.");
    }

    // ...
}
```
