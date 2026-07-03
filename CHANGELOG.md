# Changelog

All notable changes to Flash-Engine. Versioning: SemVer for the public `Flash.Sdk`;
the payload is additionally bound to one FXServer artifact version (FiveM has no
stable plugin ABI).

## Compatibility matrix

| Flash release | Flash.Sdk (NuGet) | Core contract | FXServer artifact (Windows) |
|---|---|---|---|
| 0.3.0 | 0.3.0 | v12 | **31689** (`06d4d348c`) |
| 0.2.0 | 0.2.0 | v12 | **31689** (`06d4d348c`) |
| 0.1.0 | 0.1.0 | v12 | **31689** (`06d4d348c`) |

Rules:
- **SDK ↔ payload:** The SDK checks the core version at startup and rejects a payload
  that is too old with a plain-text message. A newer payload with an older SDK is always
  fine (the contract only grows by appending).
- **Payload ↔ artifact:** pinned exactly. A different artifact version needs a payload
  release built for it.

## [0.3.0] — 2026-07-03

Framework + security release: multicharacter-ready schema, offline account API,
jobs/societies/paychecks, position persistence with spawn flow, async RPC, and a
security pass (event rate limiting, payload hardening, identifier enforcement,
SQLite CVE fix). The native core is unchanged (same core contract v12, same
FXServer artifact pin) — the payload update is managed-only (host + SDK).

