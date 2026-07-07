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
//
//  HARDENING (core contract v14, #213/#240/#242/#243/#245): the core additionally owns
//    - an ITEM REGISTRY: once RegisterItem pushed the first valid item, every unknown
//      hash is rejected natively BEFORE any allocation — no cheat-minted items, no
//      hash-spam memory growth;
//    - CONTAINER LIMITS (max weight/slots), checked under the same lock as the mutation
//      (a C#-side check would be check-then-act across the native boundary = racy);
//    - CONTAINER FLAGS: busy (frozen during DB I/O) and locked.
//  The Try* methods surface the rejection REASON (InvResult); the classic bool/void
//  methods stay source-compatible and simply collapse the reason to success/failure.
// =====================================================================================

/// <summary>Why an inventory transaction was rejected (or <see cref="Ok"/>). Values 0-6
/// mirror the native core's result codes byte-for-byte; the higher values are produced
/// on the C# side before the native call. (#213/#235/#240/#242)</summary>
public enum InvResult : byte
{
    /// <summary>Transaction executed (a qty of 0 is a documented no-op "Ok").</summary>
    Ok = 0,
    /// <summary>The source container holds fewer than the requested quantity.</summary>
    Insufficient = 1,
    /// <summary>The target container would exceed its maximum weight. (#213)</summary>
    WeightExceeded = 2,
    /// <summary>The target container would exceed its maximum slot count. (#213)</summary>
    SlotsExceeded = 3,
    /// <summary>The container is locked. (#224)</summary>
    Locked = 4,
    /// <summary>The container is frozen during a DB load/save. Retry shortly. (#240)</summary>
    Busy = 5,
    /// <summary>The item is not in the item registry — only registered items can ever
    /// enter a container or the database. (#242/#245)</summary>
    Unregistered = 6,
    /// <summary>The quantity failed the sanity cap: anything above
    /// <see cref="Inventory.MaxQty"/> is rejected — a negative int cast to uint lands
    /// here instead of minting 4 billion items. (#243)</summary>
    InvalidQuantity = 7,
    /// <summary>The item's categories don't match the target container's filter
    /// (key ring / weapon case style special containers). (#218)</summary>
    CategoryRejected = 8,
    /// <summary>The unique instance id already exists — an instance can never exist
    /// twice, which is exactly what kills unique-item dupes at the root. (#214)</summary>
    DuplicateInstance = 9,
    /// <summary>A batch exceeded <see cref="Inventory.MaxBatch"/> ops per side. (#233)</summary>
    BatchTooLarge = 10,
    /// <summary>Internal core error (allocation failure) — the transaction did not run.</summary>
    InternalError = 11,
    /// <summary>The native core is not wired up (no FlashApi yet).</summary>
    Unavailable = 255,
}

