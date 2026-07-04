# Flash.Sdk — API Reference

Everything runs **server-side** on the FiveM script thread. `using Flash;` is all you
need — every type lives in the `Flash` namespace. IntelliSense provides detailed docs on
every method; this document is the overview with examples.

Contents:
[Lifecycle](#lifecycle) · [Log](#log) · [Events](#events) · [Players](#players) ·
[Commands](#commands) · [Deferrals](#deferrals-connect-gate) · [State bags](#state-bags-replicated) ·
[Exports](#exports) · [State](#state-reactive-core-store) · [Db](#db) · [Async](#async) ·
[Http](#http) · [Natives](#natives) · [Grid](#grid) · [Culling](#culling)

---

## Lifecycle

```csharp
public sealed class Main : IResource, ITickable   // ITickable is optional
{
    public void OnStart() { }   // on ensure/start — initialization
    public void OnStop()  { }   // on stop/restart — release references, clean up
    public void OnTick()  { }   // only with ITickable: once per server frame
}
```

One resource = **one** class implementing `IResource` (name/namespace are up to you),
with a parameterless constructor. The DLL must be named `<resourcename>.dll`. Every
resource runs in its own unloadable context — `restart <name>` reloads it fresh without
a server restart.

## Log

```csharp
Log.Info("Shop ready");     // [myshop] [INFO] Shop ready
Log.Warn(...); Log.Error(...); Log.Debug(...);
```
Automatically prefixed with resource name + level.

## Events

A bridge onto FiveM's **real** event bus — interop with Lua/JS resources and the network.
Arguments travel as msgpack; handlers receive them as `object?[]` (numbers as
`long`/`double`, strings as `string`, arrays as `object?[]`, maps as
`Dictionary<string, object?>`).

```csharp
// Receive (server-internal OR from clients):
Events.On("myshop:buy", args =>
{
    int from = Events.SourceNetId;         // -1 if not from a client
    string item = args[0]?.ToString() ?? "";
});
Events.On("otherResource:signal", () => { /* no args */ });

// Send:
Events.Emit("myshop:sold", "pistol", 250);          // server bus (all resources)
Events.EmitClient(netId, "myshop:ui", "open");       // to ONE client
Events.EmitAllClients("weather:sync", "RAIN");       // to all clients
```

- `Events.Source` — raw source of the running event (`"net:5"` for client events);
  `Events.SourceNetId` — the NetID from it. **Only valid synchronously inside the
  handler** (don't read it after an `await` — capture it in a local first).
- Client-side you receive with `RegisterNetEvent`/`AddEventHandler` (Lua) and send with
  `TriggerServerEvent` — examples in the [cookbook](cookbook.md).
- Handlers may be `async`; continuations resume on the script thread.
- An error in one handler is isolated (logged; other handlers keep running).

## Players

```csharp
int n = Players.Count;
foreach (var p in Players.All) Log.Info($"{p.NetId}: {p.Name} ({p.Ping}ms)");

var pl = Players.Get(netId);
pl.Name; pl.Endpoint; pl.Ping;
pl.Identifiers;                        // ["license:...", "discord:...", ...]
pl.IdentifierOfType("license");        // or null
pl.Kick("reason");
pl.Emit("event:name", args);           // = Events.EmitClient(pl.NetId, ...)
pl.State["money"] = 500;               // the player's replicated state bag
```

`Players.Get` does not check whether the player is connected — properties then return
empty values. For accounts/bans use **identifiers** as keys, never the NetID (which is
per session).

## Commands

```csharp
Commands.Register("give", (src, args, raw) =>
{
    // src = caller's NetID (0 = server console)
    // args = ["player", "500"], raw = "give player 500"
}, restricted: false);   // true -> only with ace permission
```

### Attribute router (typed parameters)

For anything beyond a one-liner, annotate methods with `[Command]` and register
the object once — parameters are parsed and validated automatically, and a parse
error replies a generated usage line instead of reaching your handler:

```csharp
public sealed class AdminCommands
{
    [Command("give", Restricted = true)]
    public void Give(CommandContext ctx, ServerPlayer target, int amount, [Rest] string reason = "")
    {
        // target parsed from a netId arg, amount is an int, reason swallows the
        // rest of the line. Optional params (with a default) may be omitted.
        Exports.Call("flash-core", "addMoney", target.NetId, "cash", amount, reason);
        ctx.Reply($"Gave {amount} to #{target.NetId}.");
    }
}

Commands.RegisterAll(new AdminCommands());
```

Supported parameter types: `CommandContext` (injected — caller/raw line/`Reply`),
`int`/`long`/`float`/`double`/`bool`/`string`, and `ServerPlayer` (from a netId).
`[Rest]` on the last string captures the remaining line. Async (`Task`) methods
are awaited safely; handler exceptions are logged and routed to `Diagnostics`.

### Rate limiting (client events)

Incoming **client** events are rate-limited per player and event name (token
bucket, checked before the payload is even decoded) — protection against event
flooding from mod menus. Server-internal events are never throttled. Defaults
are generous; tune in `server.cfg`:

```cfg
set flash_event_rate_limit      "32"   # events/s per (player, event); 0 = off
set flash_event_rate_burst      "64"   # bucket capacity (short bursts are fine)
set flash_event_rate_kick_after "500"  # drops within 10s until kick; 0 = never
```

Drops are logged once per abuse window (`[SECURITY] … exceeds the rate limit`);
sustained flooding kicks the player. Note FiveM delivers a client event to each
listening resource separately — limits count per delivery.

## Rpc (request/response)

Events are fire-and-forget; `Flash.Rpc` adds awaitable request/response on top
(correlation tickets + timeouts). RPC names are a server-wide namespace —
prefix them with your resource name.

```csharp
// Answer client calls (sync or async handler):
Rpc.Register("bank:getBalance", (netId, args) =>
    Exports.Call<int>("flash-core", "getMoney", netId, Args.Str(args, 0, "cash")));
Rpc.Register("bank:getHistory", async (netId, args) =>
    await Db.QueryAsync("SELECT ... WHERE cid=@p0", cid));

// Call INTO a client and await the answer (TimeoutException if it stays silent):
int hp = await Rpc.Client<int>(netId, "getHealth");
var r  = await Rpc.ClientWithTimeout<long>(2000, netId, "probe");
```

Client side (Lua, provided by flash-core's RPC bridge — usable from any client
resource):

```lua
-- ask the server (awaits without a callback; wrap in pcall for errors):
local balance = exports['flash-core']:rpcCall('bank:getBalance', { 'cash' })

-- let the server call into this client:
exports['flash-core']:rpcRegister('getHealth', function()
    return GetEntityHealth(PlayerPedId())
end)
```

Security: requests are only accepted from real clients, responses only from the
client the request was sent to (tickets are bound to the netId); handler errors
reply with a generic failure (details stay in the server log); RPC traffic runs
through the client-event rate limiter like any other event.

## Deferrals (connect gate)

Hold the connection process and admit/reject — whitelists, bans, queues. The **async
overload** is the recommended form for database checks: it defers automatically, a
return without `Done` admits, and an unhandled exception **rejects** (fail-closed —
an erroring gate must not wave everyone through):

```csharp
Events.OnPlayerConnecting(async (name, deferrals, src) =>
{
    string lic = Players.Get(src).IdentifierOfType("license") ?? "";  // read natives before the first await
    deferrals.Update("Checking whitelist …");
    bool ok = (await Db.QueryAsync("SELECT 1 FROM whitelist WHERE license=@p0", lic)).Count > 0;
    if (!ok) deferrals.Done($"Hey {name}, you are not whitelisted.");
    // returning without Done() admits the player
});
```

For full manual control (adaptive cards, keeping the deferral beyond the handler)
use the synchronous overload and drive `Defer`/`Update`/`Done` yourself. `Done` is
final — further `Done`/`Update` calls are ignored (`deferrals.Completed`).

The bundled `flash-admin` resource already gates connects with DB-backed bans plus an
optional whitelist: enable with `set flash_whitelist "true"` in `server.cfg` and manage
entries via the `whitelist add|remove|check <netId|license>` console command.

For production servers, also set `set flash_require_identifier "true"`: connections
without a cryptographic identifier (license/fivem/discord/steam/live/xbl) are then
**rejected** at the gate. Without it, such players fall back to ip/netid keys — those
are not session-stable, so an account or ban could change owners (LAN/dev only).

## State bags (replicated)

FiveM's replicated state — the server writes, clients read (`LocalPlayer.state`,
`Entity(x).state`, `GlobalState` in Lua). Server-authoritative: clients cannot forge it.

```csharp
GlobalState.Set("weather", "RAIN");            // replicated to all clients
var w = GlobalState.Get<string>("weather");    // server-side read

var bag = Players.Get(netId).State;            // bag "player:<netId>"
bag.Set("job", "police");                      // client: LocalPlayer.state.job
bag["level"] = 5;                              // indexer = Set(replicated)/Get
int lvl = bag.Get<int>("level");
```

Values are msgpack — numbers come back as `long`/`double` on read
(`Get<T>` returns `default` if the type doesn't match).

## Exports

Synchronous resource↔resource calls **with a return value** (between
Flash/C# resources only):

```csharp
// Provider (untyped, full object?[] access):
Exports.Register("addMoney", a => AddMoney(ToInt(a[0]), a[1]?.ToString() ?? "cash", ToInt(a[2])));

// Provider (typed overloads — no manual indexing/casting; args coerced via Args.To<T>):
Exports.Register<int, string, int, bool>("addMoney", (netId, acc, amount) => AddMoney(netId, acc, amount));
Exports.Register<int>("playerCount", () => Players.Count);

// Consumer:
bool ok = Exports.Call<bool>("flash-core", "addMoney", netId, "cash", 100);
```

`Call` throws if the resource/export doesn't exist. `Call<T>` coerces the return
value safely (an export returning `int` can be read as `long` etc.). Typed
`Register<...>` overloads exist for 0–4 parameters; numbers that arrive as
`long`/`double` (e.g. from a forwarded msgpack payload) are coerced, and absurd
values become `default` instead of throwing inside the export. The handler runs
in the **caller's** context — irrelevant for synchronous handlers (the normal
case); don't register `Events.On` inside export handlers.

## State (reactive core store)

Server-internal key/value store in the native core — **not** replicated (use state bags
for that), but reactive and cross-resource:

```csharp
State.SetInt("score", 1337);          State.GetInt("score");
State.SetFloat("speed", 42.5);        State.GetFloat("speed");
State.SetBool("active", true);        State.GetBool("active");
State.SetString("phase", "lobby");    State.GetString("phase");   // null if missing
State.SetTable("stats", new Dictionary<string,int>{ ["hp"]=100 });
var s = State.GetTable<Dictionary<string,int>>("stats");
State.Delete("score");                // remove the key entirely

State.OnChange(key => { if (key == "phase") ... });   // called on EVERY change
```

`OnChange` handlers run synchronously on the setter's thread — only read/pass values
along there; don't fire natives.

> **Script-thread only.** Like natives, `State` is meant to be used from the script thread
> (OnStart/OnTick/handlers, or an `await`ed continuation — those resume on it). Reading a
> string/table from a raw `Task.Run(...)` thread *while another thread overwrites the same
> key* can race the core's copy. Keep state access on the script thread and this cannot
> happen. See [Threading model](#threading-model--dont-block-on-tasks).

## Db

Parameterized SQL queries (placeholders `@p0, @p1, …` in argument order) against a
configurable provider: **SQLite** (zero-setup default, file `flash-engine.db` in the
server data directory) or **MySQL/MariaDB** (what production servers run).

Configure in `server.cfg` — no code needed:

```cfg
set flash_db_provider   "mysql"
set flash_db_connection "Server=127.0.0.1;Database=flash;User=flash;Password=..."
```

Use `set`, **not** `sets` — `sets` publishes the value (your DB password!) to the
public server list. Alternatively configure in code, once in `OnStart`:

```csharp
Db.Configure("Data Source=my-server.db");            // SQLite
Db.Configure("mysql", "Server=...;Database=...");    // MySQL/MariaDB
```

```csharp
Db.Execute("CREATE TABLE IF NOT EXISTS kills (name TEXT, n INTEGER)");
Db.Execute("UPDATE kills SET n = n + 1 WHERE name = @p0", name);
long n   = Convert.ToInt64(Db.Scalar("SELECT n FROM kills WHERE name=@p0", name) ?? 0L);
var rows = Db.Query("SELECT name, n FROM kills ORDER BY n DESC LIMIT 10");
foreach (var r in rows) Log.Info($"{r["name"]}: {r["n"]}");

long id = Db.Insert("INSERT INTO kills (name, n) VALUES (@p0, 0)", name); // new row id
```

**Async variants** (`ExecuteAsync` / `QueryAsync` / `ScalarAsync` / `InsertAsync`) run
the database work off-thread and resume on the script thread — natives/state stay safe
after the `await`:

```csharp
var rows = await Db.QueryAsync("SELECT * FROM accounts WHERE license=@p0", lic);
long id  = await Db.InsertAsync("INSERT INTO accounts (license) VALUES (@p0)", lic);

// Several statements in ONE transaction (all or nothing) -- for multi-row
// invariants like a money transfer (debit + credit + audit row):
await Db.ExecuteBatchAsync(
    ("UPDATE accounts SET cash = cash - @p0 WHERE id = @p1", new object?[] { 500, fromId }),
    ("UPDATE accounts SET cash = cash + @p0 WHERE id = @p1", new object?[] { 500, toId }));
```

Rule of thumb: sync calls are fine for `OnStart`/DDL and SQLite; use the async API in
per-player or per-frame hot paths — with MySQL a sync query would block the whole
server frame for a network round-trip. `Db.Provider` (`"sqlite"`/`"mysql"`) lets you
pick dialect-specific SQL (e.g. `AUTOINCREMENT` vs `AUTO_INCREMENT`) where needed.
`Db.Insert`/`InsertAsync` is the portable way to get the generated key (`INSERT …
RETURNING` does not exist on MySQL 8).

## Async

`async/await` that stays on the server script thread (natives/state remain safe after
an `await`):

```csharp
await Async.Delay(1000);        // 1s without blocking the server frame
await Async.NextFrame();        // until the next tick

IDisposable t = Async.SetTimeout(5000, () => Log.Info("once after 5s"));
IDisposable i = Async.SetInterval(60_000, SaveAll);   // until i.Dispose()

// Cancellable waits: pair with the player's drop token so countdowns die with
// the player instead of resuming against an empty session (TaskCanceledException).
await Async.Delay(30_000, Players.Get(netId).DropToken());

// Wall-clock scheduling (server-local time; interval loops can't hit 04:00):
IDisposable d = Async.DailyAt(4, 0, RunNightlyPayout);
IDisposable h = Async.HourlyAt(30, CleanupLoop);
```

Event/command handlers may be `async` too. Only callable inside a resource
(OnStart/OnTick/handlers).

### Threading model — don't block on tasks

Everything runs on the single FiveM **script thread**; an `await` resumes back on it once
the frame drains. Because of that, **never block the script thread waiting on a Flash
task** — `.Result`, `.Wait()`, or `.GetAwaiter().GetResult()` on a `Db`/`Http`/`Async`
task will **deadlock the server**: the task's continuation needs the script thread to
resume, but you're holding that thread hostage waiting for the task. The FiveM watchdog
then kills the process. Always `await` instead:

```csharp
var rows = await Db.QueryAsync("SELECT ...");   // correct
var rows = Db.QueryAsync("SELECT ...").Result;  // WRONG — deadlocks the server
```

Likewise, don't touch natives, `State`, or state bags from a raw `Task.Run(...)`
thread-pool thread — those are script-thread-only. Do the work inside the resource (an
`await`ed continuation is already back on the script thread); the `Db`/`Http` async APIs
handle the thread hop for you.

## Routing buckets (virtual worlds)

Players/entities in different buckets can't see or touch each other — the
primitive for instanced apartments, mission lobbies, dealer interiors:

```csharp
int world = RoutingBuckets.Allocate();      // unique instance world (ids from 1000)
RoutingBuckets.MovePlayer(netId, world);
RoutingBuckets.MoveEntity(vehicleEntity, world);
RoutingBuckets.SetLockdownMode(world, "strict"); // server-authoritative entities only
RoutingBuckets.Release(world);              // players return to world 0
```

## Diagnostics (error hook)

The framework isolates and logs every handler error — this hook makes them
programmatically available (Discord/Sentry forwarding):

```csharp
Diagnostics.OnUnhandled((context, ex) =>   // context e.g. "event:buyItem", "scheduler"
    _ = Http.Post(webhookUrl, JsonSerializer.Serialize(new { content = $"{context}: {ex.Message}" })));
```

Log levels are color-coded in the server console (warnings yellow, errors red).

## Http

Server-side HTTP requests (Discord webhooks, external APIs) — non-blocking:

```csharp
var resp = await Http.Get("https://api.example.com/status");
if (resp.Ok) { var data = resp.Json<MyDto>(); }

var post = await Http.Post("https://discord.com/api/webhooks/...",
    JsonSerializer.Serialize(new { content = "Server started" }),
    new Dictionary<string,string> { ["Content-Type"] = "application/json" });

// Fully configurable: method, body, headers, timeout (default 30s; <=0 = off)
var r = await Http.Request(url, "PUT", body, headers, timeoutMs: 10_000);
```

`HttpResponse`: `Status`, `Body`, `Headers`, `Error` (null when ok), `Ok` (2xx),
`Json<T>()`. On timeout/failure: `Status == 0` + an `Error` text.

## Natives

Practically **all** FiveM/GTA natives as typed wrappers — server-side mostly
`Flash.Natives.Cfx`:

```csharp
var ped  = Natives.Cfx.GetPlayerPed(netId.ToString());   // server natives: playerSrc as string
var pos  = Natives.Cfx.GetEntityCoords(ped);              // Vector3 return
var veh  = Natives.Cfx.CreateVehicleServerSetter(hash, "automobile", pos.X, pos.Y, pos.Z, 0f);
```

- Handles are distinct types (`Player`, `Ped`, `Vehicle`, `Entity`, …) — the compiler
  prevents mix-ups. `Vector3` with `X/Y/Z`.
- Natives returning objects yield `object?` (msgpack-decoded: `object?[]`/maps).
- Callback parameters (`func`) take a `Func<object?[], object?>`.
- Raw pointer parameters are passed through as `nint` (escape hatch for power users).

## Grid

Server-authoritative 2D spatial grid (150m cells = FiveM sector) for **your own**
objects/markers/NPC management + fast radius queries:

```csharp
Grid.Insert(id, pos, Priority.Cosmetic);   // upsert (id is yours, e.g. NetID)
Grid.Remove(id);

ulong[] near = Grid.Query(playerPos, radius: 50f, maxHeight: 20f, max: 32);
ulong[] budg = Grid.QueryBudgeted(playerPos, 200f, budgetPerCell: 10);  // VRAM protection;
// Priority.Critical ALWAYS passes (anti-ESP), the rest is capped per cell
```

Results are sorted by priority (more important first), then distance.

## Culling

Drives FiveM's engine-enforced entity culling for **networked** entities
(server-authoritative — the client never even receives distant entities):

```csharp
Culling.Apply(entity, Priority.Cosmetic);      // category -> radius (75m)
Culling.SetEntityRadius(entity, 300f);         // explicit radius
Culling.SetPlayerRadius(netId, 200f);          // weak client -> fewer entities
```
