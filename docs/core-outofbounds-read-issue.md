# Bug: Out-of-Bounds Stack Read in coreInvokeObject When Native Argument Count Exceeds 32

## Component
* **Path:** `src/core/root.zig`
* **Vulnerability Type:** Buffer Overflow / Out-of-Bounds Read / Memory Corruption
* **Severity:** High

---

## Description

The `flash_core` Zig module contains the function `coreInvokeObject`, which marshals parameters from the .NET boundary into the native C++ runtime context for natives returning objects (such as `GET_STATE_BAG_VALUE`):

```zig
fn coreInvokeObject(hash: u64, args: [*c]const usize, n: c_int, out_ptr: *u64, out_len: *u64) callconv(.c) void {
    const len: usize = @intCast(n);
    var raw: [32]?*anyopaque = undefined;
    var i: usize = 0;
    while (i < len and i < 32) : (i += 1) raw[i] = @ptrFromInt(args[i]);
    c.Runtime_InvokeNativeObject(natives.getHandle(), hash, &raw, n, out_ptr, out_len);
}
```

### The Vulnerability
1. A stack-allocated buffer `raw` of size `32` is declared: `var raw: [32]?*anyopaque = undefined;`.
2. The loop populates it up to `32` items: `while (i < len and i < 32)`.
3. If the incoming parameter count `n` is greater than 32, the array population stops at index 31.
4. However, the subsequent C++ call passes the original count `n`:
   ```zig
   c.Runtime_InvokeNativeObject(natives.getHandle(), hash, &raw, n, out_ptr, out_len);
   ```
5. The C++ shim reads `n` arguments from the `&raw` pointer. Since `raw` is a stack array containing only 32 elements, the C++ code will perform an out-of-bounds read on the stack of the active thread.

This results in:
* **Undefined behavior** due to reading uninitialized stack frame values as pointers.
* **Access Violation crashes** of FXServer if the out-of-bounds memory points to unmapped virtual memory.
* **Information exposure / stack memory corruption** if the C++ side uses the leaked stack pointers.

---

## Proposed Fix

Enforce a safety limit of 32 arguments at runtime and prevent the out-of-bounds call entirely:

```zig
fn coreInvokeObject(hash: u64, args: [*c]const usize, n: c_int, out_ptr: *u64, out_len: *u64) callconv(.c) void {
    if (n > 32) {
        std.log.err("coreInvokeObject: argument count {d} exceeds maximum limit of 32", .{n});
        out_ptr.* = 0;
        out_len.* = 0;
        return;
    }
    const len: usize = @intCast(n);
    var raw: [32]?*anyopaque = undefined;
    var i: usize = 0;
    while (i < len) : (i += 1) raw[i] = @ptrFromInt(args[i]);
    c.Runtime_InvokeNativeObject(natives.getHandle(), hash, &raw, n, out_ptr, out_len);
}
```
Additionally, `i < 32` check inside the loop is no longer necessary once `n > 32` is checked up front, which makes the code cleaner and safer.
