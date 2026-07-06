# Bug: Use-After-Free Dangling Active Runtime Pointer Left in Zig Core After Resource Destroy()

## Component
* **Path:** `src/component/src/FlashRuntime.cpp`
* **Vulnerability Type:** Use-After-Free (UAF) / Dangling Pointer
* **Severity:** High / Security

---

## Description

In the Flash-Engine, native calls are routed through the Zig core using the active runtime handle. The runtime handle is set before invoking any resource code so that native calls have the correct resource context.

During resource destruction, `FlashRuntime::Destroy()` runs:

```cpp
result_t FlashRuntime::Destroy()
{
	// 1) notify Zig core. onDestroy sets the active handle in Zig to null
	if (s_onDestroy)
	{
		s_onDestroy(this);
	}

	if (auto* resource = static_cast<fx::Resource*>(m_parentObject))
	{
		// 2) set active runtime to this (dangling overwrite!)
		flash_core_set_active_runtime(this);
		const std::string& resourceName = resource->GetName();
		int32_t unloadResult = flash_core_unload_resource(resourceName.c_str());
		trace("[Flash-Engine] Resource '%s' unloaded -> %d.\n", resourceName.c_str(), unloadResult);
	}

	m_scriptHost = nullptr;
	return FX_S_OK;
}
```

### The Vulnerability
1. `s_onDestroy(this)` correctly invokes Zig's `onDestroy` callback, which sets the active runtime handle to `null`.
2. `Destroy()` then calls `flash_core_set_active_runtime(this)` to ensure that C# resource stop hooks run under the resource's environment. This overwrites the `null` handle in the Zig core with `this`.
3. After `flash_core_unload_resource` returns, `Destroy()` exits without resetting the active runtime pointer.
4. The FXServer component host deletes the `FlashRuntime` instance, freeing its memory.
5. The Zig core is left with a dangling active runtime handle pointing to the deleted `FlashRuntime`.

If any other component or thread subsequently queries the active runtime or attempts to invoke a native when no new active runtime is explicitly set, it will attempt to access the freed memory of the deleted `FlashRuntime`, causing a use-after-free crash (Access Violation) or memory corruption.

---

## Proposed Fix

Reset the active runtime pointer to `nullptr` at the end of `FlashRuntime::Destroy()` to prevent it from pointing to freed memory:

```diff
 	if (auto* resource = static_cast<fx::Resource*>(m_parentObject))
 	{
 		flash_core_set_active_runtime(this);
 		const std::string& resourceName = resource->GetName();
 		int32_t unloadResult = flash_core_unload_resource(resourceName.c_str());
 		trace("[Flash-Engine] Resource '%s' unloaded -> %d.\n", resourceName.c_str(), unloadResult);
 	}
 
+	flash_core_set_active_runtime(nullptr);
 	m_scriptHost = nullptr;
 	return FX_S_OK;
 }
```