**SDK (`Flash.Sdk`)**
- `Flash.Rpc` — awaitable request/response RPC between client and server on top
  of the event bus (correlation tickets, timeouts, script-thread continuations).
  Server answers clients via `Rpc.Register(name, handler)` (sync or async);
  server calls into clients via `await Rpc.Client<T>(netId, name, args)`
  (`TimeoutException` on silence). Client side ships as flash-core's Lua bridge:
  `exports['flash-core']:rpcCall(name, args[, cb])` (awaits without callback)
  and `rpcRegister(name, fn)`. Tickets are bound to the target netId (no
  forged answers); handler errors reply generically and log server-side. (#15)
- Security: rate limiting for incoming client events (token bucket per player
  and event name, checked BEFORE payload decode; server-internal events are
  never throttled). Convars `flash_event_rate_limit` (default 32/s),
  `flash_event_rate_burst` (64), `flash_event_rate_kick_after` (500 drops/10s
  → kick, 0 = never). Drops log once per abuse window; sustained flooding
  kicks. (#20)
- Security: `Flash.Args` — safe accessors for untrusted payloads (event/export/
  command args). Never throw: out-of-range numbers (e.g. `long.MaxValue` as an
  amount), NaN, wrong types or missing indices yield the default instead of an
  OverflowException in the handler; flash-core/flash-admin use them throughout.
  (#22)
- Security/deps: SQLite stack bumped to Microsoft.Data.Sqlite 10.0.9 +
  SQLitePCLRaw 3.0.3 — resolves the long-standing e_sqlite3 advisory
  (GHSA-2m69-gcr7-jv3q); the NU1903 suppression is gone. MySqlConnector
  2.4.0 → 2.6.1. Verified in-server on both providers. (#21)

**Framework resources**
- `flash-admin`: `set flash_require_identifier "true"` rejects connections
  without a cryptographic identifier at the connect gate (fail-closed) —
  ip/netid fallback keys are not session-stable and could let accounts/bans
  change owners. Default off (LAN/dev unchanged). (#19)
- `flash-core` position persistence + spawn flow: player positions are sampled
  server-side (OneSync) on every auto-save tick and on disconnect (only
  movement > 5 m marks the row dirty — idle players cost nothing) and stored
  per character (`pos_x/pos_y/pos_z/heading`, nullable; auto-migrated onto
  existing tables). New flash-core client script spawns rejoining players at
  their saved position via spawnmanager (first spawn/respawn = default spawn
  points; start flash-core after basic-gamemode). New `getPosition` export.
- `flash-core` jobs as data: `jobs`/`job_grades` tables (label + salary per
  grade; reference jobs seeded into an empty catalog), `setJob`/`setJobOffline`
  validate against the catalog (`false` for unknown job/grade), `getJobs`/
  `getJob`/`reloadJobs` exports. Paycheck loop pays each online player their
  grade's salary to the bank account (`flash_paycheck_minutes` convar, default
  10, 0 = off; `money_log` reason `paycheck:<job>`).
- `flash-core` societies (one shared fund per job): `societyGetBalance`/
  `societyDeposit`/`societyWithdraw` (atomic, funds/overflow guards in SQL)
  plus boss-menu primitives `societyDepositFromPlayer`/`societyWithdrawToPlayer`
  (compensate automatically if the second leg fails). Audited in `society_log`;
  `flashfw:societyChanged(job, balance)` event. Permission policy is up to the
  calling module.
- `flash-core` offline access: `getAccountById`/`getAccountByLicense` (account +
  active character, live session values when online, `netId` = -1 when offline)
  and `addMoneyOffline`/`removeMoneyOffline`/`setJobOffline` keyed by `cid`.
  Online targets are booked through the live session (no lost update against
  the write-through); offline targets are updated atomically in SQL
  (funds/overflow guard in the WHERE clause). All offline movements hit
  `money_log`; the exports return `Task<…>` (await the export result — no
  frame blocking). `flash-admin`: `offlinemoney <add|remove> <license|id>
  <amount> [reason]` console command (level 2+).
- `flash-core`: **multicharacter-ready schema** — `accounts` (identity: license,
  name; bans/admin levels key off it) is split from `characters` (save state:
  money, job, later position/appearance). Every account automatically plays
  character slot 1 until character selection ships, so players notice nothing.
  Existing databases (0.1/0.2, SQLite and MySQL) are migrated automatically and
  keep their money/jobs and permanent account ids; `money_log.player_id` is
  renamed to `cid`. New id convention for modules: `id` = account id (identity),
  `cid` = character id (save state; also in state bags and `getPlayer`). New
  `getCid` export. Existing exports keep their signatures and operate on the
  active character.

## [0.2.0] — 2026-07-03

Database + connect-gate release: async DB API with MySQL/MariaDB support, async
deferrals, money integrity. The native core is unchanged (same core contract v12,
same FXServer artifact pin) — the payload update is managed-only (host + SDK).

**SDK (`Flash.Sdk`)**
- `Flash.Db`: async API (`ExecuteAsync`/`QueryAsync`/`ScalarAsync`/`InsertAsync`) —
  database work runs off-thread, the `await` resumes on the script thread. Calling the
  async API outside a resource context fails with a clear error (instead of a silent
  off-thread continuation).
- `Flash.Db`: MySQL/MariaDB provider (MySqlConnector) next to SQLite; selected via
  `server.cfg` convars `flash_db_provider` + `flash_db_connection` (or
  `Db.Configure(provider, connectionString)`). `Db.Provider` exposes the active
  provider for dialect-specific SQL.
- `Flash.Db.Insert`/`InsertAsync`: INSERT that returns the generated row id,
  portable across SQLite and MySQL (same-connection `last_insert_rowid()` /
  `LAST_INSERT_ID()`).
- `ServerPlayer.Connected` — check after an `await` in join paths (drop race).
- `Db.ExecuteBatchAsync` — several statements in ONE transaction (all or nothing),
  for multi-row invariants like money transfers. Rollback proven live on SQLite
  and MariaDB/InnoDB.
- **Async deferrals**: received funcrefs stay valid across ticks (proven live:
  lua callback invoked from C# after an `await`). New async
  `Events.OnPlayerConnecting(Func<…, Task>)` overload with a safe contract:
  auto-defer (incl. the one-tick gap the core wants), return without `Done` =
  admit, unhandled exception = reject (fail-closed). `Deferrals.Completed`;
  `Done`/`Update` after the decision are ignored.

**Framework resources**
- `flash-core`: join load + auto-save are async (no frame blocking with MySQL);
  drop/stop still save synchronously (continuations no longer run after stop).
  MySQL schema variant; dirty-flag saves (only changed accounts); money overflow
  guard; `flashfw:moneyChanged` event; loud warning on weak (ip/netid) identifier
  fallback.
- `flash-core` money integrity: **write-through** — money/job changes hit the DB
  immediately (a crash no longer loses up to 60 s of transactions; the 60 s
  auto-save remains as a sweeper for failed writes). New `transferMoney` export
  (atomic player-to-player: funds/overflow checked, both rows + both audit rows
  in one DB transaction). New `money_log` audit table (ts, player id, account,
  delta, balance, reason); money exports take an optional reason string
  (defaults to the calling resource), flash-admin logs `admin:<name>`.
- `flash-admin`: connect gate is async (bans checked via `Db.QueryAsync` while the
  connection is deferred) + optional **whitelist** (`set flash_whitelist "true"`,
  `whitelist add|remove|check <netId|license>` console command, `isWhitelisted`
  export). MySQL dialect support (DDL, upserts); ban now persists BEFORE the kick;
  join/audit/ban-list paths async.

## [0.1.0] — 2026-07-01

First publishable release. Server-side C# resources for FiveM (Windows).

**Engine/core**
- Runtime-loaded FXServer component (stock artifact, no custom server), .NET 10 host
  with per-resource isolation (collectible AssemblyLoadContexts, `restart` without a
  server restart), versioned core contract (v12).
- Verified against official artifact 31689 (component loads, host boots, resources
  start, self-tests green).
- Plain-text diagnostics in the server log (missing .NET 10, incomplete payload,
  version conflict, resource load errors).

**SDK (`Flash.Sdk`)**
- Lifecycle `IResource`/`ITickable`; `Flash.Log`.
- `Flash.Events` (server-internal + client↔server, msgpack interop with Lua/JS),
  `Events.Source`/`SourceNetId`, `OnPlayerConnecting` with deferrals.
- `Flash.Players` / `ServerPlayer` (name, identifiers, ping, kick, emit, state bag).
- `Flash.Commands` (funcref-based), `Flash.Exports` (C#↔C#, with return values).
- `Flash.StateBag`/`GlobalState`/`player.State` (replicated, SET+GET).
- `Flash.State` — reactive core store (int/float/bool/string/bytes/tables, delete).
- `Flash.Async` (`Delay`/`NextFrame`/`SetTimeout`/`SetInterval`; continuations on the
  script thread), `Flash.Http` (`Get`/`Post`/`Request` with timeout).
- `Flash.Db` — parameterized queries, SQLite adapter (BYO-DB design).
- `Flash.Natives.*` — effectively 100% of the FiveM/GTA natives (generated + a few
  hand-written wrappers), `Flash.Grid` (spatial queries), `Flash.Culling`.

**Framework resources (reference/base)**
- `flash-core`: accounts (permanent player id, money, job) with SQLite persistence,
  schema migration, replication via state bags, export API.
- `flash-admin`: server-authoritative admin menu (permissions, kick/ban with a
  deferral gate, player/world/vehicle management, audit log) + Lua client UI.

**Tooling/distribution**
- `install-flash.ps1` (idempotent, .NET 10 check), `stage-flash-payload.ps1`,
  `make-public-repo.ps1` (closed-core split), `dotnet new` template
  (`Flash.Templates`), CI (Zig tests, .NET build, NuGet pack).
