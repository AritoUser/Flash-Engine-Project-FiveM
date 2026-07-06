# Bug: Spatial Hashing Cell Registry Retains Empty Lists Indefinitely, Causing Memory Leak

## Component
* **Path:** `src/core/grid.zig`
* **Vulnerability Type:** Memory Leak / Resource Leak
* **Severity:** Medium

---

## Description

The 2D spatial grid implementation (`grid.zig`) tracks entity positions using a spatial hashing structure:
* `entities`: A hash map of `u64` (entity ID) to `Entity` (coordinates, priority, cell key).
* `cells`: A hash map of `u64` (cell key) to `ArrayList(u64)` (list of entity IDs in that cell).

When an entity is inserted or updated via `insert`, its cell is calculated. If its cell changes, it is removed from the old cell via `removeFromCell` and added to the new cell via `addToCell`.

However, `removeFromCell` does not clean up empty cell lists:

```zig
fn removeFromCell(cell: u64, id: u64) void {
    if (cells.getPtr(cell)) |list| {
        var i: usize = 0;
        while (i < list.items.len) : (i += 1) {
            if (list.items[i] == id) {
                _ = list.swapRemove(i);
                return;
            }
        }
    }
}
```

When the last entity is removed from a cell, the `ArrayList` in `cells` becomes empty (`items.len == 0`), but:
1. The `ArrayList` is **never deinitialized** (its allocated capacity is leaked in the allocator).
2. The key-value entry for the cell is **never removed** from the `cells` AutoHashMap.

As vehicles, players, and NPCs move dynamically across the vast GTA map (covering several square kilometers), entities constantly trigger cell crossings. Over time, the server will accumulate thousands of empty cells in the `cells` map, leading to a **continuous memory creep (leak)** on long-running gameservers.

---

## Proposed Fix

Modify `removeFromCell` to deallocate the list and remove the cell key from the `cells` map if it becomes empty:

```zig
fn removeFromCell(cell: u64, id: u64) void {
    if (cells.getPtr(cell)) |list| {
        var i: usize = 0;
        while (i < list.items.len) : (i += 1) {
            if (list.items[i] == id) {
                _ = list.swapRemove(i);
                if (list.items.len == 0) {
                    list.deinit(alloc);
                    _ = cells.remove(cell);
                }
                return;
            }
        }
    }
}
```
*(Note: Use `list.deinit(alloc)` or `list.deinit()` depending on the exact compiler signature matched by `clear()` in `grid.zig`.)*
