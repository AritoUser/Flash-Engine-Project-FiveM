# Changelog

All notable changes to Flash-Engine. Versioning: SemVer for the public `Flash.Sdk`;
the payload is additionally bound to one FXServer artifact version (FiveM has no
stable plugin ABI).

> The player-lifecycle work below was **live-verified** end-to-end on 2026-07-10 with a
> real client on the local FXServer: connect → spawn → `playerReady` → death (with
> killer/weapon/cause) → server respawn at a hospital spawn point → downed/revive → limbo →
> reconnect, plus the flash-core/flash-admin selftests (`lifecycle`/`identity`/`queue`) —
> **and** the full character-creator path (custom adapter: hold → creator scene with live
> ped preview → save → spawn → returning character skips the creator).

### Fix: position sampler gated by lifecycle state (flash-core, live-test finding)
- `SamplePosition` now only samples while the player is actually in the world
  (`playing`/`downed`/`dead`/`limbo`). Before, it sampled the raw join position (~0,0,0)
  while a custom adapter (character creator) held the spawn — persisting garbage that made
  the next `flashfw:spawnAt` deliver `hasPos=true` at the null island (spawn under the map).
  Exactly the class of bug the lifecycle state machine exists to prevent.
- **Null-island healing** in the spawn reply: a "saved" position at ~(0,0) is always an
  artifact of the ungated sampler (no legitimate position sits there in the sea) and is
  treated as "never saved" — existing poisoned rows heal without a DB wipe.

### Charcreator template hardening (live-test findings)
- NUI callback host now uses `GetParentResourceName()` (correct casing) instead of
  `location.hostname` (can be lowercased → callbacks silently unrouted for mixed-case
  resource names like `MyCharcreator`).
- New characters are placed at a fixed, streamed-in **creation spot** before the creator
  opens (with a custom adapter nothing has spawned the ped yet — the camera stared into
  unstreamed nowhere).
- "Enter world" is double-click-guarded (one spawn per confirm), and a dev command
  `cc_reset` wipes the caller's saved look (console: `cc_reset <cid>`) so the creator can
  be re-tested without touching the DB.

## Compatibility matrix

| Flash release | Flash.Sdk (NuGet) | Core contract | FXServer artifact (Windows) |
|---|---|---|---|
| 0.8.0 | 0.8.0 | v15 | **31689** (`06d4d348c`) |
| 0.7.1 | 0.7.1 | v15 | **31689** (`06d4d348c`) |
| 0.7.0 | 0.7.0 | **v15** | **31689** (`06d4d348c`) |
| 0.6.0 | 0.6.0 | v13 | **31689** (`06d4d348c`) |
| 0.5.1 | 0.5.1 | v12 | **31689** (`06d4d348c`) |
| 0.5.0 | 0.5.0 | v12 | **31689** (`06d4d348c`) |
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

## [0.8.0] — 2026-07-10

The **player lifecycle** release. Finishes the spawn decoupling started in 0.7.1: flash-core now spawns players **entirely
on its own**, with zero dependency on any other resource. **Managed-only** (client Lua +
comments): no native change, core contract stays **v15**, artifact pin (**31689**)
unchanged. Existing servers need no config; the new default reproduces a working spawn out
of the box without `spawnmanager` *or* `basic-gamemode`.

### Self-contained native spawn is the default (flash-core)
- New default adapter `spawn_native.lua`: a **fully self-contained** spawn built on pure GTA
  natives (fade → collision request → `NetworkResurrectLocalPlayer` → place/heading → wait
  for collision → hand back control → `playerSpawned`). No `exports.spawnmanager`, and it no
  longer relies on `basic-gamemode` for spawn points — flash-core carries its own default
  point. `ensure flash-core` alone now puts a player in the world.
- Default spawn point (fresh character + auto-respawn) is Legion Square, convar-tunable:
  `flash_spawn_x/y/z/h`, optional `flash_spawn_model`, and `flash_spawn_autorespawn` /
  `flash_spawn_respawn_delay` for the parity auto-respawn-on-death behaviour (all `setr`,
  read client-side).