/// <summary>One leg of an atomic batch: qty of an item in/out of a container.
/// Build item ids with <see cref="Inventory.Item"/>. (#233)</summary>
public readonly record struct InvOp(ulong Container, uint ItemId, uint Qty);

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
    // Hardening v14 (#213/#240/#242/#245): registry push, limits, flag bits, and the
    // result-code transaction variants (u8 = InvResult).
    internal static delegate* unmanaged<uint*, uint*, int, void> RegisterItems_;
    internal static delegate* unmanaged<ulong, ulong, uint, void> SetLimits_;
    internal static delegate* unmanaged<ulong, uint, byte, void> SetFlag_;
    internal static delegate* unmanaged<ulong, uint, uint, byte> Give2_;
    internal static delegate* unmanaged<ulong, uint, uint, byte> Take2_;
    internal static delegate* unmanaged<ulong, ulong, uint, uint, byte> Move2_;
    internal static delegate* unmanaged<ulong, ulong> Weight_;
    // Features v15 (#214/#218/#233): categories, atomic batch, unique instances.
    internal static delegate* unmanaged<uint*, uint*, uint*, int, void> RegisterItems2_;
    internal static delegate* unmanaged<ulong, uint, void> SetFilter_;
    internal static delegate* unmanaged<ulong*, uint*, uint*, int, ulong*, uint*, uint*, int, byte> Batch_;
    internal static delegate* unmanaged<ulong, ulong, uint, byte, byte> UniqueAdd_;
    internal static delegate* unmanaged<ulong, byte> UniqueRemove_;
    internal static delegate* unmanaged<ulong, ulong, byte> UniqueMove_;
    internal static delegate* unmanaged<ulong, ulong> UniqueContainer_;
    internal static delegate* unmanaged<ulong, ulong*, uint*, int, int> UniqueList_;

    // Native flag bits (must match the Zig core's FLAG_* constants).
    private const uint FlagBusy = 1;
    private const uint FlagLocked = 2;

    /// <summary>Sanity cap for a single transaction's quantity. A negative int that
    /// resource code accidentally casts to uint becomes ~4 billion — nothing legitimate
    /// moves a million of anything in one call, so everything above this is rejected as
    /// <see cref="InvResult.InvalidQuantity"/> instead of executing garbage. (#243)</summary>
    public const uint MaxQty = 1_000_000;

    // C#-side mirror of the item registry: hash -> name. Gives friendly errors and lets
    // diagnostics (dev commands, logs) resolve snapshot hashes back to names. The native
    // registry stays the enforcement backstop — this mirror is convenience only.
    private static readonly Dictionary<uint, string> s_names = new();
    private static readonly Dictionary<string, uint> s_hashes = new(StringComparer.OrdinalIgnoreCase);
    // Category mirror (#231/#232): hash -> mask and name -> category names. The MASK is
    // what the core enforces (the filter); the NAMES are what the C# side needs for
    // snapshot queries (FindByCategory) and category ItemHandlers -- without a core round-trip.
    private static readonly Dictionary<uint, uint> s_masks = new();
    private static readonly Dictionary<string, string[]> s_itemCategories = new(StringComparer.OrdinalIgnoreCase);
    // Weight mirror (#231): hash -> grams/unit, for snapshot queries without a core round-trip.
    private static readonly Dictionary<uint, uint> s_weights = new();
    private static readonly object s_regLock = new();

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
    /// DB-load delta). Atomic. Silently drops rejected transactions (registry/limits/flags,
    /// v14) — use <see cref="TryGive"/> when the caller needs the reason.</summary>
    public static void Give(ulong container, string item, uint qty)
        => TryGive(container, item, qty);

    /// <summary>Consumes <paramref name="qty"/> of an item. Returns true only if the
    /// container held at least that many (then it was atomically removed); false leaves
    /// the count untouched. The un-dupeable "destroy/consume" primitive.</summary>
    public static bool Take(ulong container, string item, uint qty)
        => TryTake(container, item, qty) == InvResult.Ok;

    /// <summary>Moves <paramref name="qty"/> of an item from one container to another,
    /// atomically. Returns true only if the source held enough (then moved); false changes
    /// nothing. The un-dupeable "move/trade" primitive — a second concurrent move of the
    /// same last item loses.</summary>
    public static bool Move(ulong from, ulong to, string item, uint qty)
        => TryMove(from, to, item, qty) == InvResult.Ok;

    // --- Try-variants with rejection reason (v14) --------------------------------------

    // Shared pre-flight: quantity sanity cap (#243). The registry/limit/flag checks live
    // in the CORE (same lock as the mutation — checking here would be racy); only the
    // cap is C#-side because the core can't tell a huge-but-honest uint from a cast bug.
    private static InvResult Pre(uint qty)
        => qty > MaxQty ? InvResult.InvalidQuantity : InvResult.Ok;

    /// <summary>Like <see cref="Give"/>, but returns WHY a transaction was rejected
    /// (weight/slots/locked/busy/unregistered — <see cref="InvResult"/>).</summary>
    public static InvResult TryGive(ulong container, string item, uint qty)
        => TryGiveById(container, Item(item), qty);

    /// <summary>Like <see cref="Take"/>, but returns the rejection reason.</summary>
    public static InvResult TryTake(ulong container, string item, uint qty)
        => TryTakeById(container, Item(item), qty);

    /// <summary>Like <see cref="Move"/>, but returns the rejection reason.</summary>
    public static InvResult TryMove(ulong from, ulong to, string item, uint qty)
        => TryMoveById(from, to, Item(item), qty);

    /// <summary><see cref="TryGive"/> by precomputed item id.</summary>
    public static InvResult TryGiveById(ulong container, uint itemId, uint qty)
    {
        var pre = Pre(qty);
        if (pre != InvResult.Ok) return pre;
        if (Give2_ == null) return InvResult.Unavailable;
        var r = (InvResult)Give2_(container, itemId, qty);
        if (r == InvResult.Ok && qty > 0) RaiseGiven(container, itemId, qty);
        return r;
    }

    /// <summary><see cref="TryTake"/> by precomputed item id.</summary>
    public static InvResult TryTakeById(ulong container, uint itemId, uint qty)
    {
        var pre = Pre(qty);
        if (pre != InvResult.Ok) return pre;
        if (Take2_ == null) return InvResult.Unavailable;
        var r = (InvResult)Take2_(container, itemId, qty);
        if (r == InvResult.Ok && qty > 0) RaiseTaken(container, itemId, qty);
        return r;
    }

    /// <summary><see cref="TryMove"/> by precomputed item id.</summary>
    public static InvResult TryMoveById(ulong from, ulong to, uint itemId, uint qty)
    {
        var pre = Pre(qty);
        if (pre != InvResult.Ok) return pre;
        if (Move2_ == null) return InvResult.Unavailable;
        var r = (InvResult)Move2_(from, to, itemId, qty);
        if (r == InvResult.Ok && qty > 0) RaiseMoved(from, to, itemId, qty);
        return r;
    }

    // --- Item registry (#242/#245) ------------------------------------------------------

    /// <summary>Registers a valid item (name + per-unit weight in kg). The FIRST registered
    /// item arms the native filter: from then on, every transaction with an unregistered
    /// item hash is rejected inside the core before it can allocate anything — cheat-spawned
    /// fantasy items and hash-spam memory attacks die at the native boundary. Call for every
    /// item your server knows at startup (flash-core does this from its item catalog);
    /// additional resources may add their items during their own OnStart.</summary>
    public static void RegisterItem(string name, float weightKg = 0f)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        uint hash = Item(name);
        lock (s_regLock)
        {
            // FNV-1a collisions are astronomically unlikely at server-catalog scale, but a
            // silent collision would let two names share one count — make it loud instead.
            if (s_names.TryGetValue(hash, out var existing) &&
                !string.Equals(existing, name, StringComparison.OrdinalIgnoreCase))
            {
                Log.Error($"Inventory.RegisterItem: hash collision between '{existing}' and '{name}' (0x{hash:x8}) — rename one of them!");
                return;
            }
            s_names[hash] = name;
            s_hashes[name] = hash;
            s_weights[hash] = (uint)Math.Max(0, Math.Round(weightKg * 1000f));
        }
        if (RegisterItems_ != null)
        {
            uint weightG = (uint)Math.Max(0, Math.Round(weightKg * 1000f));
            uint* items = stackalloc uint[1] { hash };
            uint* weights = stackalloc uint[1] { weightG };
            RegisterItems_(items, weights, 1);
        }
    }

    /// <summary>True if the item name was registered via <see cref="RegisterItem(string, float)"/>.</summary>
    public static bool IsRegistered(string name)
    {
        lock (s_regLock) return s_hashes.ContainsKey(name);
    }

    /// <summary>Resolves a snapshot hash back to its registered item name (null if the
    /// hash is unknown — e.g. a pre-registry DB row). Diagnostics/UI convenience.</summary>
    public static string? NameOf(uint itemId)
    {
        lock (s_regLock) return s_names.TryGetValue(itemId, out var n) ? n : null;
    }

    /// <summary>All registered item names (copy). Dev tooling/diagnostics.</summary>
    public static List<string> RegisteredItems()
    {
        lock (s_regLock) return new List<string>(s_hashes.Keys);
    }

    // --- Container limits & flags (#213/#224/#240) --------------------------------------

    /// <summary>Sets a container's capacity limits, enforced atomically in the core under
    /// the same lock as every transaction (no check-then-act race). 0 = unlimited. Limits
    /// survive <see cref="Clear"/> (a DB reload must not drop a trunk's config). (#213)</summary>
    public static void SetLimits(ulong container, float maxWeightKg, uint maxSlots)
    {
        if (SetLimits_ == null) return;
        ulong weightG = (ulong)Math.Max(0, Math.Round(maxWeightKg * 1000f));
        SetLimits_(container, weightG, maxSlots);
    }

    /// <summary>The container's current total weight in kg (sum of registered per-unit
    /// weights; unregistered legacy items weigh 0). (#213)</summary>
    public static float GetWeight(ulong container)
        => Weight_ != null ? Weight_(container) / 1000f : 0f;

    /// <summary>Locks/unlocks a container: while locked, every give/take/move is rejected
    /// with <see cref="InvResult.Locked"/>. WHO may unlock (key items, ACLs) is the
    /// gameplay layer's decision — this is only the enforcement bit. (#224)</summary>
    public static void SetLocked(ulong container, bool locked)
    {
        if (SetFlag_ != null) SetFlag_(container, FlagLocked, locked ? (byte)1 : (byte)0);
    }

    /// <summary>Freezes/unfreezes a container for DB I/O: while busy, give/take/move are
    /// rejected with <see cref="InvResult.Busy"/>, but <see cref="Set"/>/<see cref="Clear"/>
    /// (the load path's own tools) keep working. Intended for persistence layers around
    /// their load/save window — ALWAYS clear it in a finally. The core auto-clears a busy
    /// flag older than 30 s as a crash failsafe. (#240)</summary>
    public static void SetBusy(ulong container, bool busy)
    {
        if (SetFlag_ != null) SetFlag_(container, FlagBusy, busy ? (byte)1 : (byte)0);
    }

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
    /// Persist first if you need the state. Also clears the metadata of unique instances
    /// in the container, and recursively clears LINKED child containers (bags inside a
    /// cleared player inventory don't linger in memory, #216).</summary>
    public static void Clear(ulong container)
    {
        // Linked children first (their instance metadata is keyed to their own containers).
        foreach (ulong child in TakeChildrenOf(container))
            Clear(child); // recursion is bounded by the cycle guard at link time

        if (UniqueList_ != null)
            foreach (var (inst, _) in ListUnique(container))
                s_metadata.TryRemove(inst, out _);
        if (Clear_ != null) Clear_(container);
    }

    // --- id-based overloads (for persistence: Snapshot returns hashes, load them back
    //     without re-hashing names). ---------------------------------------------------

    /// <summary>Credits an item by its precomputed id (<see cref="Item"/> / snapshot key).</summary>
    public static void GiveById(ulong container, uint itemId, uint qty)
        => TryGiveById(container, itemId, qty);

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
        => TryTakeById(container, itemId, qty) == InvResult.Ok;

    /// <summary>Atomically moves an item by id.</summary>
    public static bool MoveById(ulong from, ulong to, uint itemId, uint qty)
        => TryMoveById(from, to, itemId, qty) == InvResult.Ok;

    // === Change events (#220) ===========================================================
    // Raised AFTER a successful transaction, synchronously ON THE CALLING THREAD (which
    // may be a thread-pool thread — inventory ops are thread-safe by design). Handlers
    // that touch script-thread-only APIs must hop themselves (e.g. via Async). Raised
    // from the C# wrappers, NOT from inside the native lock: every mutation flows through
    // these wrappers, so coverage is identical — without the deadlock/reentrancy risk of
    // native callbacks. A throwing handler is logged and cannot break the transaction.

    /// <summary>An item was credited: (container, itemId, qty). Also fires for each give
    /// leg of a batch/craft/trade.</summary>
    public static event Action<ulong, uint, uint>? OnItemGiven;
    /// <summary>An item was consumed: (container, itemId, qty). Also fires for each take
    /// leg of a batch/craft/trade.</summary>
    public static event Action<ulong, uint, uint>? OnItemTaken;
    /// <summary>An item was moved: (from, to, itemId, qty).</summary>
    public static event Action<ulong, ulong, uint, uint>? OnItemMoved;
    /// <summary>A unique instance changed location: (instance, fromContainer, toContainer).
    /// from = 0 on creation, to = 0 on removal.</summary>
    public static event Action<ulong, ulong, ulong>? OnUniqueMoved;

    private static void RaiseGiven(ulong c, uint item, uint qty)
    {
        try { OnItemGiven?.Invoke(c, item, qty); }
        catch (Exception ex) { Log.Error($"Inventory.OnItemGiven handler threw: {ex.Message}"); }
    }
    private static void RaiseTaken(ulong c, uint item, uint qty)
    {
        try { OnItemTaken?.Invoke(c, item, qty); }
        catch (Exception ex) { Log.Error($"Inventory.OnItemTaken handler threw: {ex.Message}"); }
    }
    private static void RaiseMoved(ulong from, ulong to, uint item, uint qty)
    {
        try { OnItemMoved?.Invoke(from, to, item, qty); }
        catch (Exception ex) { Log.Error($"Inventory.OnItemMoved handler threw: {ex.Message}"); }
    }
    private static void RaiseUnique(ulong inst, ulong from, ulong to)
    {
        try { OnUniqueMoved?.Invoke(inst, from, to); }
        catch (Exception ex) { Log.Error($"Inventory.OnUniqueMoved handler threw: {ex.Message}"); }
    }

    // === Categories (#218) ==============================================================
    // Categories are bits (max 32): the container filter is a single AND test in the core
    // under the transaction lock. C# assigns the bits automatically in registration order;
    // the names are purely the developer-facing surface.

    private static readonly Dictionary<string, int> s_catBits = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Bitmask for the given category names, auto-registering unknown ones.
    /// Throws when a 33rd distinct category is requested (bitmask limit — restructure
    /// categories rather than exceeding 32).</summary>
    public static uint CategoryMask(params string[] categories)
    {
        uint mask = 0;
        lock (s_regLock)
        {
            foreach (var cat in categories)
            {
                if (string.IsNullOrWhiteSpace(cat)) continue;
                if (!s_catBits.TryGetValue(cat, out int bit))
                {
                    if (s_catBits.Count >= 32)
                        throw new InvalidOperationException($"Inventory: more than 32 item categories ('{cat}' no longer fits in the bitmask).");
                    bit = s_catBits.Count;
                    s_catBits[cat] = bit;
                }
                mask |= 1u << bit;
            }
        }
        return mask;
    }

    /// <summary>Registers an item WITH categories (e.g. "weapon", "small"). Categories
    /// feed container filters (<see cref="SetFilter(ulong, string[])"/>). (#218/#242)</summary>
    public static void RegisterItem(string name, float weightKg, params string[] categories)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        uint mask = CategoryMask(categories);
        uint hash = Item(name);
        lock (s_regLock)
        {
            if (s_names.TryGetValue(hash, out var existing) &&
                !string.Equals(existing, name, StringComparison.OrdinalIgnoreCase))
            {
                Log.Error($"Inventory.RegisterItem: hash collision between '{existing}' and '{name}' (0x{hash:x8}) — rename one of them!");
                return;
            }
            s_names[hash] = name;
            s_hashes[name] = hash;
        }
        lock (s_regLock)
        {
            s_masks[hash] = mask;
            s_itemCategories[name] = (string[])categories.Clone();
            s_weights[hash] = (uint)Math.Max(0, Math.Round(weightKg * 1000f));
        }
        if (RegisterItems2_ != null)
        {
            uint weightG = (uint)Math.Max(0, Math.Round(weightKg * 1000f));
            uint* items = stackalloc uint[1] { hash };
            uint* weights = stackalloc uint[1] { weightG };
            uint* masks = stackalloc uint[1] { mask };
            RegisterItems2_(items, weights, masks, 1);
        }
    }

    /// <summary>The registered category names of an item (empty when none/unknown).</summary>
    public static string[] CategoriesOf(string itemName)
    {
        lock (s_regLock) return s_itemCategories.TryGetValue(itemName, out var c) ? c : Array.Empty<string>();
    }

    /// <summary>True if the item carries the given category. (#218/#232)</summary>
    public static bool HasCategory(uint itemId, string category)
    {
        uint want = CategoryMask(category);
        lock (s_regLock) return s_masks.TryGetValue(itemId, out uint m) && (m & want) != 0;
    }

    /// <summary>Restricts a container to items carrying at least one of the given
    /// categories (key ring, weapon case, glovebox...). Empty = filter off. Enforced
    /// natively under the transaction lock; rejected as
    /// <see cref="InvResult.CategoryRejected"/>. (#218)</summary>
    public static void SetFilter(ulong container, params string[] allowedCategories)
        => SetFilterMask(container, allowedCategories.Length == 0 ? 0u : CategoryMask(allowedCategories));

    /// <summary>Filter by precomputed bitmask (<see cref="CategoryMask"/>); 0 = off.</summary>
    public static void SetFilterMask(ulong container, uint mask)
    {
        if (SetFilter_ != null) SetFilter_(container, mask);
    }

    // === Atomic batch + transaction scope (#233) ========================================

    /// <summary>Max ops per batch side (mirrors the core's MAX_BATCH — bounds native
    /// lock hold time).</summary>
    public const int MaxBatch = 64;

    /// <summary>Executes several takes and gives as ONE all-or-nothing transaction under
    /// a single native lock. Either every op applies or none does — the foundation for
    /// crafting (<see cref="Craft"/>), trades (<see cref="ExecuteTrade"/>) and
    /// <see cref="BeginTransaction"/>. (#233)</summary>
    public static InvResult ExecuteBatch(IReadOnlyList<InvOp> takes, IReadOnlyList<InvOp> gives)
    {
        if (Batch_ == null) return InvResult.Unavailable;
        if (takes.Count > MaxBatch || gives.Count > MaxBatch) return InvResult.BatchTooLarge;
        foreach (var op in takes) if (op.Qty > MaxQty) return InvResult.InvalidQuantity;
        foreach (var op in gives) if (op.Qty > MaxQty) return InvResult.InvalidQuantity;

        ulong* tc = stackalloc ulong[Math.Max(1, takes.Count)];
        uint* ti = stackalloc uint[Math.Max(1, takes.Count)];
        uint* tq = stackalloc uint[Math.Max(1, takes.Count)];
        ulong* gc = stackalloc ulong[Math.Max(1, gives.Count)];
        uint* gi = stackalloc uint[Math.Max(1, gives.Count)];
        uint* gq = stackalloc uint[Math.Max(1, gives.Count)];
        for (int i = 0; i < takes.Count; i++) { tc[i] = takes[i].Container; ti[i] = takes[i].ItemId; tq[i] = takes[i].Qty; }
        for (int i = 0; i < gives.Count; i++) { gc[i] = gives[i].Container; gi[i] = gives[i].ItemId; gq[i] = gives[i].Qty; }

        var r = (InvResult)Batch_(tc, ti, tq, takes.Count, gc, gi, gq, gives.Count);
        if (r == InvResult.Ok)
        {
            foreach (var op in takes) if (op.Qty > 0) RaiseTaken(op.Container, op.ItemId, op.Qty);
            foreach (var op in gives) if (op.Qty > 0) RaiseGiven(op.Container, op.ItemId, op.Qty);
        }
        return r;
    }

    /// <summary>Starts a staged inventory transaction (unit of work): queue takes/gives,
    /// then <see cref="InventoryTransaction.Commit"/> applies them atomically. Disposal
    /// without commit discards the staging — nothing was ever applied, so there is
    /// nothing to roll back (deliberately staging+commit, NOT execute+compensate: no
    /// intermediate state is ever visible, and an "undo" can't fail halfway). (#233)</summary>
    public static InventoryTransaction BeginTransaction() => new();

    // === Crafting (#223) ================================================================
    // Recipes live in C# (data); execution is the native batch: ingredient check,
    // deduction and product credit under ONE lock -> spam-clicking can never craft twice
    // or lose ingredients without a product.

    private sealed record Recipe(List<InvOp> Ingredients, uint OutputQty);
    private static readonly Dictionary<uint, Recipe> s_recipes = new();

    /// <summary>Registers a crafting recipe: product name + (ingredient name → qty).
    /// Re-registering a product replaces its recipe.</summary>
    public static void RegisterRecipe(string product, IReadOnlyDictionary<string, uint> ingredients, uint outputQty = 1)
    {
        var list = new List<InvOp>(ingredients.Count);
        foreach (var kv in ingredients) list.Add(new InvOp(0, Item(kv.Key), kv.Value));
        lock (s_regLock) s_recipes[Item(product)] = new Recipe(list, outputQty);
    }

    /// <summary>Atomically crafts a product in a container: all ingredients are consumed
    /// and the product credited in ONE native transaction, or nothing happens
    /// (<see cref="InvResult"/> says why). Unknown recipe → <see cref="InvResult.Unregistered"/>. (#223)</summary>
    public static InvResult TryCraft(ulong container, string product)
    {
        uint productId = Item(product);
        Recipe? recipe;
        lock (s_regLock) s_recipes.TryGetValue(productId, out recipe);
        if (recipe == null) return InvResult.Unregistered;
        var takes = new List<InvOp>(recipe.Ingredients.Count);
        foreach (var ing in recipe.Ingredients) takes.Add(ing with { Container = container });
        return ExecuteBatch(takes, new[] { new InvOp(container, productId, recipe.OutputQty) });
    }

    /// <summary>Bool convenience over <see cref="TryCraft"/>.</summary>
    public static bool Craft(ulong container, string product)
        => TryCraft(container, product) == InvResult.Ok;

    // === Trades (#225) ==================================================================

    /// <summary>Executes a player-to-player trade atomically: validates BOTH offers
    /// against the live counts and swaps them crosswise in ONE native transaction. If
    /// either side no longer holds its offer (dropped/consumed/moved meanwhile — or a
    /// disconnect killed the session before this call), nothing moves. Session flow
    /// (offers, accept buttons, timeouts) is the gameplay layer's job; THIS is the only
    /// step that touches items. Unique items: move them with <see cref="MoveUnique"/>
    /// after a successful stack swap (instance ids can't dupe by design). (#225)</summary>
    public static InvResult ExecuteTrade(
        ulong containerA, ulong containerB,
        IReadOnlyDictionary<string, uint> offerA, IReadOnlyDictionary<string, uint> offerB)
    {
        var takes = new List<InvOp>(offerA.Count + offerB.Count);
        var gives = new List<InvOp>(offerA.Count + offerB.Count);
        foreach (var kv in offerA)
        {
            uint id = Item(kv.Key);
            takes.Add(new InvOp(containerA, id, kv.Value));
            gives.Add(new InvOp(containerB, id, kv.Value));
        }
        foreach (var kv in offerB)
        {
            uint id = Item(kv.Key);
            takes.Add(new InvOp(containerB, id, kv.Value));
            gives.Add(new InvOp(containerA, id, kv.Value));
        }
        return ExecuteBatch(takes, gives);
    }

    // === Unique instances + metadata (#214) =============================================
    // The core owns the LOCATION truth (instance -> container) and moves instances
    // atomically; metadata (durability/serial/lockcode as JSON) lives here in C# and is
    // mirrored to the DB by flash-core. Instance ids are assigned ONLY by the server
    // (cryptographically random u64) -- the id doubles as the serial number (#241).

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, string?> s_metadata = new();

    private static ulong NewInstanceId()
    {
        Span<byte> b = stackalloc byte[8];
        System.Security.Cryptography.RandomNumberGenerator.Fill(b);
        ulong id = BitConverter.ToUInt64(b);
        return id == 0 ? 1 : id; // 0 ist "unbekannt" reserviert
    }

    /// <summary>Creates a unique item instance (weapon/key/bag) in a container with an
    /// optional metadata JSON. Returns the rejection reason and the new server-generated
    /// instance id (0 on failure). The id doubles as the serial number. (#214)</summary>
    public static InvResult TryGiveUnique(ulong container, string item, out ulong instanceId, string? metadataJson = null)
    {
        instanceId = 0;
        if (UniqueAdd_ == null) return InvResult.Unavailable;
        uint itemId = Item(item);
        // Collision retry: practically never needed with 64-bit random ids, but a
        // DuplicateInstance here is definitely an id clash, not a player error.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            ulong id = NewInstanceId();
            var r = (InvResult)UniqueAdd_(container, id, itemId, 0);
            if (r == InvResult.DuplicateInstance) continue;
            if (r == InvResult.Ok)
            {
                instanceId = id;
                s_metadata[id] = metadataJson;
                RaiseUnique(id, 0, container);
            }
            return r;
        }
        return InvResult.DuplicateInstance;
    }

    /// <summary>Convenience over <see cref="TryGiveUnique"/>: instance id, or 0 on rejection.</summary>
    public static ulong GiveUnique(ulong container, string item, string? metadataJson = null)
        => TryGiveUnique(container, item, out ulong id, metadataJson) == InvResult.Ok ? id : 0;

    /// <summary>Destroys/consumes a unique instance (and drops its metadata).</summary>
    public static InvResult RemoveUnique(ulong instanceId)
    {
        if (UniqueRemove_ == null) return InvResult.Unavailable;
        ulong from = UniqueContainer_ != null ? UniqueContainer_(instanceId) : 0;
        var r = (InvResult)UniqueRemove_(instanceId);
        if (r == InvResult.Ok)
        {
            s_metadata.TryRemove(instanceId, out _);
            RaiseUnique(instanceId, from, 0);
        }
        return r;
    }

    /// <summary>Atomically moves a unique instance to another container (target filter/
    /// limits/locks enforced natively). An instance can never exist twice. (#214)</summary>
    public static InvResult MoveUnique(ulong instanceId, ulong toContainer)
    {
        if (UniqueMove_ == null) return InvResult.Unavailable;
        ulong from = UniqueContainer_ != null ? UniqueContainer_(instanceId) : 0;
        var r = (InvResult)UniqueMove_(instanceId, toContainer);
        if (r == InvResult.Ok) RaiseUnique(instanceId, from, toContainer);
        return r;
    }

    /// <summary>The container currently holding the instance (0 = unknown/removed).</summary>
    public static ulong ContainerOfUnique(ulong instanceId)
        => UniqueContainer_ != null ? UniqueContainer_(instanceId) : 0;

    /// <summary>All unique instances in a container as (instanceId, itemId) pairs.
    /// Grows the buffer automatically — no silent truncation (#208 pattern).</summary>
    public static List<(ulong Instance, uint ItemId)> ListUnique(ulong container, int max = 64)
    {
        var result = new List<(ulong, uint)>();
        if (UniqueList_ == null || max <= 0) return result;
        int cap = max;
        while (true)
        {
            var inst = new ulong[cap];
            var items = new uint[cap];
            int total;
            fixed (ulong* pi = inst)
            fixed (uint* pt = items)
            {
                total = UniqueList_(container, pi, pt, cap);
            }
            if (total <= cap)
            {
                for (int i = 0; i < total; i++) result.Add((inst[i], items[i]));
                return result;
            }
            cap = total;
        }
    }

    /// <summary>The instance's metadata JSON (null = none / unknown instance).</summary>
    public static string? GetMetadata(ulong instanceId)
        => s_metadata.TryGetValue(instanceId, out var m) ? m : null;

    /// <summary>Deserialized metadata (default(T) when absent or malformed).</summary>
    public static T? GetMetadata<T>(ulong instanceId)
    {
        var json = GetMetadata(instanceId);
        if (string.IsNullOrEmpty(json)) return default;
        try { return System.Text.Json.JsonSerializer.Deserialize<T>(json); }
        catch { return default; }
    }

    /// <summary>Sets/replaces the instance's metadata JSON (persisted by flash-core on
    /// the next container save).</summary>
    public static void SetMetadata(ulong instanceId, string? metadataJson)
        => s_metadata[instanceId] = metadataJson;

    /// <summary>Serializing convenience over <see cref="SetMetadata(ulong, string?)"/>.</summary>
    public static void SetMetadata<T>(ulong instanceId, T metadata)
        => s_metadata[instanceId] = System.Text.Json.JsonSerializer.Serialize(metadata);

    /// <summary>PERSISTENCE-ONLY (DB load): re-adds a known instance with its stored id
    /// and metadata, bypassing busy/capacity gates like <see cref="Set"/> does (the DB is
    /// the absolute truth on load; the registry still filters). Not for gameplay — new
    /// instances come from <see cref="GiveUnique"/> so ids stay server-generated.</summary>
    public static InvResult RestoreUnique(ulong container, ulong instanceId, uint itemId, string? metadataJson)
    {
        if (UniqueAdd_ == null) return InvResult.Unavailable;
        var r = (InvResult)UniqueAdd_(container, instanceId, itemId, 1);
        if (r == InvResult.Ok) s_metadata[instanceId] = metadataJson;
        return r;
    }

    // === Nested containers (#216) =======================================================
    // Parent-child linking (a backpack INSIDE a player inventory) is pure C# bookkeeping:
    // the core needs no atomicity for it. The link call checks for cycles (A in B in A ->
    // infinite loop) and caps the depth; Clear recursively clears linked children
    // (no RAM leak from orphaned sub-containers).

    private static readonly Dictionary<ulong, HashSet<ulong>> s_children = new();
    private static readonly Dictionary<ulong, ulong> s_parent = new();
    private static readonly object s_nestLock = new();
    private const int MaxNestDepth = 4; // Spieler > Rucksack > Beutel > Etui reicht

    /// <summary>Links a child container (a bag's own container id) under a parent, so a
    /// <see cref="Clear"/> of the parent clears the child too. Rejects self-links, cycles
    /// and nesting deeper than 4 levels; a child can only have one parent. Returns false
    /// with an error log instead of throwing (link calls come from gameplay code). (#216)</summary>
    public static bool LinkSubContainer(ulong parent, ulong child)
    {
        lock (s_nestLock)
        {
            if (parent == child) { Log.Error($"Inventory.LinkSubContainer: {child} cannot contain itself."); return false; }
            if (s_parent.ContainsKey(child)) { Log.Error($"Inventory.LinkSubContainer: {child} haengt bereits unter einem Parent."); return false; }
            // Cycle/depth check: walk up the parent's ancestor chain.
            int depth = 1;
            ulong cur = parent;
            while (s_parent.TryGetValue(cur, out ulong up))
            {
                if (up == child) { Log.Error($"Inventory.LinkSubContainer: Zyklus ({child} ist Vorfahre von {parent})."); return false; }
                cur = up;
                if (++depth >= MaxNestDepth) { Log.Error($"Inventory.LinkSubContainer: Verschachtelung tiefer als {MaxNestDepth} Ebenen."); return false; }
            }
            if (!s_children.TryGetValue(parent, out var set)) s_children[parent] = set = new HashSet<ulong>();
            set.Add(child);
            s_parent[child] = parent;
            return true;
        }
    }

    /// <summary>Removes a parent-child link (bag taken out of the inventory). The child
    /// container itself stays untouched.</summary>
    public static void UnlinkSubContainer(ulong child)
    {
        lock (s_nestLock)
        {
            if (s_parent.Remove(child, out ulong parent) && s_children.TryGetValue(parent, out var set))
            {
                set.Remove(child);
                if (set.Count == 0) s_children.Remove(parent);
            }
        }
    }

    /// <summary>The linked children of a container (copy; empty when none).</summary>
    public static List<ulong> ChildrenOf(ulong parent)
    {
        lock (s_nestLock)
            return s_children.TryGetValue(parent, out var set) ? new List<ulong>(set) : new List<ulong>();
    }

    // Clear helper: ATOMICALLY detach the children and return them (also drops the links --
    // a cleared container leaves no dangling edges).
    private static List<ulong> TakeChildrenOf(ulong parent)
    {
        lock (s_nestLock)
        {
            if (!s_children.Remove(parent, out var set)) return new List<ulong>();
            foreach (ulong child in set) s_parent.Remove(child);
            return new List<ulong>(set);
        }
    }

    // === User-friendly rejection messages (#235) ========================================
    // Codes -> text is DELIBERATELY an overridable table rather than hardcoded strings in
    // the logic: servers localize their messages (SetMessage), the engine ships English
    // defaults as an immediately usable starting point.

    private static readonly Dictionary<InvResult, string> s_messages = new()
    {
        [InvResult.Ok] = "",
        [InvResult.Insufficient] = "Not enough items.",
        [InvResult.WeightExceeded] = "The inventory is too heavy.",
        [InvResult.SlotsExceeded] = "No free slots left.",
        [InvResult.Locked] = "This inventory is locked.",
        [InvResult.Busy] = "Inventory is loading -- try again shortly.",
        [InvResult.Unregistered] = "Unknown item.",
        [InvResult.InvalidQuantity] = "Invalid quantity.",
        [InvResult.CategoryRejected] = "This item does not fit here.",
        [InvResult.DuplicateInstance] = "Item already exists.",
        [InvResult.BatchTooLarge] = "Too many operations at once.",
        [InvResult.InternalError] = "Internal error -- please try again.",
        [InvResult.Unavailable] = "Inventory system unavailable.",
    };

    /// <summary>The player-facing message for a rejection reason. (#235)</summary>
    public static string MessageOf(InvResult result)
    {
        lock (s_messages) return s_messages.TryGetValue(result, out var m) ? m : result.ToString();
    }

    /// <summary>Overrides the message for a rejection reason (localization).</summary>
    public static void SetMessage(InvResult result, string message)
    {
        lock (s_messages) s_messages[result] = message;
    }

    /// <summary>Try-pattern with a ready-to-show error message: one clean if-block
    /// instead of check-then-act chains. (#235)</summary>
    public static bool TryTake(ulong container, string item, uint qty, out string error)
    {
        var r = TryTake(container, item, qty);
        error = r == InvResult.Ok ? "" : MessageOf(r);
        return r == InvResult.Ok;
    }

    /// <summary><see cref="TryTake(ulong, string, uint, out string)"/> for gives.</summary>
    public static bool TryGive(ulong container, string item, uint qty, out string error)
    {
        var r = TryGive(container, item, qty);
        error = r == InvResult.Ok ? "" : MessageOf(r);
        return r == InvResult.Ok;
    }

    /// <summary><see cref="TryTake(ulong, string, uint, out string)"/> for moves.</summary>
    public static bool TryMove(ulong from, ulong to, string item, uint qty, out string error)
    {
        var r = TryMove(from, to, item, qty);
        error = r == InvResult.Ok ? "" : MessageOf(r);
        return r == InvResult.Ok;
    }

    // Internal access for the snapshot queries (#231): weight/mask from the mirror.
    internal static uint WeightOfItem(uint itemId)
    {
        lock (s_regLock) return s_weights.TryGetValue(itemId, out uint w) ? w : 0;
    }

    /// <summary>Fluent configuration for a container: limits, category filter, lock state
    /// in one readable chain, pushed to the core on <see cref="ContainerConfigurationBuilder.Apply"/>. (#230)</summary>
    public static ContainerConfigurationBuilder Configure(ulong container) => new(container);
}

