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

## Deferrals (connect gate)

Hold the connection process and admit/reject — whitelists, bans, queues:

```csharp
Events.OnPlayerConnecting((name, deferrals, src) =>
{
    deferrals.Defer();
    deferrals.Update("Checking whitelist …");
    bool ok = /* DB check via identifiers of Players.Get(src) */;
    if (ok) deferrals.Done();
    else    deferrals.Done("You are not whitelisted.");
});
```

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
// Provider:
Exports.Register("addMoney", a => AddMoney(ToInt(a[0]), a[1]?.ToString() ?? "cash", ToInt(a[2])));

// Consumer:
bool ok = Exports.Call<bool>("flash-core", "addMoney", netId, "cash", 100);
```

`Call` throws if the resource/export doesn't exist. The handler runs in the **caller's**
context — irrelevant for synchronous handlers (the normal case); don't register
`Events.On` inside export handlers.

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

## Db

Parameterized SQL queries (placeholders `@p0, @p1, …` in argument order).
Default: SQLite file `flash-engine.db` in the server data directory.

```csharp
Db.Configure("Data Source=my-server.db");    // optional, once in OnStart

Db.Execute("CREATE TABLE IF NOT EXISTS kills (name TEXT, n INTEGER)");
Db.Execute("UPDATE kills SET n = n + 1 WHERE name = @p0", name);
long n   = Convert.ToInt64(Db.Scalar("SELECT n FROM kills WHERE name=@p0", name) ?? 0L);
var rows = Db.Query("SELECT name, n FROM kills ORDER BY n DESC LIMIT 10");
foreach (var r in rows) Log.Info($"{r["name"]}: {r["n"]}");
```

Calls are synchronous (they briefly block the script thread) — fine for SQLite; don't
run big queries every frame.

## Async

`async/await` that stays on the server script thread (natives/state remain safe after
an `await`):

```csharp
await Async.Delay(1000);        // 1s without blocking the server frame
await Async.NextFrame();        // until the next tick

IDisposable t = Async.SetTimeout(5000, () => Log.Info("once after 5s"));
IDisposable i = Async.SetInterval(60_000, SaveAll);   // until i.Dispose()
```

Event/command handlers may be `async` too. Only callable inside a resource
(OnStart/OnTick/handlers).

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