- Adapter selection via `setr flash_spawn_adapter`: `"native"` (default) · `"spawnmanager"`
  (the legacy bridge, now **opt-in** — still shipped as `spawn_spawnmanager.lua`) ·
  `"custom"`/`"none"` (handle `flashfw:spawnAt` yourself). The neutral contract is unchanged.

### New: character-creator template (Flash.Templates)
- `dotnet new flash-charcreator` scaffolds a full **custom spawn adapter** that shows the
  `"custom"` case end-to-end: hide the player on join, edit appearance in a self-contained
  NUI, persist it per character id (`cid`) over flash-core's RPC bridge, then drive
  `flashfw:requestSpawn` ⇄ `flashfw:spawnAt`. Reference for anyone building character select /
  custom spawn UIs. Lives in `templates/charcreator-resource/`.

### Player lifecycle — state machine skeleton (flash-core, Slice 1)
- First slice of the [player-lifecycle design](docs/player-lifecycle.md): a server-authoritative
  **state machine** (`Lifecycle.cs`) tracking each player as `connecting → spawning → playing →
  dead → dropped` (full state vocabulary defined; later slices wire the remaining transitions).
- Neutral, no hard dependency: every transition emits `flashfw:stateChanged(netId, old, new)`
  and mirrors a snapshot into the player state bag `player.State["flash:lc"]` (`{ state, cid,
  alive }`). New exports `getState(netId)` and `getSession(netId)` (rich session snapshot).
- Managed-only: no native change, core contract stays **v15**. Covered headless by the
  flash-core selftest (`lifecycle=True`).

### Player lifecycle — `playerReady` + apply pipeline (flash-core, Slice 2)
- New canonical **`flashfw:playerReady(netId, cid)`** — fires when the player is *fully in the
  world*, i.e. after the ped is placed **and** appearance/loadout were applied. Distinct from
  the existing `flashfw:playerLoaded` (server data loaded). Modules that touch the ped should
  wait for `playerReady`. Also fired on `restart flash-core` for already-spawned players.
- **Apply pipeline** (client, adapter-independent, in `client.lua`): appearance/clothing/loadout
  resources `registerApplier('tag')` once and `markApplied('tag')` per spawn while listening to
  `flashfw:applyLook(cid)`. The active adapter calls `awaitApply()` after placing the ped (still
  faded out); flash-core waits for all registered tags (5 s timeout) then fades in and acks.
- The `playing` transition now hangs on the real client ack (`flashfw:spawnComplete`) instead of
  the Slice-1 approximation — idempotent + forgery-safe (only `spawning → playing` fires ready).
  The built-in adapter and the charcreator template both drive it, so `playerReady` fires
  identically regardless of spawn adapter. Selftest extended (`lifecycle=True`).

### Player lifecycle — limbo + spawn primitives (flash-core, Slice 3)
- **Client exports** (adapter-independent, in `client.lua`): `spawn(x, y, z, heading)` — the
  single canonical placement (fade, collision streaming, resurrect, apply pipeline, fade-in,
  `playerSpawned`), now shared by `spawn_native.lua` *and* offered to custom adapters;
  `freezeInLimbo(enable)` — the "hidden + frozen + faded" hold state in one call;
  `orbitCam(opts)` / `stopCam(cam)` — the creation/intro camera helper.
- **Spawn holds** (server): `holdSpawn(netId, reason) → token` / `releaseSpawn(netId, token)`.
  A creator/intro/multichar resource claims the pre-spawn *per player at runtime* — flash-core
  defers the `flashfw:spawnAt` answer while any hold is outstanding and delivers it when the
  **last** token is released. No `flash_spawn_adapter` reconfiguration needed for the common case.
- **Limbo as a real state** (server): `enterLimbo(netId, mode)` / `exitLimbo(netId)` with
  `flashfw:limboEnter/limboExit` events — spectate/noclip/cutscene share one clean enter/exit
  (only reachable from `playing`, idempotent-safe). The client act stays with the caller.
