# Security and Thread-Safety Audit: Flash-Engine v0.5.0

We conducted a comprehensive audit of the newly introduced C# source files in the Flash-Engine v0.5.0 release. This report details our findings, identifies a critical thread-safety vulnerability in `StateWatcher.cs`, and provides a concrete patch to fix it.

---

## 1. Critical Finding: Race Condition / Memory Corruption in `StateWatcher.cs`

* **Component:** `src/Flash.Sdk/StateWatcher.cs`
* **Vulnerability Type:** Thread-Safety / Concurrent Dictionary Modification Race Condition
* **Severity:** High

### Description
In FiveM, State Bag change handlers registered via `AddStateBagChangeHandler` are triggered whenever state bag values change. Since the Flash-Engine allows setting state bag values off-thread (e.g., inside background database threads or thread-pool tasks), these change callbacks can fire on thread-pool threads concurrently.

While `State.cs` was hardened against this in version 0.4.1 using `s_onChangeLock`, the newly introduced `StateWatcher.cs` handles its cache dictionaries (`s_cookies`, `s_lastValues`, `s_dropWired`) without any synchronization locks.

### Impact
When multiple state bag updates occur concurrently off-thread:

* **Server Crashes:** Two concurrent writes to `s_lastValues[resource]` can corrupt the underlying `Dictionary` structure, throwing an unhandled `InvalidOperationException` or causing the server host to crash.
* **Stale Values & Memory Leaks:** If a player disconnects, `EnsureDropCleanup` runs `cache.Remove(k)` on the script thread. If a thread-pool thread writes to `cache[cacheKey] = newValue` at the same time, it throws a `"Collection was modified"` exception, causing the cleanup routine to fail and leak the disconnected player's state cache.

### Root Cause Analysis
In `StateWatcher.cs`, lines 99–102 (accessed concurrently on thread-pool threads):
```csharp
string cacheKey = bagName + "\0" + key;
if (!s_lastValues.TryGetValue(resource, out var cache)) s_lastValues[resource] = cache = new();
cache.TryGetValue(cacheKey, out object? oldValue);
cache[cacheKey] = newValue;
```

In `StateWatcher.cs`, lines 173–180 (accessed on the main script thread when a player disconnects):
```csharp
Events.On("playerDropped", _ =>
{
    if (!Events.IsFromCore) return;
    if (!s_lastValues.TryGetValue(resource, out var cache)) return;
    string prefix = "player:" + Events.SourceNetId + "\0";
    var stale = cache.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
    foreach (var k in stale) cache.Remove(k); // Structural modification during enumeration/reads
});
```

### Proposed Fix / Patch
To resolve this issue, we must serialize access to the static dictionaries using a synchronization lock object, aligning `StateWatcher.cs` with the pattern used in `State.cs`.

Here is the diff to apply to `StateWatcher.cs`:

