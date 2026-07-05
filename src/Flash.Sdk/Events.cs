using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Flash;

/// <summary>
/// Server event bus for resources — a bridge onto FiveM's REAL event bus (deliberately
/// NOT a parallel silo): Flash resources can talk to all FiveM resources (lua/v8/mono)
/// and, later, the network.
///   On()   registers the resource via REGISTER_RESOURCE_AS_EVENT_HANDLER (otherwise the
///          core won't deliver the event) and remembers the handler.
///   Emit() fires through TRIGGER_EVENT_INTERNAL.
///   Delivery comes from the host (FlashHost.EventBridge → Dispatch).
///
/// ARGUMENTS: serialized as a msgpack array (interop-compatible with lua/mono). Handlers
/// receive the decoded arguments as object?[] (numbers arrive as long/double, strings as
/// string, nested arrays/maps as object?[] or Dictionary).
/// </summary>
public static partial class Events
{
    // Handlers PER resource (key = resource name) → per event name → list.
    // Why per resource: the shared SDK is static — without partitioning, a dispatch for
    // resource A would also hit handlers of B. Resource identity comes from
    // GetCurrentResourceName (correct because the host has set the active runtime).
    private static readonly Dictionary<string, Dictionary<string, List<Action<object?[]>>>> s_handlers = new();

    // Source of the currently delivered event (e.g. "net:1" for client events, else "").
    // AsyncLocal (not a plain static): the value FLOWS into async handler continuations,
    // so `Events.Source`/`SourceNetId` stay correct AFTER an `await` -- a plain static
    // would be restored by the dispatch's finally before the continuation resumes,
    // returning "" (#92). Save/restore below still handles SYNCHRONOUS nesting.
    private static readonly System.Threading.AsyncLocal<string> s_source = new();

    /// <summary>Source of the currently running event. "net:&lt;id&gt;" for client→server
    /// events, "internal-net:&lt;id&gt;" on connect, else "". Valid inside a handler AND
    /// after an <c>await</c> within it.</summary>
    public static string Source => s_source.Value ?? "";

    /// <summary>The player NetID of the event source (from "net:&lt;id&gt;"), or -1 if the
    /// event did not come from a client. Handy in client→server handlers.</summary>
    public static int SourceNetId
    {
        get
        {
            string s = Source;
            int colon = s.LastIndexOf(':');
            return colon >= 0 && int.TryParse(s.AsSpan(colon + 1), out int id) ? id : -1;
        }
    }

    /// <summary>
    /// True if the running event was triggered by the server CORE, not forged by a client.
    /// The FiveM core tags its lifecycle events (playerConnecting/playerJoining/playerDropped)
    /// with source "internal-net:&lt;id&gt;", whereas a client's <c>TriggerServerEvent</c> always
    /// arrives as "net:&lt;id&gt;" (the source is stamped server-side from the sender's netId and
    /// cannot be spoofed). Gate trust of lifecycle events on this so a client can't forge a
    /// drop/join to desync the server's session state (#73). Verified against the pinned
    /// FXServer source (ServerEventPacketHandler = "net:", GameServer/ClientRegistry/
    /// InitConnectMethod = "internal-net:").
    /// </summary>
    public static bool IsFromCore
        => Source.StartsWith("internal-net:", StringComparison.Ordinal);

    /// <summary>Registers a handler WITH access to the event arguments.</summary>
    public static void On(string eventName, Action<object?[]> handler)
    {
        string res = global::Flash.Natives.Cfx.GetCurrentResourceName() ?? "";

        if (!s_handlers.TryGetValue(res, out var byEvent))
        {
            byEvent = new Dictionary<string, List<Action<object?[]>>>();
            s_handlers[res] = byEvent;
        }
        if (!byEvent.TryGetValue(eventName, out var list))
        {
            list = new List<Action<object?[]>>();
            byEvent[eventName] = list;
            // Only on the FIRST handler of this resource for this event, tell the core
            // that the resource handles it — otherwise it filters it out before delivery.
            global::Flash.Natives.Cfx.RegisterResourceAsEventHandler(eventName);
        }
        list.Add(handler);
    }

