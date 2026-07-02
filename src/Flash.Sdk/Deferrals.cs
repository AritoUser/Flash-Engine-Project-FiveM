using System.Collections.Generic;

namespace Flash;

/// <summary>
/// Deferrals for playerConnecting: allows holding the connection process (Defer),
/// informing the joining player about progress (Update) and then admitting (Done())
/// or rejecting (Done(reason)) them. Built from the event's deferrals object
/// (a msgpack map of function references).
///
/// Currently usable SYNCHRONOUSLY (Defer → optionally Update → Done within the same
/// handler). Asynchronous deferrals (awaiting over time, e.g. a DB whitelist) will
/// need ref lifetime management later.
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

    /// <summary>Holds the connection process (must be called before Update/Done).</summary>
    public void Defer() => _defer?.Invoke();

    /// <summary>Sends a status message to the connecting player.</summary>
    public void Update(string message) => _update?.Invoke(message);

    /// <summary>Admits the connection.</summary>
    public void Done() => _done?.Invoke();

    /// <summary>Rejects the connection (the reason is shown to the player).</summary>
    public void Done(string reason) => _done?.Invoke(reason);
}