```diff
@@ -19,4 +19,5 @@
      private static readonly Dictionary<string, List<int>> s_cookies = new();
      private static readonly Dictionary<string, Dictionary<string, object?>> s_lastValues = new();
      private static readonly HashSet<string> s_dropWired = new();
+     private static readonly object s_lock = new();
  
      /// <summary>
      /// Registers every <c>[StateWatcher]</c>-annotated method of <paramref name="target"/>
@@ -73,15 +74,18 @@
          if (byKey.Count == 0) return;
  
-         EnsureDropCleanup(resource);
+         lock (s_lock)
+         {
+             EnsureDropCleanup(resource);
  
-         foreach (var (key, methods) in byKey)
-         {
-             // keyFilter = key (only this key fires), bagFilter = null (any bag: global/player/entity).
-             int cookie = global::Flash.Natives.Cfx.AddStateBagChangeHandler(key, null!, callArgs =>
-             {
-                 Dispatch(resource, key, methods, callArgs);
-                 return null;
-             });
-             if (!s_cookies.TryGetValue(resource, out var cookies)) s_cookies[resource] = cookies = new();
-             cookies.Add(cookie);
-         }
+             foreach (var (key, methods) in byKey)
+             {
+                 // keyFilter = key (only this key fires), bagFilter = null (any bag: global/player/entity).
+                 int cookie = global::Flash.Natives.Cfx.AddStateBagChangeHandler(key, null!, callArgs =>
+                 {
+                     Dispatch(resource, key, methods, callArgs);
+                     return null;
+                 });
+                 if (!s_cookies.TryGetValue(resource, out var cookies)) s_cookies[resource] = cookies = new();
+                 cookies.Add(cookie);
+             }
+         }
      }
  
@@ -96,7 +100,11 @@
  
          // Old value from the per-resource cache, then store the new one for next time.
          string cacheKey = bagName + "\0" + key;
-         if (!s_lastValues.TryGetValue(resource, out var cache)) s_lastValues[resource] = cache = new();
-         cache.TryGetValue(cacheKey, out object? oldValue);
-         cache[cacheKey] = newValue;
+         object? oldValue;
+         lock (s_lock)
+         {
+             if (!s_lastValues.TryGetValue(resource, out var cache)) s_lastValues[resource] = cache = new();
+             cache.TryGetValue(cacheKey, out oldValue);
+             cache[cacheKey] = newValue;
+         }
  
          int netId = ParseNetId(bagName);
@@ -172,11 +180,14 @@
          if (!s_dropWired.Add(resource)) return;
          Events.On("playerDropped", _ =>
          {
              if (!Events.IsFromCore) return;
-             if (!s_lastValues.TryGetValue(resource, out var cache)) return;
-             string prefix = "player:" + Events.SourceNetId + "\0";
-             var stale = cache.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
-             foreach (var k in stale) cache.Remove(k);
+             lock (s_lock)
+             {
+                 if (!s_lastValues.TryGetValue(resource, out var cache)) return;
+                 string prefix = "player:" + Events.SourceNetId + "\0";
+                 var stale = cache.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
+                 foreach (var k in stale) cache.Remove(k);
+             }
          });
      }
  
@@ -193,8 +204,11 @@
      internal static void ClearResource(string resource)
      {
-         if (s_cookies.Remove(resource, out var cookies))
-             foreach (int cookie in cookies)
-                 try { global::Flash.Natives.Cfx.RemoveStateBagChangeHandler(cookie); } catch { }
-         s_lastValues.Remove(resource);
-         s_dropWired.Remove(resource);
+         lock (s_lock)
+         {
+             if (s_cookies.Remove(resource, out var cookies))
+                 foreach (int cookie in cookies)
+                     try { global::Flash.Natives.Cfx.RemoveStateBagChangeHandler(cookie); } catch { }
+             s_lastValues.Remove(resource);
+             s_dropWired.Remove(resource);
+         }
      }
  }
```

---

## 2. Minor Findings & Design Verifications (Pass)

### A. Catastrophic Backtracking Guard (ReDoS) in `Security.cs` (Pass)
* **Component:** `src/Flash.Sdk/Security.cs`
* **Details:** The `IsMatch` sanitization helper compiles regexes and caches them. ReDoS is mitigated by setting an explicit timeout (`MatchTimeout = TimeSpan.FromMilliseconds(50)`). Pathological input correctly triggers `RegexMatchTimeoutException` and rejects the input cleanly. This is robust and highly secure.

### B. Off-thread Native Access Protection in `FlashHttpMessageHandler.cs` (Pass)
* **Component:** `src/Flash.Sdk/FlashHttpMessageHandler.cs`
* **Details:** HttpClient tasks typically resume on background thread-pool threads. Cfx server natives crash the process if accessed off-thread. The handler correctly uses the captured `FlashSyncContext` to marshal continuations back onto the scripting thread using `_ctx.Post(...)`. It also fails cleanly with an `InvalidOperationException` if constructed off-thread, preventing silent hard-crashes.

### C. SQL Injection & Auto-Mapping in `Db.cs` (Pass)
* **Component:** `src/Flash.Sdk/Db.cs`
* **Details:** The new `Db.Query<T>` Dapper-style rows mapper normalization strips underscores and processes case-insensitively using stack-allocated char spans, which is safe and has zero performance overhead. Parameters are strictly bound as `@p0`, `@p1` to protect against SQL injections.
