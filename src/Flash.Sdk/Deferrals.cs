using System.Collections.Generic;

namespace Flash;

/// <summary>
/// Deferrals for playerConnecting: allows holding the connection process (Defer),
/// informing the joining player about progress (Update) and then admitting (Done())
/// or rejecting (Done(reason)) them. Built from the event's deferrals object
/// (a msgpack map of function references).
///
/// Works synchronously (Defer → optionally Update → Done inside the handler) AND
/// asynchronously: the ref strings stay valid across ticks, so an async handler can
/// `await` (database checks etc.) between Defer and Done. The convenient way is the
/// async <see cref="Events.OnPlayerConnecting(System.Func{string, Deferrals, int, System.Threading.Tasks.Task})"/>
/// overload — it defers automatically and admits when the handler returns without
/// rejecting.
/// </summary>
public sealed class Deferrals
{
    private readonly Funcref? _defer;
    private readonly Funcref? _done;
    private readonly Funcref? _update;

    internal Deferrals(IDictionary<string, object?> map)
    {
        if (map.TryGetValue("defer", out var d)) _defer = d as Funcref;
        if (map.TryGetValue("done", out var dn)) _done = dn as Funcref;
        if (map.TryGetValue("update", out var u)) _update = u as Funcref;
    }

    /// <summary>true once Done() was called (admitted or rejected). Further Done/Update
    /// calls are ignored — the connection decision is final.</summary>
    public bool Completed { get; private set; }

    /// <summary>Holds the connection process (must be called before Update/Done; the
    /// core wants at least one tick between Defer and Done).</summary>
    public void Defer() => _defer?.Invoke();

    /// <summary>Sends a status message to the connecting player.</summary>
    public void Update(string message)
    {
        if (!Completed) _update?.Invoke(message);
    }

    /// <summary>Admits the connection.</summary>
    public void Done()
    {
        if (Completed) return;
        Completed = true;
        _done?.Invoke();
    }

    /// <summary>Rejects the connection (the reason is shown to the player).</summary>
    public void Done(string reason)
    {
        if (Completed) return;
        Completed = true;
        _done?.Invoke(reason);
    }
}