    /// <summary>Convenience overload for events that don't need arguments.</summary>
    public static void On(string eventName, Action handler)
        => On(eventName, _ => handler());

    /// <summary>
    /// Convenience for playerConnecting: handler(name, deferrals, source). With
    /// deferrals.Defer() the connection can be held and admitted/rejected via
    /// Done()/Done(reason) (whitelist/auth). source = NetID of the connecting player.
    /// </summary>
    public static void OnPlayerConnecting(Action<string, Deferrals, int> handler)
    {
        On("playerConnecting", args =>
        {
            string name = args.Length > 0 ? args[0]?.ToString() ?? "" : "";
            var map = (args.Length > 2 && args[2] is IDictionary<string, object?> m)
                ? m : new Dictionary<string, object?>();
            handler(name, new Deferrals(map), SourceNetId);
        });
    }

    /// <summary>
    /// ASYNC connection gate for playerConnecting — the safe contract for database
    /// checks (whitelist/bans) via <c>await</c>:
    ///   - the connection is held automatically (Defer + the one-tick gap the core wants),
    ///   - reject inside the handler with <c>deferrals.Done(reason)</c>,
    ///   - returning WITHOUT Done admits the player automatically,
    ///   - an unhandled exception REJECTS (fail-closed: an erroring gate must not wave
    ///     everyone through) and is logged.
    /// For full manual control (adaptive cards, deferral kept beyond the handler) use
    /// the synchronous overload.
    /// </summary>
    public static void OnPlayerConnecting(Func<string, Deferrals, int, Task> handler)
    {
        On("playerConnecting", args =>
        {
            string name = args.Length > 0 ? args[0]?.ToString() ?? "" : "";
            var map = (args.Length > 2 && args[2] is IDictionary<string, object?> m)
                ? m : new Dictionary<string, object?>();
            // Defer SYNCHRONOUSLY, while the event dispatch still runs -- after the
            // handler returned, the core would treat the connection as unhandled.
            var deferrals = new Deferrals(map);
            deferrals.Defer();
            _ = RunConnectingGate(handler, name, deferrals, SourceNetId);
        });
    }

    private static async Task RunConnectingGate(
        Func<string, Deferrals, int, Task> handler, string name, Deferrals deferrals, int source)
    {
        try
        {
            // The core wants >= 1 tick between defer and update/done (known engine quirk;
            // the lua pattern is `deferrals.defer() Wait(0) ...`).
            await Async.NextFrame();
            await handler(name, deferrals, source);
            deferrals.Done(); // no reject -> admit (no-op if the handler already decided)
        }
        catch (Exception e)
        {
            Log.Error($"playerConnecting gate for '{name}' threw: {e.Message}");
            deferrals.Done("Connection check failed. Please try again later.");
        }
    }

    /// <summary>Fires an event with arbitrary arguments onto the server event bus.</summary>
    public static unsafe void Emit(string eventName, params object?[] args)
    {
        eventName ??= ""; // null -> IntPtr.Zero -> native null-deref crash (#95)
        byte[] payload = Msgpack.EncodeArray(args ?? Array.Empty<object?>());
        nint name = Marshal.StringToCoTaskMemUTF8(eventName);
        try
        {
            // Raw through Native.Invoke with a real byte pointer -- NOT the generated
            // TriggerEventInternal(string,...), which would marshal the payload as a
            // UTF-8 string and corrupt binary msgpack (null bytes!).
            fixed (byte* p = payload)
            {
                Span<nuint> a = stackalloc nuint[3];
                a[0] = (nuint)name;
                a[1] = (nuint)p;
                a[2] = (nuint)payload.Length;
                global::Flash.Native.Invoke(0x91310870UL, a); // TRIGGER_EVENT_INTERNAL
            }
        }
        finally { Marshal.FreeCoTaskMem(name); }
    }