- Charcreator template rebased onto the primitives as living proof: hand-rolled hide/freeze,
  camera and placement blocks replaced by `freezeInLimbo` / `orbitCam` / `spawn` (~50 lines
  less). Selftest extended: hold bookkeeping + limbo gate (`lifecycle=True`).

### Player lifecycle — spawn points + death metadata (flash-core, Slice 4)
- **Named spawn points** (`SpawnPoints.cs`): `registerSpawnPoint(name, x, y, z, heading[, job])`
  / `unregisterSpawnPoint` / `listSpawnPoints`, used via `spawnAtPoint(netId, name)` (job-gated)
  or `spawnPlayer(netId, x, y, z, h)`. Server-commanded spawns: callers pick a *name*, never
  raw client coordinates — closes the "spawn event as free teleport" lane. Both auto-revive a
  dead player and use the `respawning` state; `flashfw:playerRespawned(netId, cid, point)` fires.
- **`flashfw:preSpawn(netId, cid)`** fires before the `spawnAt` answer — a synchronous handler
  can claim the spawn via `holdSpawn` (the Slice-3 hold check runs right after).
- **Death metadata** (`Death.cs`): `flashfw:playerDied` carries a third argument
  `{ killer, weapon, cause, x, y, z }`. killer(netId)+weapon come from the death report —
  `setDead(netId, dead, killer, weapon)`, filled by the gameplay death-wire from the engine's
  own attribution (`GetPedSourceOfDeath`/`GetPedCauseOfDeath`); flash-core **classifies** cause
  = player/suicide/npc/environment/unknown. Existing `(netId, cid)` handlers keep working — the
  extra argument is additive. *(The initial `weaponDamageEvent` parse was replaced after the live
  test proved `willKill` is always false on the fatal hit and hit-entity resolution unreliable.)*
- Selftest extended: point validation (job gate/unknown) + death-cause attribution and
  freshness (`lifecycle=True`).

### Player lifecycle — identity graph & ban-evasion resistance (flash-admin, Slice 5a)
- **Persistent identity graph** (`Identity.cs`): every allowed connect records the player's
  identifier keys — which kinds is the **operator's choice** (`flash_identity_collect`,
  data-minimal default `token,license,fivem`; `off` disables the engine; `steam`/`discord`/`ip`
  are opt-in). Retention via `flash_identity_retention_days`, erasure via the `purgeIdentity`
  export — what a server stores about its players is the server operator's decision and
  responsibility.
- **Cluster resolution**: identities sharing ≥ `flash_identity_shared_threshold` hardware
  tokens link (family-PC protection); the same fivem/steam/discord account links with one;
  **ip never links** (CGNAT). A cluster is the person, not one id.
- **Cluster bans amplify the existing gate**: `banIdentity` writes ordinary rows into the
  existing `bans` + `ban_hardware_tokens` tables for every member (no second ban system) and
  kicks online members. The connect gate additionally rejects when *any* cluster member is
  actively banned — catching multi-hop evasion beyond the #52 exact-token match.
- **Owner enrichment hook**: `flash_identity_enrich "resource:export"` — flash-admin calls it
  with `(name, license, keys)`; a returned string rejects with that reason. Fail-open, keys
  (Steam API etc.) stay with the owner — same pattern as `flash_anti_vpn_api`.
- Exports `identityCluster` / `linkedIdentifiers` / `banIdentity` / `purgeIdentity` (targets
  resolve accountId → netId → license, like the `offline*` commands) + console/superadmin
  command `identity <target>` for cluster audits. Selftest: link rule (threshold/ip), graph
  round-trip, cluster ban, purge (`identity=True`). The **queue** (phase 1) remains open.

### Player lifecycle — join queue + downed/bleed-out + respawn policy (Slice 5b / phase 6)
- **Join queue** (flash-admin `Queue.cs`): when the server is full (`set flash_queue_slots 32`;
  0 = off), connects are held in their deferral with a live position card. Order: priority
  first (`queue_priority` table, `setQueuePriority` export, target resolution like `offline*`),
  FIFO within the same priority. Grants reserve the slot until `playerJoining` confirms (or a
  60 s reaper frees a crashed loader) — two queued players can't race one slot. Runs **after**
  all gates (a banned player never waits just to be rejected). Bookkeeping is selftest-covered
  (`queue=True`); the deferral hold loop itself is client-verification-pending.
