using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Flash;

// =====================================================================================
//  Async RPC (request/response) between client and server -- Issue #15.
//
//  The event bus is fire-and-forget; every "ask the other side" forces devs to split
//  logic into two handlers plus hand-rolled timeout logic. This wraps the existing
//  msgpack event bus with correlation tickets + TaskCompletionSource:
//
//    SERVER answers CLIENT:   Rpc.Register("getBalance", (netId, args) => ...);
//    SERVER asks CLIENT:      int hp = await Rpc.Client<int>(netId, "getHealth");
//
//  The client side is the flash-core Lua bridge (rpc.lua): from any client resource
//    exports['flash-core']:rpcCall(name, { args... }[, cb])        -- awaits without cb
//    exports['flash-core']:rpcRegister(name, function(...) end)    -- answers the server
//  (Client C# is a deliberate later phase; the wire format stays identical.)
//
//  SECURITY: requests are only accepted from real clients ("net:" source) and responses
//  only from the client the request was sent to (ticket bound to netId -- a different
//  player cannot forge answers). Handler errors reply with a generic failure (no
//  exception details leak to clients) and log server-side. Incoming RPC traffic runs
//  through the client-event rate limiter like any other event.
// =====================================================================================

/// <summary>
/// Request/response RPC on top of the event bus. <see cref="Register(string, Func{int, object?[], object?})"/>
/// answers client calls; <see cref="Client{T}"/> calls INTO a client and awaits the
/// result on the script thread. RPC names are a server-wide namespace -- prefix them
/// with your resource name (e.g. "shop:getCatalog").
/// </summary>
public static class Rpc
{
    private const string ReqEvent = "__flash_rpc_req";   // Client -> Server: [name, ticket, args...]
    private const string ResEvent = "__flash_rpc_res";   // Server -> Client: [ticket, ok, result]
    private const string CReqEvent = "__flash_rpc_creq"; // Server -> Client: [name, ticket, args...]
    private const string CResEvent = "__flash_rpc_cres"; // Client -> Server: [ticket, ok, result]

    /// <summary>Default timeout for server→client calls (ms).</summary>
    public const int DefaultTimeoutMs = 5_000;

    // Client->Server handlers, partitioned per resource (cleanly removable on unload).
    // Names are a server-wide namespace: whichever resource registered the name answers.
    private static readonly Dictionary<string, Dictionary<string, Func<int, object?[], Task<object?>>>> s_handlers = new();
    // Resources whose ReqEvent/CResEvent listeners are already registered.
    private static readonly HashSet<string> s_reqWired = new();
    private static readonly HashSet<string> s_resWired = new();

    // Open server->client calls: ticket -> (completion, target client, owning resource).
    private static readonly Dictionary<long, (TaskCompletionSource<object?> Tcs, int NetId, string Res, string Name)> s_pending = new();
    private static long s_nextTicket = 1;

    // === Server answers client ==============================================

    /// <summary>Registers a synchronous RPC handler: handler(netId, args) → result.</summary>
    public static void Register(string name, Func<int, object?[], object?> handler)
        => Register(name, (netId, args) => Task.FromResult(handler(netId, args)));

    /// <summary>Registers an ASYNC RPC handler (DB lookups etc.) — the response is sent
    /// when the task completes; the client awaits either way.</summary>
    public static void Register(string name, Func<int, object?[], Task<object?>> handler)
    {
        string res = global::Flash.Natives.Cfx.GetCurrentResourceName() ?? "";
        if (!s_handlers.TryGetValue(res, out var byName))
        {
            byName = new Dictionary<string, Func<int, object?[], Task<object?>>>();
            s_handlers[res] = byName;
        }
        byName[name] = handler;

        if (s_reqWired.Add(res))
            Events.On(ReqEvent, OnRequest);
    }

    // Runs inside the event dispatch of ONE resource (env set) -- only that resource's
    // registry is consulted; resources without the name stay silent (no error reply,
    // it would race the real answer from another resource).
    private static void OnRequest(object?[] args)
    {
        int netId = Events.SourceNetId;
        if (netId < 0) return; // nur echte Clients (Quelle "net:<id>")

        string res = global::Flash.Natives.Cfx.GetCurrentResourceName() ?? "";
        if (!s_handlers.TryGetValue(res, out var byName)) return;
        string name = Args.Str(args, 0, "");
        if (!byName.TryGetValue(name, out var handler)) return;

        long ticket = Args.Long(args, 1);
        object?[] rest = args.Length > 2 ? args[2..] : Array.Empty<object?>();
        _ = RunHandler(handler, name, netId, ticket, rest);
    }

