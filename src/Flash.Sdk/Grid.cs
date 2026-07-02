using System;

namespace Flash;

// =====================================================================================
//  Grid/LOD -- slim facade over the server-authoritative spatial hashing in the core.
//
//  WHY/HOW:
//    The native core keeps a 2D grid (fixed chunks) over server-generated entities
//    (NPCs, dynamic props, markers) and performs radius queries with priority sorting
//    + Z filter -- CPU-friendly (only cells within the radius, never global). This is
//    just the API; the complexity stays in the closed core. NOT for native GTA
//    streaming (map/MLO).
//
//    You feed the grid with your entities (id = e.g. NetID/your own id) and query what
//    is near a position -- e.g. for interactions, marker visibility or your own
//    culling of server-generated objects.
// =====================================================================================

/// <summary>Server-authoritative 2D spatial grid for server-generated entities.</summary>
public static unsafe class Grid
{
    // Wired from the FlashApi contract by the host (FlashBridge.Initialize).
    internal static delegate* unmanaged<ulong, float, float, float, int, void> Insert_;
    internal static delegate* unmanaged<ulong, void> Remove_;
    internal static delegate* unmanaged<float, float, float, float, float, int, ulong*, int> Query_;
    internal static delegate* unmanaged<float, float, float, float, float, int, int, ulong*, int> QueryBudgeted_;

    /// <summary>Inserts OR updates an entity (upsert). <paramref name="priority"/>:
    /// lower = more important (e.g. 1 = critical/always, 3 = cosmetic).</summary>
    public static void Insert(ulong id, Vector3 position, int priority = 0)
    {
        if (Insert_ != null) Insert_(id, position.X, position.Y, position.Z, priority);
    }

    /// <summary>Same as above, using the importance category (Critical/Important/Cosmetic).</summary>
    public static void Insert(ulong id, Vector3 position, Priority priority)
        => Insert(id, position, (int)priority);

    /// <summary>Removes an entity (e.g. on despawn).</summary>
    public static void Remove(ulong id)
    {
        if (Remove_ != null) Remove_(id);
    }

    /// <summary>
    /// Entities within the 2D radius around <paramref name="center"/>, sorted by priority
    /// (more important first), then distance. <paramref name="maxHeight"/> &gt; 0 additionally
    /// filters by vertical distance (|dz| &lt;= maxHeight); 0 = no Z filter. <paramref name="max"/>
    /// caps the hits. Returns the ids (empty array if nothing is in range).
    /// </summary>
    public static ulong[] Query(Vector3 center, float radius, float maxHeight = 0f, int max = 64)
    {
        if (Query_ == null || max <= 0) return Array.Empty<ulong>();

        var buf = new ulong[max];
        int n;
        fixed (ulong* p = buf)
        {
            n = Query_(center.X, center.Y, center.Z, radius, maxHeight, max, p);
        }
        if (n <= 0) return Array.Empty<ulong>();
        if (n == max) return buf; // exactly full -> return the buffer directly

        var result = new ulong[n];
        Array.Copy(buf, result, n);
        return result;
    }

    /// <summary>
    /// Like <see cref="Query"/>, but with a PER-CELL BUDGET (VRAM protection): per 150m
    /// chunk (= FiveM sector) only the <paramref name="budgetPerCell"/> most important/closest
    /// pass — EXCEPT priority 1 (critical: players/vehicles/walls), which always passes
    /// (anti-ESP).
    /// </summary>
    public static ulong[] QueryBudgeted(Vector3 center, float radius, int budgetPerCell,
        float maxHeight = 0f, int max = 64)
    {
        if (QueryBudgeted_ == null || max <= 0) return Array.Empty<ulong>();

        var buf = new ulong[max];
        int n;
        fixed (ulong* p = buf)
        {
            n = QueryBudgeted_(center.X, center.Y, center.Z, radius, maxHeight, budgetPerCell, max, p);
        }
        if (n <= 0) return Array.Empty<ulong>();
        if (n == max) return buf;

        var result = new ulong[n];
        Array.Copy(buf, result, n);
        return result;
    }
}
