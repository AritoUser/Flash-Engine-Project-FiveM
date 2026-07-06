using System;
using System.Collections.Generic;

namespace Flash;

// =====================================================================================
//  Lifecycle-managed virtual instances (#126).
//
//  Routing buckets alone are an error-prone primitive: a vehicle spawned inside a
//  private bucket outlives the minigame forever (entity slot leak), players must be
//  routed back by hand, and every script reinvents its own tracking arrays. A
//  VirtualInstance bundles the three scopes into one disposable object:
//    - NETWORK: an automatically allocated routing bucket (strict lockdown by default —
//      the server-authoritative choice);
//    - ENTITIES: everything spawned through the instance is tracked and deleted on
//      Dispose;
//    - VOICE/META: each member gets the replicated state-bag key "instance" set to the
//      bucket id (cleared on leave) — voice resources (pma-voice etc.) and HUDs bind to
//      it to isolate channels. The SDK cannot drive Mumble directly (client-side), so
//      this replicated key IS the contract.
//
//  Disposing routes all members back to the default world, deletes all tracked
//  entities and releases the bucket. Script-thread only (natives).
// =====================================================================================

/// <summary>
/// A private, disposable world instance (apartment, race lobby, admin jail, minigame):
/// wraps an allocated routing bucket, tracks every entity spawned through it and
/// restores players + deletes entities on <see cref="Dispose"/>. (#126)
/// </summary>
public sealed class VirtualInstance : IDisposable
{
    /// <summary>The routing bucket backing this instance (unique per instance).</summary>
    public int Bucket { get; }

    private readonly HashSet<int> _members = new();
    private readonly List<Entity> _entities = new();
    // Personal vehicles routed IN with a member (not spawned by the instance): moved back
    // to the default world on leave/dispose, NOT deleted -- they belong to the player. (#197)
    private readonly Dictionary<int, Entity> _broughtVehicles = new();
    private bool _disposed;

    /// <summary>Creates a new instance world. <paramref name="populationEnabled"/>
    /// controls ambient peds/traffic (default off); <paramref name="lockdown"/> is the
    /// entity lockdown mode ("strict" = no client-created entities, the default;
    /// "relaxed" / "inactive" to loosen).</summary>
    public VirtualInstance(bool populationEnabled = false, string lockdown = "strict")
    {
        Bucket = RoutingBuckets.Allocate(populationEnabled);
        RoutingBuckets.SetLockdownMode(Bucket, lockdown);
    }

    /// <summary>NetIds of the players currently routed into this instance (members that
    /// disconnected are pruned on access).</summary>
    public IReadOnlyCollection<int> Members
    {
        get
        {
            _members.RemoveWhere(id => !Players.Get(id).Connected);
            return _members;
        }
    }

    /// <summary>The entities spawned through this instance that still exist.</summary>
    public IReadOnlyList<Entity> Entities
    {
        get
        {
            _entities.RemoveAll(e => !Exists(e));
            return _entities;
        }
    }

    // SERVER-side existence check: the shared DOES_ENTITY_EXIST hash is client-only —
    // on the server only the Cfx variant is registered (it takes the raw handle).
    private static bool Exists(Entity e)
        => global::Flash.Natives.Cfx.DoesEntityExist(new Object(e.Value));

    /// <summary>
    /// Routes a player into the instance. <paramref name="includeVehicle"/> brings the
    /// vehicle they are sitting in along (otherwise it would stay visible in the old
    /// world while the driver vanishes). Sets the replicated state-bag key "instance"
    /// so voice/HUD resources can scope to it.
    /// </summary>
    public void AddPlayer(ServerPlayer player, bool includeVehicle = true)
    {
        ThrowIfDisposed();
        if (!player.Connected) return;

        if (includeVehicle)
        {
            var ped = global::Flash.Natives.Cfx.GetPlayerPed(player.NetId.ToString());
            if (ped.Value != 0)
            {
                var veh = global::Flash.Natives.Cfx.GetVehiclePedIsIn(new Ped(ped.Value), false);
                if (veh.Value != 0)
                {
                    RoutingBuckets.MoveEntity(new Entity(veh.Value), Bucket);
                    // Remember it so it's routed BACK on leave/dispose (else it's stranded
                    // in the bucket, polluting the next instance that reuses the id). (#197)
                    _broughtVehicles[player.NetId] = new Entity(veh.Value);
                }
            }
        }
        RoutingBuckets.MovePlayer(player.NetId, Bucket);
        player.State.Set("instance", Bucket);
        _members.Add(player.NetId);
    }

