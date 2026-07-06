using System;
using System.Collections.Generic;

namespace Flash;

// =====================================================================================
//  Transactional inventory -- slim facade over the un-dupeable store in the core (#46).
//
//  The native core owns the live item counts and makes every check-and-decrement ATOMIC
//  under a lock, so "withdraw more than exists" is mathematically impossible — from ANY
//  thread. That matters because Flash's C# runs on the script thread but async work /
//  Task.Run can fire inventory ops from thread-pool threads, which is exactly where the
//  classic spam-click dupe races. These calls are therefore SAFE to call off-thread (the
//  whole point) — unlike the script-thread-only registries (Exports/State).
//
//  MODEL: you own item TYPES (names, weight, stack rules) and DB persistence in your
//  resource; the core owns the counts. Containers are addressed by a u64 id (a player's
//  cid, a trunk plate hash, a stash id) and items by a stable u32 name hash — Item(name)
//  computes it so callers stay in strings.
// =====================================================================================

/// <summary>Server-authoritative, atomic (un-dupeable) inventory counts, backed by the
/// native core. Container ids are your own u64 keys (cid / trunk / stash); item ids are
/// derived from a name via <see cref="Item"/>. Safe to call from any thread. (#46)</summary>
public static unsafe class Inventory
{
    // Wired from the FlashApi contract by the host (FlashBridge.Initialize). take/move
    // return the core's Zig-bool as a byte (0/1).
    internal static delegate* unmanaged<ulong, uint, uint, void> Give_;
    internal static delegate* unmanaged<ulong, uint, uint, byte> Take_;
    internal static delegate* unmanaged<ulong, ulong, uint, uint, byte> Move_;
    internal static delegate* unmanaged<ulong, uint, uint, void> Set_;
    internal static delegate* unmanaged<ulong, uint, uint> Count_;
    internal static delegate* unmanaged<ulong, uint*, uint*, int, int> Snapshot_;
    internal static delegate* unmanaged<ulong, void> Clear_;

    /// <summary>Stable 32-bit id for an item name (case-insensitive, FNV-1a). Use it if you
    /// want to pre-compute ids; the string overloads call this for you.</summary>
    public static uint Item(string name)
    {
        // FNV-1a over the lower-cased UTF-8-ish chars — deterministic across runs/machines
        // so persisted counts stay addressable. Never 0 (0 is a fine hash, kept as-is).
        uint h = 2166136261u;
        foreach (char c in name)
        {
            uint x = (uint)char.ToLowerInvariant(c);
            h = (h ^ x) * 16777619u;
        }
        return h;
    }

    /// <summary>Credits <paramref name="qty"/> of an item into a container (loot/purchase/
    /// DB-load delta). Atomic.</summary>
    public static void Give(ulong container, string item, uint qty)
    {
        if (Give_ != null) Give_(container, Item(item), qty);
    }

    /// <summary>Consumes <paramref name="qty"/> of an item. Returns true only if the
    /// container held at least that many (then it was atomically removed); false leaves
    /// the count untouched. The un-dupeable "destroy/consume" primitive.</summary>
    public static bool Take(ulong container, string item, uint qty)
        => Take_ != null && Take_(container, Item(item), qty) != 0;

    /// <summary>Moves <paramref name="qty"/> of an item from one container to another,
    /// atomically. Returns true only if the source held enough (then moved); false changes
    /// nothing. The un-dupeable "move/trade" primitive — a second concurrent move of the
    /// same last item loses.</summary>
    public static bool Move(ulong from, ulong to, string item, uint qty)
        => Move_ != null && Move_(from, to, Item(item), qty) != 0;

    /// <summary>Sets the absolute count of an item (DB load — no add, so a double load
    /// can't dupe). qty 0 removes the entry.</summary>
    public static void Set(ulong container, string item, uint qty)
    {
        if (Set_ != null) Set_(container, Item(item), qty);
    }

    /// <summary>The current count of an item in a container.</summary>
    public static uint Count(ulong container, string item)
        => Count_ != null ? Count_(container, Item(item)) : 0;

    /// <summary>A snapshot of a container as (itemId → count) — for persisting to the DB.
    /// Keys are the item-name hashes (<see cref="Item"/>); keep your own name↔id map if you
    /// need the names back. Empty if the container is unknown. The buffer grows automatically
    /// to fit ALL items — no silent truncation, whatever <paramref name="max"/> starts at. (#208)</summary>
    public static Dictionary<uint, uint> Snapshot(ulong container, int max = 256)
    {
        var result = new Dictionary<uint, uint>();
        if (Snapshot_ == null || max <= 0) return result;

        // The core returns the TOTAL item count. If it exceeds the buffer, the write was
        // truncated -> retry once with a buffer sized to the total (a container can't grow
        // between the two calls faster than we resize meaningfully; a second overshoot just
        // loops). This prevents SaveAsync from deleting the items it failed to read.
        int cap = max;
        while (true)
        {
            var items = new uint[cap];
            var counts = new uint[cap];
            int total;
            fixed (uint* pi = items)
            fixed (uint* pc = counts)
            {
                total = Snapshot_(container, pi, pc, cap);
            }
            int written = Math.Min(total, cap);
            if (total <= cap)
            {
                for (int i = 0; i < written; i++) result[items[i]] = counts[i];
                return result;
            }
            cap = total; // buffer was too small -> size exactly and read again
        }
    }

    /// <summary>Drops a whole container from the store (trunk despawned / stash unloaded).
    /// Persist first if you need the state.</summary>
    public static void Clear(ulong container)
    {
        if (Clear_ != null) Clear_(container);
    }

    // --- id-based overloads (for persistence: Snapshot returns hashes, load them back
    //     without re-hashing names). ---------------------------------------------------

    /// <summary>Credits an item by its precomputed id (<see cref="Item"/> / snapshot key).</summary>
    public static void GiveById(ulong container, uint itemId, uint qty)
    {
        if (Give_ != null) Give_(container, itemId, qty);
    }

    /// <summary>Sets the absolute count of an item by id (DB load).</summary>
    public static void SetById(ulong container, uint itemId, uint qty)
    {
        if (Set_ != null) Set_(container, itemId, qty);
    }

    /// <summary>The current count of an item by id.</summary>
    public static uint CountById(ulong container, uint itemId)
        => Count_ != null ? Count_(container, itemId) : 0;

    /// <summary>Atomically takes an item by id.</summary>
    public static bool TakeById(ulong container, uint itemId, uint qty)
        => Take_ != null && Take_(container, itemId, qty) != 0;

    /// <summary>Atomically moves an item by id.</summary>
    public static bool MoveById(ulong from, ulong to, uint itemId, uint qty)
        => Move_ != null && Move_(from, to, itemId, qty) != 0;
}
