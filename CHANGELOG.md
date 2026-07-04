# Changelog

All notable changes to Flash-Engine. Versioning: SemVer for the public `Flash.Sdk`;
the payload is additionally bound to one FXServer artifact version (FiveM has no
stable plugin ABI).

## Compatibility matrix

| Flash release | Flash.Sdk (NuGet) | Core contract | FXServer artifact (Windows) |
|---|---|---|---|
| 0.4.1 | 0.4.1 | v12 | **31689** (`06d4d348c`) |
| 0.4.0 | 0.4.0 | v12 | **31689** (`06d4d348c`) |
| 0.3.0 | 0.3.0 | v12 | **31689** (`06d4d348c`) |
| 0.2.0 | 0.2.0 | v12 | **31689** (`06d4d348c`) |
| 0.1.0 | 0.1.0 | v12 | **31689** (`06d4d348c`) |

Rules:
- **SDK ↔ payload:** The SDK checks the core version at startup and rejects a payload
  that is too old with a plain-text message. A newer payload with an older SDK is always
  fine (the contract only grows by appending).
- **Payload ↔ artifact:** pinned exactly. A different artifact version needs a payload
  release built for it.

## [0.4.1] — 2026-07-04

Bug-fix release from a focused review of seven reported issues (#147–#153), each verified
against the code and covered by a build + targeted test. **Managed-only**: the fixes are in
`Flash.Sdk` and the private host — the native core is unchanged, so the core contract (v12)
and the FXServer artifact pin (31689) stay the same. A newer SDK on the 0.4.0 payload is fine.

**Security / reliability**
- Disconnect cleanup now actually runs. The rate-limiter free (#81) and the pending
  server→client RPC cancellation (#100) were gated on the source starting with `"net:"`, but
  the core tags a genuine `playerDropped` as `"internal-net:<id>"` — so the checks never matched
  a real disconnect (state was reclaimed only by the 60 s idle sweep; RPCs hung until timeout, or
  forever with no timeout). Worse, the old prefix *did* match a client-forged `net:` drop, letting
  a client reset its own rate-limiter/abuse counter to evade flood protection. Both paths now
  require the genuine `internal-net:` prefix and parse the NetID after the last colon. (#147)
- `State.OnChange` dispatch is now thread-safe. The change trampoline can fire on a thread-pool
  thread (state set off-thread) while `OnChange`/`ClearResource` mutate the handler dictionary on
  the script thread; the plain `Dictionary` could throw `InvalidOperationException` (collection
  modified) or corrupt. Access is serialized with a lock and handlers are snapshotted under it and
  invoked outside it. (#148)
- `StateBag.Get` guards the msgpack decode. A client-owned bag (player/entity state, strict mode
  off) can be replicated with malformed bytes; the unguarded decode threw straight into the calling
  resource (remote crash / DoS). A bad payload is now treated as absent and logged. (#152)

**Memory / lifecycle**
- `Async.Delay(ms, token)` no longer leaks. The `CancellationTokenRegistration` was never disposed,
  so a long-lived token (e.g. `player.DropToken()`) kept the callback — and through it the TCS,
  its Task and the captured async state machine — rooted after the delay completed. A per-second
  `await Delay(ms, dropToken)` loop leaked one of each per iteration; the registration is now
  disposed when the delay settles. (#149)
- Resource unload completes pending delay timers instead of dropping them. `FlashSyncContext.Clear()`
  cleared `_timers` without completing the tasks, leaving the suspended `await Async.Delay(...)`
  state machines rooted and pinning the collectible ALC on every hot-reload. Clear now completes
  them (their continuations are dropped because the context is already dead, so no resource code
  runs after stop). (#150)

**DX**
- The command router binds nullable primitive parameters. `[Command]` methods with `int?`/`bool?`/
  `long?`/… (for optional args) fell through the type switch to "unsupported" and the command was
  unusable; the switch now resolves `Nullable<T>` to its underlying type. (#151)
- `Args.To<T>` coerces nullable primitives. `Args.To<int?>(5L)` (and the export/RPC paths built on
  it) returned `null` instead of `5` because the exact-type checks never matched `Nullable<T>`; it
  now resolves the underlying type (and maps a null input to `null`, not `0`). (#153)

## [0.4.0] — 2026-07-04

Security, reliability & economy-integrity release: three verified issue reviews worked
through end-to-end (crash-atomic society transactions, stale-save and transfer-deadlock
fixes, scheduler DoS/leak hardening, a state-store use-after-free fix, and a lifecycle-event
forgery guard), plus new SDK primitives (`Db.ExecuteGuardedBatchAsync`, `Events.IsFromCore`).
Verified in the real server on SQLite **and** MySQL/MariaDB (both self-tests green) and via
Zig core unit tests. Unlike 0.2.0/0.3.0 this is **not** managed-only: the native core is
rebuilt (state-store UAF fix + native-invoker bounds), but the core contract (v12) and the
FXServer artifact pin (31689) are unchanged.

**Security / reliability pass 2** (from the third issue review — verified against the code)
- Native invoker hardened against stack smashing: the generated `invokeRaw`/`invokeVec3`
  clamp the argument count to the 32-slot native context **before** copying into the
  stack buffer. A call with >32 args could previously overwrite the buffer (the C++ shim
  already capped the downstream count, but the Zig-side copy ran first). (#102)
- Server→client RPC calls to a player are cancelled on that player's disconnect
  (`OperationCanceledException` instead of hanging until the 5 s timeout — or forever
  when called with no timeout), from the same disconnect choke point as the rate-limiter
  cleanup. (#100)
- `flash-admin` self-test cleans up its temporary `selftest:admin` rows in a `finally` —
  a throwing/failing test no longer leaves admin-level/ban/whitelist rows in the
  production DB (matches the flash-core fix #87). (#101)
- `ServerPlayer.DropToken()` for an already-disconnected player returns an
  already-cancelled token instead of allocating a `CancellationTokenSource` that would
  never fire or be freed (leak + hanging awaiter). (#104)
- `flash-core` economy integrity: the auto-save sweeper now identity-checks each
  snapshotted session against the live one before writing — a player who drops and
  reconnects (same character) during a save pass can no longer have the stale session
  overwrite the fresh reconnect state (progress rollback / cash duplication, #86). The
  atomic `transferMoney` batch retries on a transient DB deadlock/lock-timeout, so an
  InnoDB deadlock no longer drops the two `money_log` rows while keeping the balances
  (both now survive together, #79).
- `flash-core` society↔player transactions are now crash-atomic (#67). Depositing to /
  withdrawing from a society fund was a compensated saga (book the player, then move the
  fund) — a hard crash in the one-`await` window could vaporize money. Both now book the
  player in memory optimistically and persist the player row + the guarded society UPDATE
  + the audit row in a single transaction; a rejected society guard (overflow/coverage)
  or a DB error rolls the in-memory booking back. The player's cash and the fund balance
  move together or not at all.

- Scheduler hardening: the per-frame async drain is now bounded — it processes the
  continuations queued at the start of the frame and defers any posted while draining to
  the next frame. A `while (true) { await Task.Yield(); }` loop (or any await that re-posts
  synchronously) now advances one step per frame instead of hijacking the server thread
  until the FiveM watchdog kills the process (#72). A resource's sync context is also
  marked dead on unload, so a database task that completes *after* the resource stopped has
  its continuation dropped instead of pinning the collectible `AssemblyLoadContext` (memory
  creep on repeated restarts, #82).
- Docs: threading model — never block the script thread on a Flash task (`.Result`/`.Wait`
  deadlocks the server), and keep `State`/native access on the script thread (#96).
- Core: the reactive `State` store's string/bytes getters now copy the value under the
  store lock into a per-thread buffer and return a pointer to that, instead of returning a
  pointer into store-owned memory after releasing the lock. A concurrent set/delete from a
  thread-pool thread can no longer free the value mid-read (use-after-free). Same C-ABI, so
  it ships with the next native core build. (#84)
- Security: lifecycle events are now source-verified. A client can `TriggerServerEvent`
  a fake `playerJoining`/`playerDropped`, which would let it desync its own server-side
  session (a base for dupe attempts). `flash-core`/`flash-admin` now act on these only when
  the event actually came from the server core (`Events.IsFromCore` — source
  `internal-net:` vs a client's `net:`), which is unspoofable. A genuine drop is always
  `internal-net:`, so it's never falsely rejected. (#73)

**SDK (`Flash.Sdk`)**
- `Events.IsFromCore` — true when the running event was triggered by the server core
  (source `internal-net:`), false for a client-forged one (`net:`). Gate trust of
  lifecycle events (playerJoining/playerDropped/playerConnecting) with it. (#73)
- `Db.ExecuteGuardedBatchAsync(params (string Sql, object?[] Args, int RequiredAffected)[])`
  — like `ExecuteBatchAsync`, but each statement can assert an exact affected-row count;
  a guarded statement that matches a different number of rows rolls the whole transaction
  back and returns `false` (a clean rejection, not an error). Makes conditional multi-row
  invariants (a `WHERE`-guarded UPDATE plus dependent writes) crash-atomic. (#67)

**Security / reliability pass** (from the second issue review — verified against the code)
- Msgpack decoder hardened against DoS: recursion depth limit (nested payload can no
  longer StackOverflow → uncatchable host kill, #68) and array/map length validated
  against the remaining bytes before allocating (forged huge count can no longer force
  a preallocation OOM, #77).
- Host bridges cap inbound payloads (16 MiB) before allocating — a single oversized
  event/ref payload can no longer force a huge LOH allocation (#63).
- `Args.Int` no longer throws on `uint` > int.MaxValue (MySQL `INT UNSIGNED`); returns
  default like every other out-of-range case (#85).
- Null-string guards on native boundaries (`State`, `Events.Emit`/`EmitClient`,
  `StateBag`) — a null key/name is coerced to "" instead of a native null-deref crash
  (#95). `Db.Provider` asserts the script thread on first (convar-reading) access
  instead of crashing off-thread (#94).
- `Events.Source`/`SourceNetId` now flow through an `AsyncLocal`, so they stay correct
  **after an `await`** inside a handler (previously reset to empty, #92).
- `State.OnChange` handlers are partitioned per resource and cleared on unload — they
  no longer pin the collectible ALC or run after resource stop (#83).
- Event rate-limiter state is freed on player disconnect (not only on kick), so a
  reused NetID can't inherit the previous player's drop counter ("NetID poisoning",
  false-positive kicks) or leak (#81).
- `flash-admin`: target validation — targeted actions reject NetID `-1`/offline (no
  more accidental server-wide heal/freeze/warn broadcast, #80), and a hierarchy check
  stops an actor from acting punitively on an equal-or-higher-ranked admin (#66).
- `flash-core`: disconnect save wrapped in try/finally (a DB error no longer strands a
  "zombie session" + bricked NetID, #71); write-through retries with exponential backoff
  instead of hot-spinning on a persistent DB error (#69); join re-validates the
  identifier after the async load to block NetID-reuse session hijacking (#78);
  replicated player state is cleared on disconnect (stale-read window on NetID reuse,
  #98); the self-test cleans up in a `finally` (#87).
- Docs: recommended `server.cfg` hardening (`sv_stateBagStrictMode`,
  `sv_filterRequestControl`, #74/#75) in Getting Started.

**SDK (`Flash.Sdk`) — DX pair**
- Attribute command router: `[Command]`-annotated methods + `Commands.RegisterAll(obj)`
  with automatic parameter parsing/validation (int/long/float/double/bool/string,
  `ServerPlayer` from a netId, injected `CommandContext`, `[Rest]` for the trailing
  line). Parse errors reply a generated usage line; async handlers awaited safely;
  handler errors routed to `Diagnostics`. (#12)
- Typed exports: `Exports.Register<...>` overloads (0–4 params) and safe `Call<T>`
  coercion — no manual `object?[]` indexing/casting; numbers arriving as `long`/
  `double` are coerced, absurd values become `default` instead of throwing.
  Shared numeric coercion via new `Args.To<T>` (also used by `Rpc`). (#10)

**SDK (`Flash.Sdk`) — QoL batch**
- `Vector3` math: operators, `Length`/`Distance`/`DistanceSquared`/`Distance2D`/
  `Dot`/`Normalized` (zero vector stays zero). (#35)
- `RoutingBuckets` — virtual-world manager: `Allocate`/`Release` (unique ids,
  players return to world 0 on release), `MovePlayer`/`MoveEntity`,
  `SetLockdownMode`, population toggle. (#36)
- `Async.Delay(ms, CancellationToken)` + `ServerPlayer.DropToken()` — waits that
  die with the player (TaskCanceledException) instead of resuming against an
  empty session. (#6)
- Wall-clock scheduling: `Async.DailyAt(hour, minute, cb)` and
  `Async.HourlyAt(minute, cb)` (server-local time, DST/NTP-safe chunked waits,
  never double-fires). (#13)
- `Diagnostics.OnUnhandled((context, ex) => …)` — programmatic hook for
  isolated handler/scheduler errors (Discord/Sentry forwarding); per-resource
  partitioned, cleaned on unload. (#24)
- Colored console levels: warnings yellow, errors red, debug cyan (FiveM inline
  color codes). (#38)

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
