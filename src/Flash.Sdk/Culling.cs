namespace Flash;

// =====================================================================================
//  Culling -- drives FiveM's OWN, engine-enforced per-player entity culling.
//
//  WHY (researched FiveM reality):
//    OneSync already computes server-side which networked entities are relevant for
//    which player (based on a culling radius; default 424m, world grid in 150m sectors)
//    and sends a client ONLY the relevant ones -> anti-ESP is already in the engine
//    core. We do NOT rebuild that, we STEER it: priority category -> entity culling
//    radius, player profile -> player culling radius. The engine enforces it
//    (server-authoritative).
//
//  SCOPE: This is for NETWORKED entities (peds/vehicles/objects with a handle).
//    For NON-networked things (3D texts, markers, interaction points) + spatial
//    queries, use Flash.Grid (we send/draw those ourselves).
// =====================================================================================

/// <summary>Importance category of an entity (1 = critical ... higher = more cullable).</summary>
public enum Priority
{
    /// <summary>Critical (players/vehicles/walls) — effectively always relevant (anti-ESP).</summary>
    Critical = 1,
    /// <summary>Important (functional objects) — normal relevance radius.</summary>
    Important = 2,
    /// <summary>Cosmetic (decoration/markers) — only nearby, culled otherwise (VRAM protection).</summary>
    Cosmetic = 3,
}

/// <summary>Engine-enforced, server-authoritative entity culling by category/profile.</summary>
public static class Culling
{
    // Default radii per category (freely adjustable). Aligned with FiveM's 424m default.
    /// <summary>Radius for Critical — very large = effectively always relevant.</summary>
    public static float CriticalRadius = 100000f;
    /// <summary>Radius for Important — FiveM's default (424m).</summary>
    public static float ImportantRadius = 424f;
    /// <summary>Radius for Cosmetic — small, only visible nearby.</summary>
    public static float CosmeticRadius = 75f;

    /// <summary>The culling radius for a category (from the defaults above).</summary>
    public static float RadiusFor(Priority priority) => priority switch
    {
        Priority.Critical => CriticalRadius,
        Priority.Important => ImportantRadius,
        _ => CosmeticRadius,
    };

    /// <summary>Sets a networked entity's culling radius by category. The engine then only
    /// sends it to players within the radius (server-authoritative).</summary>
    public static void Apply(Entity entity, Priority priority)
        => global::Flash.Natives.Cfx.SetEntityDistanceCullingRadius(entity, RadiusFor(priority));

    /// <summary>Sets an explicit culling radius for an entity.</summary>
    public static void SetEntityRadius(Entity entity, float radius)
        => global::Flash.Natives.Cfx.SetEntityDistanceCullingRadius(entity, radius);

    /// <summary>Sets the global culling radius of ONE player (e.g. weak client /
    /// performance profile → smaller radius → receives fewer entities).</summary>
    public static void SetPlayerRadius(int netId, float radius)
        => global::Flash.Natives.Cfx.SetPlayerCullingRadius(netId.ToString(), radius);
}