- **Downed / bleed-out** (flash-core): `setDowned(netId, bool)` / `isDowned` / `revive`
  (clears dead *and* downed) with the `downed` lifecycle state, `flashfw:playerDowned` /
  `flashfw:playerRevived` events and the replicated `downed` state-bag key. Death supersedes
  downed; a dead player can't be downed. Optional bleed-out: `set flash_downed_bleedout_s 300`
  auto-kills a downed player after the window (a revive in time wins). Transient — a relog
  while downed falls under the existing combat-log guard.
- **Server respawn policy** (flash-core): `set flash_respawn_point "hospital"` +
  `flash_respawn_delay_s` auto-respawns dead players at a named spawn point, server-side.
  Using it? Disable the client auto-respawn (`setr flash_spawn_autorespawn "false"`) — one
  respawner is enough. Selftest extended (downed gate, `lifecycle=True`).

## [0.7.1] — 2026-07-07

Decoupling release: standard FXServer resources that flash-core baked in are now handled
like everything else in the engine — **the core gives the base, the engine user does the
act** (same philosophy as the inventory system). **Managed-only**: no native change, the
core contract stays **v15** and the FXServer artifact pin (**31689**) is unchanged; a v15
core still runs. Existing servers need no config — the shipped defaults reproduce the old
behaviour exactly.

### Swappable spawn adapter (flash-core)
- flash-core no longer performs the spawn itself or hard-`dependency`s `spawnmanager`. It
  now owns only the server-authoritative **position persistence** + a **neutral spawn
  contract** (`flashfw:requestSpawn` ⇄ `flashfw:spawnAt`). The spawnmanager glue moved into
  a separate, optional adapter (`spawn_spawnmanager.lua`) that is the shipped default *and*
  the reference implementation.
- Write your own spawn (character select, custom UI, or no spawnmanager) without forking:
  `setr flash_spawn_adapter "custom"` disables the default adapter and you handle
  `flashfw:spawnAt` yourself. `spawnmanager` is now a **soft** dependency — the adapter waits
  briefly for it and disables cleanly with a log line if it's absent, instead of blocking
  startup.

### Overridable command reply sink (Flash.Sdk)
- New `Commands.SetReplySink((netId, message) => …)` / `Commands.ReplyTo(...)`: route command
  replies to ox_lib / a custom NUI chat instead of the standard `chat:addMessage`. Default
  (unset) is byte-identical to before. Process-global (the SDK is one shared instance),
  auto-cleared when the setting resource stops so its delegate can't dangle past unload.
  `flash-core` and `flash-admin` now emit their player replies through it, so a single
  server-side override captures those too.

## [0.7.0] — 2026-07-07

Inventory feature & hardening release closing the #213–#245 batch (30 implemented, 2
rejected as not-applicable/redundant). **This is a PAYLOAD release, not managed-only**: the
core contract grows **v13 → v15** (append-only), so the payload ships a rebuilt
`citizen-scripting-flash.dll` + core; a v15 SDK requires the v15 core. The FXServer artifact
pin (**31689**) is unchanged. Every item is covered by a `flash-core` selftest and is green
in the real release FXServer; the server-side distance gate is additionally confirmed in-game
(near → allowed, 100 m → rejected).