    private static async Task RunHandler(Func<int, object?[], Task<object?>> handler,
        string name, int netId, long ticket, object?[] args)
    {
        try
        {
            object? result = await handler(netId, args);
            Events.EmitClient(netId, ResEvent, ticket, true, result);
        }
        catch (Exception ex)
        {
            // Kein Exception-Detail zum Client (Info-Leak); Ursache steht im Server-Log.
            Log.Error($"RPC-Handler '{name}' warf fuer netId {netId}: {ex.Message}");
            Events.EmitClient(netId, ResEvent, ticket, false, "rpc handler failed");
        }
    }

    // === Server asks client =================================================

    /// <summary>
    /// Calls an RPC registered on the CLIENT (via the flash-core Lua bridge:
    /// <c>rpcRegister(name, fn)</c>) and awaits the result — resumes on the script
    /// thread. Throws <see cref="TimeoutException"/> if the client does not answer
    /// within <see cref="DefaultTimeoutMs"/>, and <see cref="InvalidOperationException"/>
    /// if the client-side handler failed or is unknown.
    /// </summary>
    public static Task<T?> Client<T>(int netId, string name, params object?[] args)
        => ClientWithTimeout<T>(DefaultTimeoutMs, netId, name, args);

    /// <summary>Like <see cref="Client{T}"/> with an explicit timeout in ms.</summary>
    public static async Task<T?> ClientWithTimeout<T>(int timeoutMs, int netId, string name, params object?[] args)
    {
        string res = global::Flash.Natives.Cfx.GetCurrentResourceName() ?? "";
        if (s_resWired.Add(res))
            Events.On(CResEvent, OnClientResponse);

        long ticket = s_nextTicket++;
        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        s_pending[ticket] = (tcs, netId, res, name);

        // Anfrage raus: [name, ticket, args...] als flaches Event-Argument-Array.
        var payload = new object?[2 + (args?.Length ?? 0)];
        payload[0] = name; payload[1] = ticket;
        if (args != null) Array.Copy(args, 0, payload, 2, args.Length);
        Events.EmitClient(netId, CReqEvent, payload);

        // Timeout ueber den Resource-Scheduler (Script-Thread), Muster wie Flash.Http.
        if (timeoutMs > 0 && Scheduler.Get(res) is { } ctx)
        {
            long t = ticket;
            ctx.ScheduleTimer(Environment.TickCount64 + timeoutMs, () =>
            {
                if (s_pending.Remove(t))
                    tcs.TrySetException(new TimeoutException($"RPC '{name}' to netId {netId} timed out after {timeoutMs} ms."));
            });
        }

        object? raw = await tcs.Task;
        return ConvertResult<T>(raw);
    }

    private static void OnClientResponse(object?[] args)
    {
        long ticket = Args.Long(args, 0);
        if (!s_pending.TryGetValue(ticket, out var entry)) return;

        // FORGERY-SCHUTZ: nur der Client, an den die Anfrage ging, darf sie beantworten.
        if (Events.SourceNetId != entry.NetId) return;
        s_pending.Remove(ticket);

        bool ok = Args.Bool(args, 1);
        if (ok) entry.Tcs.TrySetResult(args.Length > 2 ? args[2] : null);
        else entry.Tcs.TrySetException(new InvalidOperationException(
            $"RPC '{entry.Name}' failed on client netId {entry.NetId}: {Args.Str(args, 2, "unknown error")}"));
    }

    // msgpack liefert Zahlen als long/double -- fuer die gaengigen Ziel-Typen sicher
    // konvertieren (Flash.Args), alles andere per Typ-Match.
    private static T? ConvertResult<T>(object? raw)
    {
        if (raw is T t) return t;
        object? boxed =
            typeof(T) == typeof(int) ? Args.Int(raw) :
            typeof(T) == typeof(long) ? Args.Long(raw) :
            typeof(T) == typeof(bool) ? Args.Long(raw) != 0 :
            typeof(T) == typeof(float) ? (float)Args.Long(raw) :
            typeof(T) == typeof(string) ? raw?.ToString() :
            raw;
        // double/float aus msgpack kommen als double -> direkter Cast-Versuch oben,
        // sonst best effort.
        if (typeof(T) == typeof(float) && raw is double d) boxed = (float)d;
        if (typeof(T) == typeof(double) && raw is long l) boxed = (double)l;
        return boxed is T ct ? ct : default;
    }

    /// <summary>On resource stop: drop the resource's handlers and open calls (their
    /// continuations would otherwise pin the collectible ALC).</summary>
    internal static void ClearResource(string resource)
    {
        s_handlers.Remove(resource);
        s_reqWired.Remove(resource);
        s_resWired.Remove(resource);

        List<long>? drop = null;
        foreach (var kv in s_pending)
            if (kv.Value.Res == resource) (drop ??= new List<long>()).Add(kv.Key);
        if (drop != null)
            foreach (var tk in drop) s_pending.Remove(tk); // NICHT completen -> Task wird Garbage
    }
}