    /// <summary>Routes a player back to the default world and clears their "instance"
    /// state-bag key. Safe to call for players that already left or disconnected.</summary>
    public void RemovePlayer(ServerPlayer player)
    {
        _members.Remove(player.NetId);
        // Route the personal vehicle we brought in back to the default world (#197).
        if (_broughtVehicles.Remove(player.NetId, out var veh)
            && global::Flash.Natives.Cfx.DoesEntityExist(new Object(veh.Value)))
            RoutingBuckets.MoveEntity(veh, RoutingBuckets.DefaultWorld);
        if (!player.Connected) return;
        RoutingBuckets.MovePlayer(player.NetId, RoutingBuckets.DefaultWorld);
        player.State.Set("instance", null);
    }

    /// <summary>Spawns a vehicle inside the instance (server-authoritative,
    /// CREATE_VEHICLE_SERVER_SETTER). Deleted automatically on <see cref="Dispose"/>.
    /// <paramref name="type"/> is the FiveM vehicle type ("automobile", "bike", "heli",
    /// "boat", "plane", "train", ...).</summary>
    public Vehicle SpawnVehicle(string model, Vector3 position, float heading, string type = "automobile")
    {
        ThrowIfDisposed();
        var hash = global::Flash.Natives.Cfx.GetHashKey(model);
        var veh = global::Flash.Natives.Cfx.CreateVehicleServerSetter(
            hash, type, position.X, position.Y, position.Z, heading);
        Track(new Entity(veh.Value));
        return veh;
    }

    /// <summary>Spawns an NPC ped inside the instance (server-side CREATE_PED).
    /// Deleted automatically on <see cref="Dispose"/>.</summary>
    public Entity SpawnPed(string model, Vector3 position, float heading, int pedType = 4)
    {
        ThrowIfDisposed();
        var hash = global::Flash.Natives.Cfx.GetHashKey(model);
        var ped = global::Flash.Natives.Cfx.CreatePed(
            pedType, hash, position.X, position.Y, position.Z, heading, true, true);
        Track(ped);
        return ped;
    }

    /// <summary>Spawns a prop/object inside the instance (server-side
    /// CREATE_OBJECT_NO_OFFSET). Deleted automatically on <see cref="Dispose"/>.</summary>
    public Entity SpawnProp(string model, Vector3 position)
    {
        ThrowIfDisposed();
        var hash = global::Flash.Natives.Cfx.GetHashKey(model);
        var obj = global::Flash.Natives.Cfx.CreateObjectNoOffset(
            hash, position.X, position.Y, position.Z, true, true, false);
        Track(obj);
        return obj;
    }

    /// <summary>Adopts an externally created entity into the instance: moves it into the
    /// bucket and deletes it on <see cref="Dispose"/> like the Spawn* results.</summary>
    public void TrackEntity(Entity entity) { ThrowIfDisposed(); Track(entity); }

    private void Track(Entity entity)
    {
        if (entity.Value == 0) return;
        RoutingBuckets.MoveEntity(entity, Bucket);
        _entities.Add(entity);
    }

    /// <summary>
    /// Tears the instance down: routes all members back to the default world (clearing
    /// their "instance" key), deletes every tracked entity that still exists and
    /// releases the routing bucket. Idempotent.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (int netId in _members)
        {
            var p = Players.Get(netId);
            if (!p.Connected) continue;
            RoutingBuckets.MovePlayer(netId, RoutingBuckets.DefaultWorld);
            try { p.State.Set("instance", null); } catch { }
        }
        _members.Clear();

        // Personal vehicles routed in are moved BACK, not deleted (they belong to the
        // players); only instance-SPAWNED entities below get deleted. (#197)
        foreach (var veh in _broughtVehicles.Values)
        {
            try
            {
                if (global::Flash.Natives.Cfx.DoesEntityExist(new Object(veh.Value)))
                    RoutingBuckets.MoveEntity(veh, RoutingBuckets.DefaultWorld);
            }
            catch { }
        }
        _broughtVehicles.Clear();

        // The whole point of the lifecycle (#126): no ghost vehicles locked in a dead
        // bucket eating entity slots forever.
        foreach (var e in _entities)
        {
            try
            {
                if (Exists(e))
                    global::Flash.Natives.Cfx.DeleteEntity(e);
            }
            catch { /* a single stubborn entity must not stop the teardown */ }
        }
        _entities.Clear();

        RoutingBuckets.Release(Bucket);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(VirtualInstance),
            $"Instance (bucket {Bucket}) is already disposed.");
    }
}