### Security & reliability (core contract v14)
- **Item registry & native hash filter** (#242/#245): once a server registers its item
  catalog (`items.json`), the core rejects any unregistered item hash *before* allocating —
  killing cheat-spawned items and hash-spam memory exhaustion at the native boundary. Opt-in:
  without a catalog the previous behaviour is unchanged.
- **Atomic weight/slot limits** (#213) enforced under the same lock as the mutation (no
  check-then-act race), plus **busy/locked container flags** (#240/#224) with a 30 s crash
  failsafe.
- **Persistence hardening** (#240/#244): per-container serialization + a busy flag around
  every load/save closes the item-loss window in the async gap; each save runs in one SQL
  transaction.
- **Quantity sanity cap** (#243): a negative int cast to `uint` is rejected instead of
  minting ~4 billion items. Rejections now surface a reason (`InvResult`); the classic
  bool/void APIs stay source-compatible.

### Inventory features (core contract v15)
- **Atomic batch primitive** (#233): one all-or-nothing takes+gives transaction — the
  foundation for **crafting** (#223), **atomic trades** (#225) and the `InventoryTransaction`
  unit-of-work scope.
- **Unique item instances + metadata** (#214): weapons/keys/bags as server-generated u64
  instances (the id doubles as the serial), with an `inventory_instances` table; the core
  owns the atomic location truth, metadata lives in C#/DB.
- **Category filters** (#218), **change events** (#220), and **nested containers** (#216).

### Gameplay & security layer (flash-core)
- **Container access pipeline** (#224/#238/#239): session → rate-limit → distance → ACL/keys
  for every player-driven interaction, blocking remote-looting and macro-spam; violations
  raise `flashfw:invViolation` (flag-and-log, no auto-ban).
- **Audit logging** (#227), **loot tables** (#228), **ground drops** in a memory-only band
  (#221/#219), **duplication alerts** (#241), and opt-in **state-bag sync** (#215).
- **Lazy item decay** (#222) for unique items, **container sessions** (#236, server half),
  and DX helpers: rejection messages (#235), snapshot queries (#231), a fluent container
  builder (#230), category `[ItemHandler]`s with metadata injection (#232), and a
  `/dumpitems` constants generator (#234).

Not applicable: #226 (no slot/stack model to consolidate), #229 (a split is already the
atomic `move`).

## [0.6.0] — 2026-07-06

Large feature release closing the actionable server-side backlog: player identity, gameplay
vitals, QoL lifecycle, the security/anti-cheat suite, the vehicle garage, and the
Zig-backed transactional inventory — plus a full pre-release review pass (#194–211).
**This is a PAYLOAD release, not managed-only**: the transactional inventory (#46) grows the
native core contract to **v13**, so the payload ships a rebuilt `citizen-scripting-flash.dll`
+ core; a v13 SDK requires the v13 core. The FXServer artifact pin (**31689**) is unchanged.
Every item is covered by a `sample-resource` / `flash-core` / `flash-admin` selftest and is
green in the real release FXServer.

**Pre-release review fixes (#194–211)** — a security/reliability review of the batches above,
all fixed and re-verified in the real release FXServer:
- Anti-cheat: weaponDamageEvent payload is `args[1]` not `args[0]` (the check was fully
  bypassed); environmental/heavy damage exclusions (no more false kicks on falls/cars/
  explosions); server-side `GetEntityVelocity` replaces the client-only `IsPedFalling`. (#194/#195/#196)
- VirtualInstance routes a member's brought-in vehicle back out instead of stranding it. (#197)
- Garage: driver-seat + ownership checks on store (no passenger griefing / plate-collision
  overwrite); `stored` flag reset on startup (no post-restart retrieval deadlock). (#199/#200)
- flash-admin: offline commands resolve the persistent Account ID before an online NetID
  (no target hijacking); anti-VPN strips the port from the endpoint. (#204/#205)
- Inventory: snapshot no longer truncates large containers (was silent data loss); reconnect
  no longer wipes the reloaded inventory; inventories persist on auto-save + shutdown. (#208/#210/#211)
- Script-thread assertions on native-touching APIs (Audit.Log(ServerPlayer), Sessions, Http,
  StateBag/GlobalState); Items registry locking; audit sinks marshalled to the script thread. (#198/#201/#202/#203/#206/#209)

**Core / anti-dupe**
- Transactional inventory backed by the native Zig core: item counts live in unmanaged
  memory and every check-and-decrement is atomic under a lock, so "take more than exists" is
  impossible even under real cross-thread concurrency (the spam-click dupe). `Flash.Inventory`
  (give/take/move/count/snapshot, safe from any thread); flash-core `inventories` persistence
  (auto load-on-join / save-on-drop by character id) + exports. Proven in-server by 800
  concurrent off-thread moves on 200 items resolving to exactly 200 winners. Core contract
  v12 → v13; new public `StateBag.ForEntity`/`ForPlayer` SDK accessors. (#46)

**Player identity**
- `ServerPlayer.SessionKey` + `Players.GetBySession`: cryptographically random per-connection
  token that never survives a reconnect — key async state and NUI request auth on it instead
  of the recyclable NetID. Owner-validated on every read; invalidated at the host's global
  playerDropped choke point. Security rules documented in `docs/api.md`. (#183)
- `ServerPlayer.AccountId` + `Players.GetByAccountId`: the persistent account ID replicated
  by flash-core (state-bag `"id"`) as a first-class SDK property; command-router
  `ServerPlayer` arguments resolve connected netId first, then account ID. flash-admin gains
  offline moderation (`offlineban`, `offlinesetlevel`, unban by account ID). (#174)

**DX**
- Automatic chat autocomplete for `[Command]`-routed commands: `[Command].Description` /
  `[Description]` on methods and parameters feed `chat:addSuggestion`; late joiners get them
  on `chat:init`, resource stop retracts them. (#156)
- `[ItemHandler]` item action registry: gameplay resources declare handlers, any inventory
  dispatches with `Items.Use(netId, item, ...)` across resources; typed binding, async
  support, per-resource cleanup. (#159)

**Observability**
- `Flash.Audit`: structured, non-blocking audit trail — in-memory queue + background writer,
  daily JSON-Lines files under `flash-audit/`, per-resource custom sinks, overflow guard.
  Never touches the game database, never blocks the script thread. (#144)

**Gameplay (flash-core)**
- Server-authoritative vehicle garage: owned vehicles keyed by character id, state
  serialized to a JSON blob in a `vehicles` table, spawned via `CreateVehicleServerSetter`
  with server-side state applied before networking (model/plate/colours/body health/dirt/
  locks un-spoofable; fuel/engine health/livery/tint/mods stored server-side and
  replicated via the `flashVehState` entity state bag for a client applier). Ownership +
  shared keys (`vehicle_keys`) gate retrieve/engine. Exports `garageStore`/`garageRetrieve`
  /`garageList`/`garageRegister`/`garageGiveKey`/`garageHasAccess`. New public
  `StateBag.ForEntity`/`ForPlayer` SDK accessors. (#62)
- Server-authoritative vitals: hunger/thirst decay purely server-side (configurable via
  convars), replicate as state bags for HUDs, fire `flashfw:vitalThreshold` on crossing
  25/10/0, persist with the character (schema migration included). Exports:
  `getVital`/`setVital`/`addVital`/`isDead`/`setDead`. (#158)
- Combat-log guard: disconnecting with critical ped health marks the character dead
  (persisted + replicated + `flashfw:combatLog` event + audit entry) — reconnecting fully
  healed is over. (#140)

**Instancing**
- `VirtualInstance`: lifecycle-managed world instances — allocated routing bucket (strict
  lockdown), tracked server-side entity spawns (vehicle/ped/prop), member routing incl.
  current vehicle, replicated `"instance"` state key as the voice/HUD contract; `Dispose`
  restores players, deletes all tracked entities and frees the bucket. (#126)

**Security (anti-cheat)**
- Server-authoritative integrity layer in flash-core (all opt-in, `off`/`log`/`block`/`kick`):
  entity mass-spawn rate limiting, implausible weapon-damage detection and on-foot
  speed/noclip validation, with a `Flash.Security` exception API
  (`AddWeaponException`/`BypassEntityLimits`) and an ACE bypass for admins. The decision
  logic is covered by the flash-core selftest; the live event hooks are wired but require
  a client to exercise end-to-end. (#54, #55)

**Security (flash-admin)**
- Hardware-token ban hardening: bans now record the client's HWID tokens
  (`ban_hardware_tokens`) and the connect gate rejects any matching token even with a
  clean license — ban evasion from the same machine fails. Unban clears the tokens. (#52)
- Optional identity gates in the connect deferral (all default off): require
  Discord/Steam/Cfx.re identifiers and an async anti-VPN/proxy HTTP check
  (`flash_require_discord`/`_steam`/`_cfx`, `flash_anti_vpn_api`). (#53)

**QoL / Lifecycle**
- `[PreserveState]`: flagged static fields/properties survive resource restarts — the host
  JSON-serializes them before the ALC unload and injects them back before `OnStart()` of
  the reloaded instance (reference-free by design, so preserved state can never pin the
  old ALC). (#117)
- Vehicle reconnect (flash-core): crashing inside a vehicle reserves (vehicle, seat) in
  RAM per license; reconnecting within `flash_reconnect_vehicle_minutes` warps the player
  back into their exact seat if the vehicle survived — otherwise the normal saved-position
  spawn applies. (#107)

## [0.5.1] — 2026-07-06

Bugfix release: eight verified findings from the post-0.5.0 audits (#175–#182), all
confirmed against the source and fixed. **Not managed-only**: the native core and the
scripting component changed too (grid leak, invoke bounds check, runtime-handle reset) —
the payload ships a rebuilt `citizen-scripting-flash.dll` + core; the core contract (v12)
and the FXServer artifact pin (31689) are unchanged. Everything verified in the real
release FXServer (full `flash-core`/`flash-admin` selftest suites green).

**SDK (Flash.Sdk)**
- `StateWatchers.RegisterAll` fails fast on `[FromSource]` parameters with unsupported
  types (same rule as the event router, #171), and the dispatch catches reflection-layer
  exceptions — a bad watcher signature can no longer crash the script thread. (#177)
- StateWatcher registries (`s_cookies`/`s_lastValues`/`s_dropWired`) are serialized under
  a lock against off-thread change-callback races — same pattern as `State.cs` (#148). (#175)
- `Exports.Register`/`Exports.Call` assert the script thread and fail fast off-thread
  (consistent with `Rpc.Client` #169 and the DB layer #89). (#178)
- `RegisterAll(typeof(X))` now works for static/utility classes across all four attribute
  routers (Commands/Events/Exports/StateWatchers) instead of silently registering nothing;
  annotated instance methods without an instance are rejected at registration. (#180)

**Native core / component**
- Spatial grid: empty cells are fully torn down on removal (list freed + map entry
  removed) — fixes the unbounded empty-cell accumulation on long-running servers. (#179)
- `coreInvokeObject` rejects argument counts outside 0..32 up front instead of passing
  the original count past the 32-slot stack buffer (out-of-bounds stack read). (#181)
- `FlashRuntime::Destroy()` resets the active runtime handle to null so the Zig core is
  never left pointing at a freed runtime (use-after-free). (#182)

**Tooling (Flash.LuaDefGen)**
- `ValueTask`/`ValueTask<T>` returns unwrap like `Task`/`Task<T>` instead of mapping to
  `table`; generic methods get correct compiler doc-IDs (arity suffix + backtick parameter
  refs) so overloaded generic exports keep their XML docs; the resolver skips the runtime
  dir cleanly when `Assembly.Location` is empty (single-file publish). (#176)

## [0.5.0] — 2026-07-05

Feature release: the `priority: high` triage backlog plus the DX attribute-routing family
(`[EventHandler]`/`[StateWatcher]`/`[FlashExport]` joining the existing `[Command]`).
**Managed-only**: additions to `Flash.Sdk` and the private host — the native core, the core
contract (v12) and the FXServer artifact pin (31689) are unchanged, so a newer SDK on the 0.4.x
payload is fine. Every item builds clean and is covered by a `sample-resource` self-test. Bugs
found in the new code during review (#162–#172) were verified and fixed before release (class-level
authorization bypass, event-router authorization gating server/core events, off-thread `HttpClient`
and RPC native crashes, the DB mapper's NULL handling, event-router default binding, and more).

**Database**
- Dapper-style typed row mapping: `Db.Query<T>` / `Db.QueryAsync<T>` / `Db.QuerySingleAsync<T>`.
  A cached reflection mapper binds columns to public properties case- and underscore-insensitively
  (`vip_level` → `VipLevel`) and coerces values through the shared numeric-safe path
  (MySQL `bigint`/`double`/`decimal` → `int`/`float`/…); NULL columns leave the property default. (#115)

**DX**
- Reactive state-bag bindings: `[StateWatcher("key")]` + `StateWatchers.RegisterAll(this)`. A
  watcher fires on state-bag changes; a `[FromSource]` parameter binds the bag owner
  (`ServerPlayer` / netId / raw bag name) and the remaining parameters bind to (newValue, oldValue)
  with numeric-safe coercion. One native handler per watched key (zero overhead for unwatched keys);
  old values are cached with leak-free cleanup on unload and on player disconnect. (#120)
- Declarative exports: `[FlashExport("name")]` + `Exports.RegisterAll(this)` (mirrors the
  command/event routers). Incoming args bind to the method's typed parameters (numeric-safe
  coercion, declared defaults) and the return value is the export result. (#173)
- Auto-generated EmmyLua definitions for C# exports: every resource build now emits
  `<resource>.d.lua` next to the DLL — generated by the new `Flash.LuaDefGen` tool from the
  `[FlashExport]` methods (types mapped C#→EmmyLua, hover docs pulled from the C# XML
  comments), so Lua scripts calling `exports.<resource>:...` get autocomplete, type checking
  and documentation in VS Code (sumneko Lua Language Server). Ships inside the Flash.Sdk
  NuGet (`buildTransitive` targets + `tools/luadefgen`); opt out per project with
  `<FlashGenerateLuaDefs>false</FlashGenerateLuaDefs>`. (#173)
- Attribute-based event routing: `[EventHandler("name")]` + `[FromSource]`, registered with
  `Events.RegisterAll(this)` (mirrors the command router). Event args are deserialized into the
  method's typed parameters (numeric-safe coercion), `[FromSource]` injects the caller
  (netId / source / `ServerPlayer`), async handlers are awaited safely, and the declarative
  authorization attributes are enforced on events too. SDK slice of the #111 DX epic. (#111)
- Standard `HttpClient` support via `FlashHttpMessageHandler`. `new HttpClient(new FlashHttpMessageHandler())`
  routes requests through the native `Flash.Http` client, so `await` continuations resume on the
  script thread (natives are safe after the await) and reuse FXServer's socket pool — unlocks Refit,
  Discord.Net and `System.Net.Http.Json`. (#116)
- `StateBag.Get<T>` / `GlobalState.Get<T>` now coerce wire-widened numerics. A value set as `int`
  that returns over the msgpack/replication boundary as `long`/`double` is coerced to the requested
  `int`/`float`/nullable primitive instead of yielding `default`; coercion is centralized in a new
  non-generic `Args.ToType(value, type)` (shared with the typed DB mapping). (#155)

**Lifecycle**
- Optional `IAsyncStoppable.OnStopAsync(CancellationToken)`. The host awaits async cleanup (e.g. a
  database flush) within a bounded, scheduler-pumped grace window (convar `flash_stop_grace_ms`,
  default 5000) *before* the synchronous `OnStop` and ALC teardown — closing the data-loss trap of
  firing async saves from `OnStop`. Non-breaking opt-in interface (same pattern as `ITickable`). (#40)

**Security**
- Server-side sanitization helpers (anti-XSS on user-generated content): new `Flash.Security`
  (`HtmlEncode`, allow-list `IsMatch` with a ReDoS match-timeout, `HasMarkup`) and
  `Args.StrRegex(args, i, maxLength, pattern, def)` for validated boundary input. Partial for #65
  (the optional encode-on-replicate wrapper and a global pipeline middleware remain). (#65)
- Declarative authorization: `[AuthorizeFaction]` (OR-combined, `MinGrade`, `MustBeOnDuty`) and
  `[AuthorizeAdmin]` (`RequiredPermission`), plus a framework-agnostic, **fail-closed**
  `Authorization` gateway with pluggable `FactionResolver`/`AdminResolver`, enforced in both the
  command router and the attribute event router before binding/execution (server-originated calls
  trusted). SDK side of #131; the `flash-core`/`flash-admin` resolver wiring is still pending. (#131)

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