/// <summary>Fluent builder over the container primitives (#230): collects the whole
/// configuration, validates it, and pushes everything in one <see cref="Apply"/> — a
/// half-applied config (exception between two setters) can't happen.</summary>
public sealed class ContainerConfigurationBuilder
{
    private readonly ulong _container;
    private float? _maxWeightKg;
    private uint? _maxSlots;
    private string[]? _categories;
    private bool? _locked;

    internal ContainerConfigurationBuilder(ulong container) => _container = container;

    /// <summary>Maximum total weight in kg (0 = unlimited). (#213)</summary>
    public ContainerConfigurationBuilder WithMaxWeight(float kg)
    {
        if (kg < 0) throw new ArgumentOutOfRangeException(nameof(kg));
        _maxWeightKg = kg;
        return this;
    }

    /// <summary>Maximum distinct item slots (0 = unlimited). (#213)</summary>
    public ContainerConfigurationBuilder WithMaxSlots(uint slots)
    {
        _maxSlots = slots;
        return this;
    }

    /// <summary>Restricts the container to these categories (key ring / weapon case). (#218)</summary>
    public ContainerConfigurationBuilder AllowCategories(params string[] categories)
    {
        _categories = categories;
        return this;
    }

    /// <summary>Initial lock state. (#224)</summary>
    public ContainerConfigurationBuilder Locked(bool locked = true)
    {
        _locked = locked;
        return this;
    }

