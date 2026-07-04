using System.Collections.Generic;

namespace Flash;

/// <summary>
/// Manager for FiveM routing buckets ("virtual worlds"/dimensions, #36): players and
/// entities in different buckets cannot see or touch each other — the primitive for
/// instanced apartments, mission lobbies, dealer interiors etc.
///
/// <code>
/// int world = RoutingBuckets.Allocate();          // unique instance world
/// RoutingBuckets.MovePlayer(netId, world);
/// ...
/// RoutingBuckets.Release(world);                  // players return to world 0
/// </code>
///
/// Allocate() hands out unique ids from 1000 — collision-free with manually used low
/// buckets. Bucket 0 is the default world.
/// </summary>
public static class RoutingBuckets
{
    /// <summary>The default world every player starts in.</summary>
    public const int DefaultWorld = 0;

    // IDs from 1000: scripts that use small bucket numbers by hand stay untouched.
    // Process-global (shared SDK) -> unique across resources too.
    private static int s_next = 1000;
    private static readonly HashSet<int> s_allocated = new();

    /// <summary>Allocates a unique instance world. <paramref name="populationEnabled"/>
    /// controls ambient peds/traffic in the bucket (default off — the usual choice
    /// for interiors/lobbies).</summary>
    public static int Allocate(bool populationEnabled = false)
    {
        int bucket = s_next++;
        s_allocated.Add(bucket);
        global::Flash.Natives.Cfx.SetRoutingBucketPopulationEnabled(bucket, populationEnabled);
        return bucket;
    }

    /// <summary>Releases an allocated instance world: every player still inside is
    /// moved back to the default world (their entities follow via OneSync).</summary>
    public static void Release(int bucket)
    {
        if (!s_allocated.Remove(bucket)) return; // nur selbst vergebene Welten aufloesen
        foreach (var p in Players.All)
            if (global::Flash.Natives.Cfx.GetPlayerRoutingBucket(p.NetId.ToString()) == bucket)
                global::Flash.Natives.Cfx.SetPlayerRoutingBucket(p.NetId.ToString(), DefaultWorld);
    }

    /// <summary>Moves a player into a bucket (0 = default world).</summary>
    public static void MovePlayer(int netId, int bucket)
        => global::Flash.Natives.Cfx.SetPlayerRoutingBucket(netId.ToString(), bucket);

    /// <summary>The bucket a player is currently in.</summary>
    public static int PlayerBucket(int netId)
        => global::Flash.Natives.Cfx.GetPlayerRoutingBucket(netId.ToString());

    /// <summary>Moves a (server-created) entity into a bucket — e.g. a vehicle spawned
    /// for a mission lobby.</summary>
    public static void MoveEntity(Entity entity, int bucket)
        => global::Flash.Natives.Cfx.SetEntityRoutingBucket(entity, bucket);

    /// <summary>The bucket an entity is currently in.</summary>
    public static int EntityBucket(Entity entity)
        => global::Flash.Natives.Cfx.GetEntityRoutingBucket(entity);

    /// <summary>Entity lockdown mode of a bucket: "strict" (no client-created entities),
    /// "relaxed" (script-created only) or "inactive" (everything). Strict is the
    /// server-authoritative choice.</summary>
    public static void SetLockdownMode(int bucket, string mode)
        => global::Flash.Natives.Cfx.SetRoutingBucketEntityLockdownMode(bucket, mode);
}
