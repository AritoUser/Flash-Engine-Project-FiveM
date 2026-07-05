using System.Threading;
using System.Threading.Tasks;

namespace Flash;

/// <summary>
/// Lifecycle contract of EVERY resource.
///
/// Instead of a magic static method, a resource author writes a plain class that
/// implements this interface. On load, the host finds exactly this one class, creates
/// ONE instance (parameterless constructor) and drives it through the lifecycle:
///   - OnStart() when the resource starts (FiveM ensure/start),
///   - OnStop()  when the resource stops, BEFORE its unloadable ALC is torn down.
///
/// WHY an interface (instead of a base class): a minimal, explicit contract without
/// forcing inheritance — the dev does not derive from engine internals, they just
/// fulfill a contract. The interface lives in the shared SDK: the resource runs in
/// its own ALC but sees IResource through the default ALC → same type identity, so
/// the host can cast the instance to IResource and call it. A second SDK copy would
/// have a FOREIGN IResource type → the cast would fail.
/// </summary>
public interface IResource
{
    /// <summary>Called when the resource starts. Initialize yourself here.</summary>
    void OnStart();

    /// <summary>
    /// Called when the resource stops, before its ALC is unloaded. Release held
    /// references here (unsubscribe events, close handles) — this also helps the
    /// collectible ALC get collected cleanly.
    /// </summary>
    void OnStop();
}

/// <summary>
/// OPTIONAL additional contract for resources that need per-server-frame logic.
///
/// Opt-in: ONLY resources implementing ITickable get OnTick() called.
/// (Internally the host ticks every resource once per frame to drain due async work
/// — await continuations/timers; whether OnTick is additionally invoked is decided
/// by a cast determined ONCE at load time, not a per-frame check.) This keeps the
/// shared IResource contract minimal.
/// </summary>
public interface ITickable
{
    /// <summary>Called once per server frame (between OnStart and OnStop).</summary>
    void OnTick();
}

/// <summary>
/// OPTIONAL additional contract for resources that need to run ASYNCHRONOUS cleanup on
/// stop (e.g. flushing player data to the database) BEFORE the resource is torn down.
///
/// Why this exists: <see cref="IResource.OnStop"/> is synchronous. Firing async DB saves
/// from it is a data-loss trap — the host would tear down the resource's ALC (and cancel
/// its pending async work) before the writes reach the database. When a resource implements
/// this interface, the host awaits <see cref="OnStopAsync"/> within a bounded grace window
/// (convar <c>flash_stop_grace_ms</c>, default 5000) — pumping the resource's scheduler so
/// awaits/DB continuations resume on the script thread — and only THEN calls
/// <see cref="IResource.OnStop"/> and unloads the ALC.
///
/// Contract: keep it bounded. If the grace window elapses, the passed cancellation token
/// is cancelled and the host proceeds with teardown regardless — honor the token to finish
/// promptly. Use OnStopAsync for async flushing and the synchronous OnStop for releasing
/// references. (#40)
/// </summary>
public interface IAsyncStoppable
{
    /// <summary>Async cleanup run before the resource is torn down; honor the token — the
    /// host stops waiting once the grace window elapses.</summary>
    Task OnStopAsync(CancellationToken cancellationToken);
}