    /// <summary>Fires an event to ONE client (server → client) over the network.
    /// target = player NetID. The client receives it via RegisterNetEvent/AddEventHandler.</summary>
    public static unsafe void EmitClient(int target, string eventName, params object?[] args)
    {
        eventName ??= ""; // null -> native null-deref crash (#95)
        byte[] payload = Msgpack.EncodeArray(args ?? Array.Empty<object?>());
        nint name = Marshal.StringToCoTaskMemUTF8(eventName);
        nint tgt = Marshal.StringToCoTaskMemUTF8(target.ToString());
        try
        {
            fixed (byte* p = payload)
            {
                // TRIGGER_CLIENT_EVENT_INTERNAL(name, target, payload, len). target is a
                // STRING (NetID or "-1" for everyone). Payload raw as a byte pointer (binary).
                Span<nuint> a = stackalloc nuint[4];
                a[0] = (nuint)name;
                a[1] = (nuint)tgt;
                a[2] = (nuint)p;
                a[3] = (nuint)payload.Length;
                global::Flash.Native.Invoke(0x2F7A49E6UL, a);
            }
        }
        finally { Marshal.FreeCoTaskMem(name); Marshal.FreeCoTaskMem(tgt); }
    }

    /// <summary>Fires an event to ALL clients (target "-1").</summary>
    public static void EmitAllClients(string eventName, params object?[] args)
        => EmitClient(-1, eventName, args);

    // --- Host-internal (not for resource authors) ---------------------------

    /// <summary>Called by the host when FiveM delivers an event to THIS resource.</summary>
    internal static void Dispatch(string resource, string eventName, byte[] payload, string source)
    {
        if (!s_handlers.TryGetValue(resource, out var byEvent)) return;
        if (!byEvent.TryGetValue(eventName, out var list)) return;

        // RATE LIMIT for client events, BEFORE the decode (the expensive part): a mod
        // menu can spam TriggerServerEvent thousands of times per second. Drops are
        // silent except once per abuse window (log spam would be its own DoS vector);
        // sustained flooding kicks the player.
        switch (RateLimit.Check(source, eventName, out int floodNetId, out bool firstDrop))
        {
            case RateLimit.Verdict.Drop:
                if (firstDrop)
                    Log.Warn($"[SECURITY] netId {floodNetId} exceeds the rate limit for '{eventName}' -- dropping.");
                return;
            case RateLimit.Verdict.Kick:
                Log.Warn($"[SECURITY] netId {floodNetId} keeps flooding '{eventName}' -> kick.");
                try { Players.Get(floodNetId).Kick("Event flooding (rate limit exceeded)."); } catch { }
                return;
        }

        object?[] args;
        try { args = Msgpack.DecodeArray(payload); }
        catch { args = Array.Empty<object?>(); } // a broken payload must not kill the server

        // Source with save/restore (synchronous nesting: a handler emits another event).
        // AsyncLocal makes the value ALSO flow into any async handler's continuation.
        string prevSource = s_source.Value ?? "";
        s_source.Value = source;
        try
        {
            // Run handlers in the resource's scheduler context → an `await` in an
            // (async) handler resumes on the script thread.
            Scheduler.RunWith(Scheduler.Get(resource), () =>
            {
                // Iterate over a copy: a handler may register during dispatch or emit
                // another event (nested) without corrupting the list. Each handler is
                // isolated: a faulty handler must not kill the others.
                foreach (var handler in list.ToArray())
                {
                    try { handler(args); }
                    catch (Exception e)
                    {
                        Log.Error($"Handler for '{eventName}' threw: {e.Message}");
                        Diagnostics.Report($"event:{eventName}", e);
                    }
                }
            });
        }
        finally { s_source.Value = prevSource; }
    }

    /// <summary>On resource stop, drop all handlers of the resource (frees captured
    /// references → helps unloading the collectible ALC).</summary>
    internal static void ClearResource(string resource) => s_handlers.Remove(resource);
}
