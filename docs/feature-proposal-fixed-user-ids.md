# [Feature Request] End-to-End Integration of Fixed User IDs (Persistent Account IDs)

## Status Quo & Motivation

In FiveM, players are identified by a temporary `netId` (source), which is volatile and resets/reassigns on every reconnect or server restart (e.g., `1`, `42`, `105`). 

For a production-ready gameserver, a persistent, human-readable **Fixed User ID** (Account ID / Permanent ID / ID shown in `/showid` or overhead) is essential. While the gameplay framework (`flash-core`) already creates an `accounts` table in the database and assigns an `AccountId` (`id` primary key) mapped to the player's Rockstar license, this ID is currently isolated:

1. **No SDK Access:** Third-party resources using `Flash.Sdk` have no direct way to query a player's stable `AccountId` except by making expensive cross-resource export calls to `flash-core`.
2. **Limited Command Resolution:** The Command Router in `Flash.Sdk` only resolves command arguments of type `ServerPlayer` by matching the string to a connected player's temporary `netId`. It cannot resolve them by their `AccountId`.
3. **Admin Tooling Limitations:** Moderation commands in `flash-admin` (`/ban`, `/unban`, `/warn`, `/setlevel`) cannot target offline players via their `AccountId` because `flash-admin` does not query the `accounts` table to map the ID back to a stable license.

---

## Proposed Goal

Establish **Fixed User IDs** as a first-class citizen across the Flash-Engine ecosystem (SDK, Command Router, `flash-core`, and `flash-admin`). 

This will enable:
* Writing `player.Id` or `player.AccountId` directly in C# scripts.
* Typing commands like `/givemoney 15 100` where `15` is resolved to the player with `AccountId == 15` (or fallback to `netId == 15`).
* Moderating offline players via `/ban 15 7d "Cheating"`, automatically mapping the ID `15` to the underlying Rockstar license in the database.

---

## Technical Design & Implementation Plan

### 1. SDK Support: Player Metadata (`Flash.Sdk`)

Add a standard property to `ServerPlayer` that retrieves the persistent ID. Since the SDK is database-agnostic, the ID can be fetched from a standardized State Bag key (replicated from `flash-core`).

#### Proposed Changes in `ServerPlayer` ([Players.cs](file:///d:/Programmieren/Runtime/Flash-Engine/src/dotnet/Flash.Sdk/Players.cs)):
```csharp
public readonly struct ServerPlayer
{
    // ... existing properties ...

    /// <summary>
    /// The persistent, database-backed Account ID (Fixed User ID) of the player.
    /// Returns null if the gameplay framework has not assigned/replicated it yet.
    /// </summary>
    public int? AccountId => State.Get<int>("id") is int val && val > 0 ? val : null;
}
```

---

### 2. Command Router Upgrades (`Flash.Sdk`)

The Command Router parses arguments and binds them to method parameters. If a parameter is of type `ServerPlayer`, the router should search for the target player using:
1. **Network ID (`netId`):** Direct match with `netId`.
2. **Account ID (`AccountId`):** Match with the replicated `"id"` state bag value.

#### Proposed Logic in Command Parser:
```csharp
private static ServerPlayer? ResolvePlayer(string arg)
{
    if (!int.TryParse(arg, out int id)) return null;

    // 1. Try NetID match
    var playerByNet = Players.Get(id);
    if (playerByNet.Connected) return playerByNet;

    // 2. Try Account ID match (Fixed User ID)
    foreach (var player in Players.All)
    {
        if (player.AccountId == id) return player;
    }

    return null;
}
```

---

### 3. State Replication in Gameplay Framework (`flash-core`)

Immediately after a player joins and their `PlayerSession` is loaded or created in the database, `flash-core` must set the `"id"` key in the player's State Bag. This replicates the ID to the client and makes it readable by `ServerPlayer.AccountId` across all resources.

#### Proposed Changes in `flash-core` ([Main.cs](file:///d:/Programmieren/Runtime/Flash-Engine/src/dotnet/flash-core/Main.cs)):
```csharp
private async Task OnJoinAsync(int netId)
{
    // ... DB loading logic ...
    
    var session = new PlayerSession { AccountId = accountId, License = license, ... };
    s_online[netId] = session;

    // Replicate persistent Account ID to State Bag
    var player = Players.Get(netId);
    player.State.Set("id", session.AccountId, replicated: true);
    
    // ... further initialization ...
}
```

This also enables client-side scripts to read the ID instantly via `LocalPlayer.state.id` or `Player(serverId).state.id` for HUDs and player lists without Server RPCs.

---

### 4. Database-backed Offline Moderation (`flash-admin`)

To allow moderation commands to target offline players, `flash-admin` needs to map a given `AccountId` string (e.g. `"15"`) back to a Rockstar license. 

Since `flash-core` and `flash-admin` share the same database connection (SQLite or MySQL configured via convars), `flash-admin` can query the `accounts` table directly.

#### Proposed Lookup Helper in `flash-admin` ([Main.cs](file:///d:/Programmieren/Runtime/Flash-Engine/src/dotnet/flash-admin/Main.cs)):
```csharp
private static async Task<string?> ResolveLicenseFromAccountIdAsync(int accountId)
{
    // Fallback/direct query on the accounts table (created by flash-core)
    var result = await Db.ScalarAsync(
        "SELECT license FROM accounts WHERE id = @p0", accountId);
    return result?.ToString();
}
```

Commands like `/ban <target> <duration> <reason>` can now do:
1. Check if `<target>` matches a connected player (by NetID or AccountID) -> get their license.
2. If not connected, check if `<target>` is a numeric AccountID -> query the database using the helper -> get license.
3. If not found, treat `<target>` as a direct license string (e.g., `license:1a2b3c...`).

---

## Benefits

* **Unified Developer Experience (DX):** Accessing `player.AccountId` is seamless and framework-agnostic.
* **Intelligent Command Handling:** Admins and players can use the same persistent IDs for gameplay commands (e.g., `/pay 15 500` or `/freeze 15`) without mismatching network IDs.
* **Robust Moderation:** Admins can ban offline griefers via their standard ID, without needing to extract their complex hexadecimal Rockstar licenses from logs.
