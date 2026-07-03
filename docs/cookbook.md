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

## Spawn at the last saved position

`flash-core` samples every online player's position server-side (OneSync) — on
each auto-save tick and on disconnect — and stores it per character. Its client
script asks the server where to spawn: rejoining players continue where they
left; a fresh character (no saved position) uses the spawnmanager's default
spawn points, as do respawns after death.

Requirements: `spawnmanager` running, and `ensure flash-core` AFTER
`basic-gamemode` (flash-core takes over the auto-spawn callback — last one
wins). Server-side, the current position is also queryable:

```csharp
var pos = Exports.Call<Dictionary<string, object?>>("flash-core", "getPosition", netId);
// { x, y, z, heading } or null (no ped / never sampled)
```

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