    /// <summary>Validates and pushes the collected configuration to the core. Only what
    /// was explicitly set is touched — an Apply without WithMaxWeight leaves existing
    /// weight limits alone.</summary>
    public void Apply()
    {
        // Validate first (CategoryMask can throw on >32 categories), THEN push --
        // after the first push nothing may be allowed to fail anymore.
        uint? mask = _categories != null
            ? (_categories.Length == 0 ? 0u : Inventory.CategoryMask(_categories))
            : null;

        if (_maxWeightKg.HasValue || _maxSlots.HasValue)
        {
            // Limits are ONE native slot -> set both values together. If only one is
            // given, the other stays "unlimited" (0) -- documented behaviour; you don't
            // partial-update a single limit across two Apply calls.
            Inventory.SetLimits(_container, _maxWeightKg ?? 0f, _maxSlots ?? 0u);
        }
        if (mask.HasValue) Inventory.SetFilterMask(_container, mask.Value);
        if (_locked.HasValue) Inventory.SetLocked(_container, _locked.Value);
    }
}

/// <summary>Query helpers over inventory snapshots (#231) — standardizes the checks
/// every gameplay script writes by hand (weight sums, ingredient checks, category
/// scans). Allocation-light plain loops; works directly on <see cref="Inventory.Snapshot"/>.</summary>
public static class InventorySnapshotExtensions
{
    /// <summary>Total weight in kg (registered per-unit weights; unknown items weigh 0).</summary>
    public static float GetTotalWeight(this IReadOnlyDictionary<uint, uint> snapshot)
    {
        ulong grams = 0;
        foreach (var kv in snapshot) grams += (ulong)Inventory.WeightOfItem(kv.Key) * kv.Value;
        return grams / 1000f;
    }

