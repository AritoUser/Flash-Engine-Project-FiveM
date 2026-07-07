# Flash Cookbook — recipes for common server tasks

Copy-paste-ready patterns. Basics: [Getting Started](getting-started.md) ·
Signatures: [API reference](api.md).

---

## Enable chat

The official FXServer artifact ships the chat resource **prebuilt** — it just needs to
be in your resources folder:

```powershell
Copy-Item -Recurse "<ServerDir>\citizen\system_resources\chat" "<DataDir>\resources\chat"
```
Then in `server.cfg`: `ensure chat`. From now on `/commands` and `chat:addMessage` from
your C# resources work:

```csharp
Players.Get(netId).Emit("chat:addMessage",
    new Dictionary<string, object?> { ["args"] = new object?[] { "[Shop]", "Purchased!" } });
```

## Let client and server talk

Flash is server-side — the game client stays Lua (for now). The pattern:

**Client (`client.lua` of the resource, `client_script` in the manifest):**
```lua
-- client -> server
RegisterCommand('buy', function(_, args)
    TriggerServerEvent('myshop:buy', args[1] or 'bread')
end, false)

-- server -> client
RegisterNetEvent('myshop:delivered', function(item, price)
    print(('Bought: %s for %d'):format(item, price))
end)
```

**Server (C#):**
```csharp
Events.On("myshop:buy", args =>
{
    int src = Events.SourceNetId;                    // who sent it?
    string item = args[0]?.ToString() ?? "";
    // ... validate server-authoritatively (money, stock) ...
    Players.Get(src).Emit("myshop:delivered", item, 250);
});
```

**Golden rule:** **never** trust the client. Prices, permissions, inventories — validate
everything on the server; the client only sends wishes and renders results.

## Whitelist on connect (async deferrals + DB)

> Tip: the bundled `flash-admin` already ships this (bans + whitelist, enable with
> `set flash_whitelist "true"`). The recipe below shows how to build your own gate.

```csharp
public void OnStart()
{
    Db.Execute("CREATE TABLE IF NOT EXISTS whitelist (license TEXT PRIMARY KEY)");

    // Async gate: defers automatically, returning without Done() admits, an
    // unhandled exception rejects (fail-closed). The DB roundtrip does not
    // block the server frame -- important with MySQL.
    Events.OnPlayerConnecting(async (name, deferrals, src) =>
    {
        string? lic = Players.Get(src).IdentifierOfType("license"); // before the first await
        deferrals.Update("Checking whitelist …");

        bool ok = lic != null &&
            (await Db.QueryAsync("SELECT 1 FROM whitelist WHERE license=@p0", lic)).Count > 0;

        if (!ok) deferrals.Done($"Hey {name}, you are not whitelisted.");
    });

    Commands.Register("wl", (src, args, raw) =>
    {   // console: wl <netId>  -> whitelist a player
        if (src != 0 || args.Length < 1) return;
        var lic = Players.Get(int.Parse(args[0])).IdentifierOfType("license");
        if (lic != null) Db.Execute("INSERT OR IGNORE INTO whitelist VALUES (@p0)", lic);
    });
}
```

## Build on flash-core (money/jobs)

`flash-core` manages accounts (permanent player id, cash/bank, job) with persistence.
Other resources use its exports — no need for your own account system:

```csharp
int  id   = Exports.Call<int>("flash-core", "getId", netId);         // permanent player id
int  cash = Exports.Call<int>("flash-core", "getMoney", netId, "cash");
bool ok   = Exports.Call<bool>("flash-core", "removeMoney", netId, "cash", 250); // false = too poor
Exports.Call("flash-core", "addMoney", netId, "bank", 1000);
Exports.Call("flash-core", "setJob", netId, "police", 2);

// Atomic player-to-player transfer (checks funds + overflow; false = rejected).
// Both account rows + both audit rows are written in ONE DB transaction.
bool sent = Exports.Call<bool>("flash-core", "transferMoney", fromNetId, toNetId, "cash", 500);

// Money ops take an optional reason string -- it ends up in the money_log audit
// table (ts, player_id, account, delta, balance, reason). Without it, the calling
// resource's name is logged. Admins query money_log first on dupe reports.
Exports.Call("flash-core", "addMoney", netId, "cash", 100, "shop:refund");

// React when an account is loaded (after join/restart):
Events.On("flashfw:playerLoaded",  a => { int netId = Convert.ToInt32(a[0]); ... });
Events.On("flashfw:jobChanged",    a => { /* netId, job, grade */ });
Events.On("flashfw:moneyChanged",  a => { /* netId, account, newBalance */ });
```

Client-side the balance is readable as replicated state (Lua):
`LocalPlayer.state.cash`, `LocalPlayer.state.job`, `LocalPlayer.state.id`,
`LocalPlayer.state.cid`.

**Two IDs, by design** (multicharacter-ready schema): `id` is the permanent
*account* id (identity — bans/admin levels key off it), `cid` is the *character*
id (the save state — money, job, and the `money_log` audit reference it). Until
character selection ships, every account automatically plays character slot 1,
so both ids are always present. Modules that store per-player data (inventory,
housing, …) should key it by `cid` (`Exports.Call<int>("flash-core", "getCid", netId)`).

**Offline access** (admin fines/refunds, rent/tax systems): these exports work
whether the player is online or not — online they book through the live session,
offline they update the DB atomically (funds/overflow guards in SQL). They return
a `Task`, so `await` the export's result:

```csharp
// Look up an account (works offline; netId is -1 when the player is offline):
var acc = await Exports.Call<Task<Dictionary<string, object?>?>>(
    "flash-core", "getAccountByLicense", "license:abc123")!;   // or getAccountById
int cid = Convert.ToInt32(acc!["cid"]);

bool ok = await Exports.Call<Task<bool>>(
    "flash-core", "removeMoneyOffline", cid, "cash", 500, "tax:weekly")!;
await Exports.Call<Task<bool>>("flash-core", "setJobOffline", cid, "unemployed", 0)!;
```

`flashfw:moneyChanged` only fires for online bookings (it carries a netId);
offline movements are recorded in `money_log`. From the server console:
`offlinemoney <add|remove> <license or account id> <amount> [reason]` (flash-admin).

## Jobs as data + societies (job funds)

Jobs live in the database (`jobs` + `job_grades`: label + salary per grade) —
`setJob` validates against this catalog and returns `false` for unknown
job/grade combos. An empty catalog is seeded with reference jobs
(`unemployed`/`police`/`ambulance`); edit them in the DB, then call the
`reloadJobs` export. Every online player receives their grade's salary on the
**bank** account each paycheck cycle (`set flash_paycheck_minutes "10"`,
`0` disables; reason `paycheck:<job>` in `money_log`).

```csharp
var job = Exports.Call<Dictionary<string, object?>>("flash-core", "getJob", "police");
bool ok = Exports.Call<bool>("flash-core", "setJob", netId, "police", 2);
await Exports.Call<Task<bool>>("flash-core", "reloadJobs")!;   // after editing the DB
```

Each job has a **society** (shared fund — the basis for boss menus). The ops are
atomic (funds/overflow guards in SQL), audited in `society_log`, and emit
`flashfw:societyChanged(job, balance)`. WHO may move money is the calling
module's policy (e.g. check the boss grade) — flash-core provides the mechanics:

```csharp
long bal = await Exports.Call<Task<long>>("flash-core", "societyGetBalance", "police")!;
await Exports.Call<Task<bool>>("flash-core", "societyDeposit", "police", 500, "fine:speeding")!;
await Exports.Call<Task<bool>>("flash-core", "societyWithdraw", "police", 200, "equipment")!;

// Player <-> society (boss menu primitives; compensate automatically on failure):
await Exports.Call<Task<bool>>("flash-core", "societyDepositFromPlayer", netId, "police", 100, "donation")!;
await Exports.Call<Task<bool>>("flash-core", "societyWithdrawToPlayer", netId, "police", 100, "bonus")!;
```

## Vitals (hunger/thirst) & combat-log guard

Hunger and thirst are **server-authoritative**: they live in the player session,
decay on a server interval (pausing or alt-tabbing the client freezes nothing) and
replicate as state bags for HUDs (`LocalPlayer.state.hunger` / `thirst` / `dead`).
Values are clamped to 0..100 and persisted with the character.

```cfg
set flash_vitals_minutes      "1"    # decay interval in minutes (0 = vitals off)
set flash_vitals_hunger_decay "1.5"  # percentage points per interval
set flash_vitals_thirst_decay "2.0"
set flash_combatlog_health    "101"  # ped health below this at disconnect = combat log
```

```csharp
float hunger = Exports.Call<float>("flash-core", "getVital", netId, "hunger");
Exports.Call("flash-core", "addVital", netId, "hunger", 25f);   // eating (clamped at 100)
Exports.Call("flash-core", "setVital", netId, "thirst", 100f);

// React to thresholds instead of polling — fires when a vital drops below 25/10/0:
Events.On("flashfw:vitalThreshold", args =>
{
    int netId = Args.Int(args, 0);       // then: vital name, threshold, current value
    // e.g. apply damage/effects client-side at 0, warn at 25
});
```

**Combat-log guard:** disconnecting with critical ped health marks the character
dead (`is_dead`, replicated as `dead`, `[SECURITY]` log + audit entry) and fires
`flashfw:combatLog(netId, accountId, cid, hp)` — reconnecting fully healed is over.
Dead characters stop decaying; revive from your ambulance/respawn module:

```csharp
bool dead = Exports.Call<bool>("flash-core", "isDead", netId);
Exports.Call("flash-core", "setDead", netId, 0);    // revive (fires flashfw:playerRevived)
```

## Set up flash-admin

1. `ensure flash-admin` (after `flash-core`).
2. Grant the first admin from the **server console**: `setadmin <netId> 3`
   (persisted via the license identifier — survives rejoins).
3. In game: **F1** or `/admin` opens the menu (visible from level 1).

Levels: 1 = mod (kick/heal/freeze/teleport), 2 = admin (+ ban/money/job/vehicles/world),
3 = superadmin (+ grant levels). Every action is validated server-side and written to
the audit log (`admin_log` table); rejected attempts show up as `[SECURITY]` warnings in
the server log.

Queryable from your own resources: `Exports.Call<int>("flash-admin", "getLevel", netId)`.

### Ban hardening & identity gates (opt-in)

Bans are keyed on the license identifier, but a determined cheater just buys a second
GTA account. On every ban, flash-admin also records the client's **hardware tokens**
(`ban_hardware_tokens`); the connect gate rejects anyone presenting a banned token even
with a clean license — ban evasion from the same PC fails. Unbanning clears the tokens
too. This is always on and needs no config.

Optional identity requirements in the connect gate (all default off — a public server
leaves them off, a hardcore server turns them on without touching code):

```cfg
set flash_require_identifier "true"   # reject clients without any cryptographic id
set flash_require_discord    "true"   # require a linked Discord account
set flash_require_steam      "true"   # require Steam running
set flash_require_cfx        "true"   # require a linked Cfx.re account (fivem: id)
# Anti-VPN/proxy: if set, the async gate queries this URL ({ip} is substituted) and
# rejects when the response contains the reject marker. fail-open on API errors.
set flash_anti_vpn_api       "https://proxycheck.io/v2/{ip}?vpn=1&key=YOURKEY"
set flash_anti_vpn_reject_if "\"proxy\":\"yes\""
```

## Spawn at the last saved position

`flash-core` samples every online player's position server-side (OneSync) — on
each auto-save tick and on disconnect — and stores it per character. It exposes a
**neutral spawn contract** and lets a swappable *adapter* do the actual spawn:
rejoining players continue where they left; a fresh character (no saved position)
uses the adapter's default spawn point, as do respawns after death.

The shipped default adapter (`spawn_spawnmanager.lua`) uses the standard
`spawnmanager`. It's a **soft** dependency now: `ensure flash-core` after
`basic-gamemode` still works, but if spawnmanager is missing the adapter just logs
and disables instead of blocking startup. Server-side the position is queryable:

```csharp
var pos = Exports.Call<Dictionary<string, object?>>("flash-core", "getPosition", netId);
// { x, y, z, heading } or null (no ped / never sampled)
```

### Bring your own spawn (character select, custom UI)

Disable the default adapter with a **replicated** convar and handle the contract
in your own resource — no forking flash-core:

```cfg
setr flash_spawn_adapter "custom"   # (or "none") — stops spawn_spawnmanager.lua
```

```lua
-- client, your resource:
--   server -> client: flashfw:spawnAt(x, y, z, heading, hasPos)  where to spawn
--                      (hasPos=false -> use your own default point)
--   client -> server: TriggerServerEvent('flashfw:requestSpawn')  once ready to spawn
RegisterNetEvent('flashfw:spawnAt', function(x, y, z, heading, hasPos)
    -- your character-select / custom spawn goes here
end)
```

## Route command replies to your own chat

By default, `[Command]` replies and `flash-core`/`flash-admin` messages go to the
standard `chat:addMessage`. To send them through ox_lib or a custom NUI chat, set a
process-global reply sink once (e.g. in your core resource's `OnStart`):

```csharp
Commands.SetReplySink((netId, message) =>
    Players.Get(netId).Emit("ox_lib:notify", new Dictionary<string, object?> { ["description"] = message }));
// Commands.SetReplySink(null) restores the default chat:addMessage.
```

It's the single instance shared across resources, so this one override captures every
resource's command replies; it's auto-cleared when the setting resource stops.

## Transactional inventory (un-dupeable)

Item counts live in the **native core**, which makes every check-and-decrement atomic
under a lock — so "take more than exists" is mathematically impossible, even when two
requests arrive from different threads at the same millisecond (the classic spam-click
dupe). Unlike the script-thread-only registries, these calls are **safe from any thread**
(that's the whole point). You own item *types* and DB persistence; the core owns the
live counts.

```csharp
// Containers are your own u64 ids: a character id, a trunk plate hash, a stash id.
Inventory.Give(cid, "water", 3);
bool took = Inventory.Take(cid, "water", 1);          // false if not enough (atomic)
bool moved = Inventory.Move(trunkId, cid, "gun", 1);  // false if the source ran out
uint have = Inventory.Count(cid, "water");
```

`Move`/`Take` return `false` (nothing changed) when the source lacks the quantity — a
second concurrent move of the same last item loses, no duplicate. flash-core persists a
container to the `inventories` table:

```csharp
await Exports.Call<Task>("flash-core", "invSave", cid)!;   // core -> DB
await Exports.Call<Task>("flash-core", "invLoad", cid)!;   // DB -> core (absolute set)
```

Player inventories are auto-loaded on join and saved on drop, keyed by character id
(`set flash_inventory_persist "false"` to opt out). Trunks/stashes use their own ids and
call `invSave`/`invLoad` themselves.

## Vehicle garage (server-authoritative)

Owned vehicles are keyed by character id (`cid`) and their state lives in a `vehicles`
table as a JSON blob. Retrieving spawns the vehicle with `CreateVehicleServerSetter` and
applies the state **on the server** before it networks — a mod menu can't spoof the hash
or hand out free upgrades. Access (engine/lock gate) is the owner or an explicit key
holder (`vehicle_keys`).

```csharp
// Dealership: register a purchased vehicle to a character
await Exports.Call<Task<bool>>("flash-core", "garageRegister", cid, GetHashKey("adder"), "ABC123", stateJson)!;

// Garage menu: list, then drive one out at a position
var mine = Exports.Call<List<object?>>("flash-core", "garageList", netId);   // [{plate,model,stored,owned}]
int vehNetId = await Exports.Call<Task<int>>("flash-core", "garageRetrieve", netId, "ABC123", x, y, z, heading)!;

// Store the vehicle the player is sitting in (returns the plate)
string plate = Exports.Call<string>("flash-core", "garageStore", netId);

// Shared keys (the hook the inventory key-item builds on)
await Exports.Call<Task<bool>>("flash-core", "garageGiveKey", "ABC123", otherCid)!;
bool canUse = await Exports.Call<Task<bool>>("flash-core", "garageHasAccess", netId, "ABC123")!;
```

**Server-applied vs client-applied:** the server's apiset registers fewer vehicle natives
than the client. Model, plate, primary/secondary colours, body health, dirt and door
locks are applied server-side (un-spoofable). Fuel, engine/petrol health, livery, window
tint and the detailed tuning **mods** are client natives — they are still stored
server-side (the server owns *what* the vehicle has) and replicated on retrieve via the
`flashVehState` entity state bag for a thin client applier to apply on stream-in.

## Server-side anti-cheat (opt-in)

flash-core ships a server-authoritative integrity layer — all checks default **off** and
each is set to `off` / `log` / `block` / `kick`:

```cfg
set flash_ac_mass_spawn  "block"   # entity mass-spawn (mod-menu entity flood)
set flash_ac_weapon_damage "log"   # implausible weapon damage (damage modifier)
set flash_ac_speed       "kick"    # on-foot speed hack / noclip

set flash_ac_mass_spawn_limit "10"     # allowed spawns per window, per player
set flash_ac_mass_spawn_window_ms "10000"
set flash_ac_max_damage "200"          # single-hit damage above this is flagged
set flash_ac_max_speed  "15"           # on-foot m/s above this is flagged
```

`log` observes only (writes a `[SECURITY]` line + audit entry), `block` cancels the
offending action, `kick` removes the player. Admins bypass the physics check with the
ACE `flash.ac.bypass` (grant it to your noclip/admin role).

Legitimate mechanics grant themselves scoped, time-limited exceptions via `Flash.Security`
so a paintball match or a car dealership doesn't trip the guard:

```csharp
Flash.Security.AddWeaponException(netId, "WEAPON_PISTOL", minutes: 10);  // paintball
Flash.Security.BypassEntityLimits(netId, seconds: 5);                    // spawn a fleet
```

## Vehicle reconnect (crash recovery)

Crashing while driving (or flying) no longer means spawning on foot while the group
drives on: on disconnect flash-core remembers the vehicle and seat **in RAM only**
(keyed by license — a server restart clears it, which is correct because the vehicle
despawns anyway). If the player reconnects within the window and the vehicle still
exists and isn't destroyed, the client warps them back into their exact seat right
after spawning; the seat is skipped if someone took it meanwhile. Otherwise the normal
saved-position spawn applies (the drop position was sampled anyway).

```cfg
set flash_reconnect_vehicle_minutes "10"   # reconnect window (0 = feature off)
```

No script code needed — it is part of the flash-core spawn flow.

## Preserve state across restarts (hot-reload QoL)

Restarting a resource unloads its AssemblyLoadContext — all in-memory state dies,
which makes iterative development painful (rebuild the test lobby after every save).
Flag **static** fields/properties with `[PreserveState]` and they survive the restart:
the host serializes them to JSON before the unload and injects them back **before**
`OnStart()` of the new instance.

```csharp
[PreserveState]
private static Dictionary<int, LobbyConfig> s_lobbies = new();

[PreserveState("my_stable_key")]           // optional stable key (default: member name)
private static bool s_lobbyOpen;
```

Rules: static members only; the value must be `System.Text.Json`-serializable
(primitives, collections, POCOs with public properties) — unserializable values are
logged and skipped. The JSON round-trip is deliberate: it breaks references into the
old assembly so preserved state can never pin the unloaded ALC. State lives in host
memory only; a server restart clears it.

## Discord webhook (server notifications)

```csharp
private static Task Notify(string text) =>
    Http.Post("https://discord.com/api/webhooks/<id>/<token>",
        System.Text.Json.JsonSerializer.Serialize(new { content = text }),
        new Dictionary<string, string> { ["Content-Type"] = "application/json" });

// e.g. in OnStart:
Events.On("playerDropped", async _ =>
{
    var p = Players.Get(Events.SourceNetId);   // read Source BEFORE the await!
    string name = p.Name;
    await Notify($"{name} left the server.");
});
```

## Recurring tasks (autosave, announcements)

```csharp
private IDisposable? _saver, _ads;

public void OnStart()
{
    _saver = Async.SetInterval(60_000, SaveEverything);
    _ads   = Async.SetInterval(600_000, () =>
        Events.EmitAllClients("chat:addMessage",
            new Dictionary<string, object?> { ["args"] = new object?[] { "[Info]", "Discord: discord.gg/..." } }));
}

public void OnStop() { _saver?.Dispose(); _ads?.Dispose(); }
```

## Spread work across frames

```csharp
Events.On("world:build", async _ =>
{
    foreach (var chunk in bigList.Chunk(50))
    {
        foreach (var x in chunk) Process(x);
        await Async.NextFrame();   // don't block the server frame
    }
});
```
