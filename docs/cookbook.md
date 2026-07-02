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

## Whitelist on connect (deferrals + DB)

```csharp
public void OnStart()
{
    Db.Execute("CREATE TABLE IF NOT EXISTS whitelist (license TEXT PRIMARY KEY)");

    Events.OnPlayerConnecting((name, deferrals, src) =>
    {
        deferrals.Defer();
        deferrals.Update("Checking whitelist …");

        string? lic = Players.Get(src).IdentifierOfType("license");
        bool ok = lic != null &&
            Db.Query("SELECT 1 FROM whitelist WHERE license=@p0", lic).Count > 0;

        if (ok) deferrals.Done();
        else    deferrals.Done($"Hey {name}, you are not whitelisted.");
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

// React when an account is loaded (after join/restart):
Events.On("flashfw:playerLoaded", a => { int netId = Convert.ToInt32(a[0]); ... });
Events.On("flashfw:jobChanged",  a => { /* netId, job, grade */ });
```

Client-side the balance is readable as replicated state (Lua):
`LocalPlayer.state.cash`, `LocalPlayer.state.job`, `LocalPlayer.state.id`.

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