    /// <summary>True if every (item, qty) requirement is covered — the "can I craft
    /// this" pre-check for UIs (the CRAFT itself stays atomic in the core).</summary>
    public static bool HasIngredients(this IReadOnlyDictionary<uint, uint> snapshot, IReadOnlyDictionary<string, uint> ingredients)
    {
        foreach (var kv in ingredients)
            if (!snapshot.TryGetValue(Inventory.Item(kv.Key), out uint have) || have < kv.Value) return false;
        return true;
    }

    /// <summary>True if at least one of the named items is present.</summary>
    public static bool ContainsAny(this IReadOnlyDictionary<uint, uint> snapshot, params string[] items)
    {
        foreach (string name in items)
            if (snapshot.TryGetValue(Inventory.Item(name), out uint c) && c > 0) return true;
        return false;
    }

    /// <summary>The count of an item by name (0 when absent).</summary>
    public static uint CountOf(this IReadOnlyDictionary<uint, uint> snapshot, string item)
        => snapshot.TryGetValue(Inventory.Item(item), out uint c) ? c : 0;

    /// <summary>All (itemId, count) entries whose item carries the category. (#218)</summary>
    public static List<(uint ItemId, uint Count)> FindByCategory(this IReadOnlyDictionary<uint, uint> snapshot, string category)
    {
        var result = new List<(uint, uint)>();
        foreach (var kv in snapshot)
            if (Inventory.HasCategory(kv.Key, category)) result.Add((kv.Key, kv.Value));
        return result;
    }
}

/// <summary>Staged multi-step inventory transaction (unit of work, #233): queue ops with
/// <see cref="Take(ulong, string, uint)"/>/<see cref="Give(ulong, string, uint)"/>, apply
/// them atomically with <see cref="Commit"/>. Without a commit, disposal discards the
/// staging — no op ever ran, so a thrown exception mid-setup can't half-apply anything
/// (the "forgot to refund on error path" bug class is structurally impossible).</summary>
public sealed class InventoryTransaction : IDisposable
{
    private readonly List<InvOp> _takes = new();
    private readonly List<InvOp> _gives = new();
    private bool _committed;

    internal InventoryTransaction() { }

    /// <summary>Stages a take (consume) op.</summary>
    public InventoryTransaction Take(ulong container, string item, uint qty)
    {
        _takes.Add(new InvOp(container, Inventory.Item(item), qty));
        return this;
    }

    /// <summary>Stages a give (credit) op.</summary>
    public InventoryTransaction Give(ulong container, string item, uint qty)
    {
        _gives.Add(new InvOp(container, Inventory.Item(item), qty));
        return this;
    }

    /// <summary>Stages a move as take+give (source and target legs are still atomic —
    /// they commit in the same batch).</summary>
    public InventoryTransaction Move(ulong from, ulong to, string item, uint qty)
    {
        uint id = Inventory.Item(item);
        _takes.Add(new InvOp(from, id, qty));
        _gives.Add(new InvOp(to, id, qty));
        return this;
    }

    /// <summary>Applies all staged ops as ONE atomic native transaction. Returns the
    /// rejection reason (staging stays intact, so a caller may adjust and retry).</summary>
    public InvResult Commit()
    {
        var r = Inventory.ExecuteBatch(_takes, _gives);
        if (r == InvResult.Ok) _committed = true;
        return r;
    }

    public void Dispose()
    {
        // Nothing to roll back: without a commit nothing was ever applied. A forgotten
        // commit is still almost certainly a bug -> make noise.
        if (!_committed && (_takes.Count > 0 || _gives.Count > 0))
            Log.Warn("InventoryTransaction disposed without Commit() — staged ops were NOT applied.");
    }
}
