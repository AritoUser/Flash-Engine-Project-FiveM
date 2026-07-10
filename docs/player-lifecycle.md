# Player Lifecycle (design)

Status: **implemented & live-verified** (all five slices + phase-6 extras, see §10) — driven
end-to-end by a real client on the local FXServer on 2026-07-10. This document defines what Flash should make
*first-class* about the whole path a player takes — from the socket connecting to the
disconnect — and draws the exact line between what the **engine owns** and what the
**server owner builds**.

The spawn system shipped today (`flashfw:requestSpawn` ⇄ `flashfw:spawnAt`, see
[cookbook](cookbook.md#spawn-at-the-last-saved-position)) is *one transition* in this
lifecycle. This proposal generalises it into an observable, server-authoritative
**state machine** with a rich **session object**, so that character creators, multichar,
intro scenes, hospital respawn, spectate, queues and ban-evasion resistance all compose
without forking flash-core.

---

## 1. Design principles

1. **Neutral contract first.** Every phase is driven by plain `flashfw:` events + state
   bags + exports on the normal bus. Any resource can observe or participate **without a
   hard dependency** on flash-core — exactly like today's spawn contract. Typed C# sugar
   in `Flash.Sdk` is optional convenience layered on top, never a requirement.
2. **Managed-only.** Nothing here needs a new native/Zig function. The core contract stays
   **v15** and the FXServer artifact pin (**31689**) is unchanged. Changes are C# (flash-core
   / flash-admin) + client Lua + optional SDK, i.e. a `dotnet build` + file copy — no payload
   rebuild, no artifact rebind.
3. **Base, not blackbox.** The engine owns the *hard, reusable, correctness-sensitive*
   plumbing (state machine, session, spawn act, identity graph). Opinionated **policy** and
   **UI** (which spawn point, respawn rules, whitelist screen, character-select UI) stay in
   templates/hooks the owner controls.
4. **Operator owns the data.** For identity/logging (phase 1), Flash provides a configurable
   collection mechanism, data-minimal defaults, and a purge export. **What is collected and
   how long it is retained is the server operator's choice and responsibility** — Flash makes
   no legal guarantee and stores nothing unless the operator opts in.

---

## 2. The state machine

```
socket
  │
  ▼
Connecting ──▶ Authorizing ──▶ Queued ──▶ Selecting ──▶ Creating ──▶ Spawning ──▶ Playing
   (deferral)   (identity)     (queue)    (multichar)    (creator)    (spawn act)     │
      │             │             │            │             │                        │
      └── reject ───┴─────────────┴── holdSpawn releases ────┘                        │
                                                                                      │
                          Limbo ◀────── (spectate / noclip / cutscene) ──────────────▶│
                            ▲                                                          │
                            │                                                          ▼
                       Respawning ◀── Respawn policy ◀── Dead ◀── Downed ◀── (damage) ─┤
                            │                                                          │
                            └──────────────────▶ Playing                              │
                                                                                      ▼
                                            Dropped ◀───────────── (disconnect, any state)
                                              │
                                              ▼
                                         Reconnecting ──▶ (resume session within window)
```

### States

| State | Meaning | Ped exists? | Controllable? |
|---|---|---|---|
| `connecting` | In the FiveM deferral (identity not yet resolved). | no | — |
| `authorizing` | Identity resolved; ban/whitelist/enrichment being decided. | no | — |
| `queued` | Accepted but waiting for a slot. | no | — |
| `selecting` | In session, choosing a character (multichar). | hidden | frozen |
| `creating` | In a character creator. | visible (creator cam) | frozen |
| `spawning` | Placement in progress (collision streaming). | placing | frozen |
| `playing` | Fully in the world, `ready` fired. | yes | yes |
| `downed` | Incapacitated, not yet dead (bleed-out window). | yes | limited |
| `dead` | Dead, awaiting respawn. | dead ped | no |
| `respawning` | Respawn placement in progress. | placing | frozen |
| `limbo` | Spectate / noclip / cutscene — a real, reusable state. | hidden/detached | scripted |
| `dropped` | Disconnected; session saved, kept for the reconnect window. | no | — |

Every transition emits `flashfw:stateChanged(netId, oldState, newState)` server-side and
mirrors the state into the player's state bag (see §4), so clients and other resources react
without polling.

---

## 3. The session object

Server-side `PlayerSession` (flash-core already has a lean version — accounts/character/
position/vitals) is extended to carry the whole lifecycle. It is the single source of truth.

```
PlayerSession (server, in-memory; persisted parts noted)
  netId            int      session slot (1..64, reused — NEVER a persistence key)
  accountId        int      stable account id           (persisted: accounts)
  cid              int      character id                (persisted: characters)
  license          string   Rockstar license

  state            enum     see §2
  stateSince       long     ms timestamp of last transition
  connectedAt      long
  spawnedAt        long
  readyAt          long

  posX/Y/Z/heading float    last sampled position        (persisted: characters)
  hasPos           bool

  alive            bool
  downed           bool
  death            { killer:int?, weapon:string?, cause:string, at:long, x,y,z }?

  holdTokens       string[] resources currently holding the spawn (see §5.3)
  spawnPoint       string?  named point used for the last spawn
  identityCluster  long?    link into the identity graph (§9)
```

Client mirror (read-only, replicated) in the player state bag:

```
player.State["flash:lc"] = { state = "playing", cid = 42, ready = true, alive = true }
```

Access:

```lua
-- any resource, no hard dependency:
local lc = Player(source).state["flash:lc"]         -- server
local lc = LocalPlayer.state["flash:lc"]            -- client
```

```csharp
// flash-core exports (typed):
var snap = Exports.Call<Dictionary<string, object?>>("flash-core", "getSession", netId);
string state = Exports.Call<string>("flash-core", "getState", netId);
```

---

## 4. Event & export catalog

Naming follows the existing conventions: events in the `flashfw:` namespace, exports in
`camelCase` (like `getPosition`, `garageStore`, `invGive`), convars as
`flash_<subsystem>_<param>` (like `flash_spawn_*`, `flash_ac_*`).

### Phase 1 — Connecting / Authorizing / Queued  (owner: **flash-admin**)

flash-admin already owns the connect gate (bans, whitelist, deferrals, VPN API). This phase
extends it. See §9 for the identity/anti-evasion engine that lives here.

| Kind | Signature | Notes |
|---|---|---|
| event (deferral) | `Events.OnPlayerConnecting` | existing SDK entry point |
| event | `flashfw:authorizing(netId, keys)` | identity resolved; keys = collected identifiers |
| hook | `flashfw:enrichIdentity(netId, keys) -> verdict` | owner's Steam/Discord/age check (allow/reject/score) |
| event | `flashfw:queued(netId, position)` / `flashfw:queueAdvanced(netId, position)` | |
| export | `queuePosition(netId) -> int` | 0 = not queued |
| export | `setPriority(license, priority)` | reserved-slot / priority queue |
| convar | `flash_queue_max` (0=off), `flash_queue_grace_ms` | queue sizing |

### Phase 2 — Selecting  (multichar handshake; owner: **flash-core**, UI: template)

Flash ships only the *handshake*. The character-select screen is a template/resource.

| Kind | Signature | Notes |
|---|---|---|
| event (S→C) | `flashfw:selectCharacter(netId, characters[])` | server offers the account's characters |
| event (C→S) | `flashfw:characterChosen(cid)` | client picks; `cid==0` ⇒ "create new" |
| event | `flashfw:characterCreated / characterDeleted / characterLoaded(cid)` | |
| export | `listCharacters(netId) -> table[]` / `createCharacter(netId)` / `deleteCharacter(cid)` | |

The session stays in `selecting` (ped hidden, frozen) until a `cid` is chosen; a single-char
server simply auto-picks slot 1 (today's behaviour) and skips straight through.

### Phase 3 — Creating  (owner: **flash-core** primitives, UI: template)

The reusable pieces a creator / intro / multichar scene all need — today every builder
reimplements them (see the shipped `charcreator` template).

| Kind | Signature | Notes |
|---|---|---|
| export (client) | `freezeInLimbo(enable)` | hide + freeze + fade — the canonical "hold the player" |
| export (client) | `orbitCam(entity, opts) -> handle` / `stopCam(handle)` | creation/intro camera helper |
| export (server) | `holdSpawn(netId, reason) -> token` | claim the spawn at runtime (see §5.3) |
| export (server) | `releaseSpawn(netId, token)` | release the claim → spawn proceeds |

`holdSpawn`/`releaseSpawn` replace the `setr flash_spawn_adapter "custom"` convar dance for the
common case: a creator/intro resource claims the spawn on join and releases it when done,
**without** the server owner having to reconfigure the adapter.

### Phase 4 — Spawning  (owner: **flash-core**)

| Kind | Signature | Notes |
|---|---|---|
| event | `flashfw:preSpawn(netId, cid)` | fires before the `spawnAt` answer; a synchronous handler may `holdSpawn` |
| contract (S→C) | `flashfw:spawnAt(x, y, z, heading, hasPos)` | existing, unchanged |
| contract (C→S) | `flashfw:requestSpawn` | existing, unchanged |
| export (client) | `spawn(x, y, z, heading)` | canonical placement (the `spawn_native.lua` reference) |
| export (server) | `registerSpawnPoint(name, x, y, z, heading[, job])` / `unregisterSpawnPoint(name)` / `listSpawnPoints()` | job = required session job ("" = open) |
| export (server) | `spawnAtPoint(netId, name)` / `spawnPlayer(netId, x,y,z,h)` | server-commanded; auto-revives a dead player, no preSpawn/hold cycle |

**Server-authoritative points.** With named points, the client picks *which point* (a name),
never raw coordinates — the server resolves and validates them, closing the "spawn-event
teleport" exploit vector (consistent with flash-core's anti-cheat stance).

### Phase 5 — Playing  (owner: **flash-core**)  ⭐

The single highest-leverage addition: a canonical "fully loaded" signal, fired **after**
appearance + loadout + state were applied — the moment every module (jobs, phone, inventory,
HUD) can safely initialise per player.

| Kind | Signature | Notes |
|---|---|---|
| event (client) | `flashfw:applyLook(cid)` | apply-pipeline entry; appearance/clothing/loadout resources hook it |
| export (client) | `registerApplier(tag)` / `markApplied(tag)` / `awaitApply()` | register a step once; signal it done per spawn; adapter awaits all before fade-in |
| event (server) | `flashfw:playerReady(netId, cid)` | **fully in the world** — distinct from the existing `flashfw:playerLoaded` (server data loaded) |

> `flashfw:playerLoaded` (exists today) = character row loaded server-side.
> `flashfw:playerReady` (new) = client spawned **and** look/loadout applied. Modules that
> touch the ped must wait for `playerReady`, not `playerLoaded`.

### Phase 6 — Downed / Dead / Respawning  (owner: **flash-core**)

Death metadata is *free*: `AntiCheat.cs` already hooks the weapon-damage events, which carry
attacker + weapon. This phase forwards that data instead of throwing it away.

| Kind | Signature | Notes |
|---|---|---|
| event | `flashfw:playerDowned(netId)` / `flashfw:playerRevived(netId, cid)` | bleed-out window; ties into vitals/`setDead` |
| event | `flashfw:playerDied(netId, cid, info)` | `info = { killer, weapon, cause, x, y, z }`. killer(netId)+weapon come from the death report (`setDead(netId, dead, killer, weapon)`); the gameplay death-wire reads the engine's own attribution (`GetPedSourceOfDeath`/`GetPedCauseOfDeath`). flash-core **classifies** cause = player/suicide/npc/environment/unknown. (The original `weaponDamageEvent` parse was dropped — live testing showed `willKill` is always false and hit-entity resolution is unreliable.) |
| event | `flashfw:playerRespawned(netId, cid, point)` | fired by `spawnAtPoint`/`spawnPlayer` when the player was dead |
| export | `revive(netId)` / `setDowned(netId, bool)` | integrates with existing `setDead`/vitals |
| convar | `flash_respawn_policy` (`point`\|`nearest`\|`spot`), `flash_respawn_delay_ms` | policy is config, not code |

The respawn *policy* (nearest hospital, fixed point, at the spot) is convar/hook-driven;
the *mechanism* (place, fade, fire events) is the shared spawn code from phase 4.

### Phase 7 — Limbo  (owner: **flash-core**)

Spectate, noclip and cutscenes become one first-class state with clean enter/exit instead of
ad-hoc scripts (the admin menu, death cam and intro all reuse it).

| Kind | Signature | Notes |
|---|---|---|
| export (server) | `enterLimbo(netId, mode)` / `exitLimbo(netId)` | `mode = "spectate" \| "noclip" \| "cutscene"` |
| event | `flashfw:limboEnter(netId, mode)` / `flashfw:limboExit(netId)` | |

### Phase 8 — Dropped / Reconnecting  (owner: **flash-core**)

| Kind | Signature | Notes |
|---|---|---|
| event | `flashfw:playerDropped(netId, reason)` | graceful save already runs today |
| behaviour | crash-vs-quit distinction; **resume session** within a window | generalises the existing vehicle-reconnect (#107) to the whole session |
| convar | `flash_reconnect_window_ms` | |

---

## 5. How the spawn hold works (holdSpawn)

`holdSpawn` is the runtime alternative to the `flash_spawn_adapter` convar. Flow:

1. On `flashfw:preSpawn`, any resource may call `holdSpawn(netId, "charcreator")` → gets a token
   and the session stays in `creating`/`selecting`.
2. flash-core will not emit `flashfw:spawnAt` while any hold token is outstanding.
3. When the creator is done it calls `releaseSpawn(netId, token)`. When the **last** token is
   released, flash-core proceeds with the spawn contract as usual.

This lets a creator/intro/multichar resource opt into "I own the pre-spawn" *per player at
runtime* without the owner editing `server.cfg`. The `custom`/`none` convar remains for
owners who want to disable the built-in adapter entirely.

---

## 6. The apply pipeline (phase 5 detail)

Between `spawned` and `ready`, appearance/clothing/loadout must be applied in a defined order
before the screen fades in. The pipeline is cooperative and awaitable:

```lua
-- an appearance resource:
AddEventHandler('flashfw:applyLook', function(cid)
    local a = fetchAppearance(cid)      -- your data
    applyAppearance(PlayerPedId(), a)
    exports['flash-core']:markApplied('appearance')  -- signal this step done
end)
```

flash-core waits (with a timeout) for all registered `markApplied` tags, then fades in and
fires `flashfw:playerReady`. Order is by registration; a resource with no look to apply simply
never registers. This is the ordering flash-core owns — the *content* (skins, loadouts) is the
resource's.

---

## 7. Layering & impact — which file changes, and how expensive

| Primitive | Layer / file | Native or managed |
|---|---|---|
| State machine, session, transitions, `stateChanged` | flash-core `Lifecycle.cs` (new; split out of the 2557-line `Main.cs`) | managed |
| Spawn act, spawn points, `preSpawn`/`spawned`, `spawn`/`spawnAtPoint` | flash-core `SpawnPoints.cs` (new) + `spawn_native.lua` / `client.lua` | managed |
| `freezeInLimbo`, `orbitCam`, `spawn` (client) | flash-core `client.lua` | managed (Lua) |
| `playerReady` + apply pipeline | flash-core `Lifecycle.cs` + `client.lua` | managed |
| Death metadata → `playerDied` | flash-core `AntiCheat.cs` (reuse existing damage hook) | managed |
| Downed/revive/respawn policy | flash-core `Lifecycle.cs` (ties into existing `setDead`/vitals) | managed |
| Limbo (spectate/noclip/cutscene) | flash-core `Lifecycle.cs` + `client.lua` | managed |
| Connect gate → queue + identity engine | flash-admin `Main.cs` + `Identity.cs` (new) | managed |
| Typed lifecycle/session helpers (optional) | `Flash.Sdk` (public, **SemVer minor**) | managed |
| DB tables (spawn points, identity graph, bans) | additive migrations (flash-core already migrates additively) | managed |

**Nothing touches the Zig core.** Core contract stays **v15**, artifact pin **31689**
unchanged. The only *versioned public* surface is the optional `Flash.Sdk` sugar (a SemVer
minor bump) — and even that is optional, because the neutral events/state-bags/exports work
without it.

---

## 8. Core vs. template boundary

**Engine owns** (reusable, correctness-sensitive):
- the state machine + session + all `flashfw:` lifecycle events,
- the spawn act, spawn-point registry + server-side validation,
- `holdSpawn`, `freezeInLimbo`, limbo states, the apply-pipeline ordering,
- the identity graph + cluster ban-match (§9).

**Server owner builds** (policy + UI; templates/hooks):
- character-select and character-creator UIs (the `charcreator` template is the reference),
- appearance/clothing/loadout **content** (keyed by `cid`),
- respawn **policy** and spawn-point **placement**,
- whitelist screen, Discord bot, Steam/age gates (via `flashfw:enrichIdentity`),
- what identity data to collect and how long to keep it (§9, §4 operator responsibility).

This keeps Flash a construction kit: the lifecycle runs standalone; flash-core is *a*
consumer of it (the shipped one), not a prerequisite.

---

## 9. Identity & ban-evasion resistance (phase 1 detail)

The hard part of banning is not "log an IP" — it is recognising the **same person** behind a
fresh account when license, Steam and Discord are all new. That is a correctness problem, so
the engine owns it; the policy and external lookups stay with the owner.

### Mechanism — the identity graph

On the connecting deferral, flash-admin collects every available **key**:

| Key kind | Source | Reliability | Default |
|---|---|---|---|
| `token` (hardware) | `GetNumPlayerTokens` / `GetPlayerToken` | present always, survives reinstall — **the anti-evasion lever** | **on** |
| `license` | identifiers | stable per Rockstar account | **on** |
| `fivem` | identifiers | Cfx.re account | on |
| `steam` | identifiers | only if Steam is running | off |
| `discord` | identifiers | only if Discord is running | off |
| `ip` | endpoint | weak (dynamic/VPN) | off |

Storage (additive tables):

```
identities     ( id, cid, first_seen, last_seen )
identity_keys  ( identity_id, kind, value )          -- one row per collected key
identity_bans  ( scope, value, reason, until, actor ) -- scope = 'cluster' | 'key'
```

A **cluster** is the connected component over identities that share keys (union-find on
`identity_keys`). Two identities linked ⇒ same person/machine. To avoid family-PC false
positives, *linking* requires ≥ `flash_identity_shared_threshold` shared tokens; a *ban match*
can be stricter (any banned key rejects immediately).

### On connect

1. Collect keys → look up matching clusters.
2. If any key (or the resolved cluster) matches an `identity_ban` that is still active →
   reject the deferral with the ban reason. A new account on the same PC shares hardware
   tokens → caught.
3. Otherwise emit `flashfw:enrichIdentity(netId, keys)` so the owner's resource can run
   Steam/Discord/age checks and veto (allow/reject/score). Flash ships the **hook**, not the
   API calls — external keys (`flash_steam_api_key`, a Discord bot token) belong to the owner
   and are configured, never bundled (same pattern as the existing `flash_anti_vpn_api`).

### Config surface

| Convar | Meaning | Default |
|---|---|---|
| `flash_identity_collect` | comma list of key kinds to store | `token,license,fivem` |
| `flash_identity_shared_threshold` | shared tokens needed to *link* two identities | `2` |
| `flash_identity_ban_scope` | `cluster` (ban the person) or `key` (ban one id) | `cluster` |
| `flash_identity_retention_days` | prune keys older than N days (0 = keep) | `0` |
| `flash_steam_api_key`, `flash_anti_vpn_api` | owner-provided enrichment keys | unset |

### Exports (flash-admin)

| Export | Purpose |
|---|---|
| `identityCluster(netId) -> clusterId` | resolve the player's cluster |
| `linkedIdentifiers(clusterId) -> table[]` | audit: every key in the cluster |
| `banIdentity(target, reason, until)` | ban a netId (→ its cluster) or a specific key |
| `purgeIdentity(target)` | **delete all identity data** for a cid/cluster |

### Operator responsibility (explicit)

Flash provides the **mechanism**: configurable collection, data-minimal defaults (hardware
tokens + license + Cfx.re only), retention pruning, and `purgeIdentity` for erasure requests.
**What a server stores about its players, and for how long, is the server operator's decision
and legal responsibility.** Flash collects nothing beyond the defaults unless the operator
opts in via `flash_identity_collect`, and makes no compliance guarantee. Owners running in
jurisdictions with data-protection law (e.g. GDPR) are responsible for their lawful basis,
disclosure, retention and erasure — the config surface and `purgeIdentity` exist to let them
implement whatever they are required to.

---

## 10. Implementation sequencing

1. **Skeleton** ✅ *(implemented)* — state machine + `stateChanged` + state-bag mirror in
   `Lifecycle.cs`, wired into `playerJoining`/`playerDropped`/`SendSpawnAsync`/`SetDead`;
   `getState`/`getSession` exports; headless selftest (`lifecycle=True`). Transitions live so
   far: `connecting → spawning → playing`, `playing ↔ dead`, `→ dropped`. The `spawned`/`ready`
   client acks, and the remaining states, arrive in the slices below.
2. **`playerReady` + apply pipeline** ✅ *(implemented)* — `flashfw:playerReady(netId, cid)`
   fires on the real client ack (`flashfw:spawnComplete`) after the ped is placed and the apply
   pipeline ran; also on `restart` for spawned players. Client pipeline in `client.lua`:
   `registerApplier(tag)` / `markApplied(tag)` / `awaitApply()`, driven by every adapter
   (built-in + charcreator). Idempotent/forgery-safe (`spawning → playing` only). Selftest covered.
3. **Limbo + spawn exports** ✅ *(implemented)* — client: `spawn` (canonical placement, shared
   with `spawn_native.lua`), `freezeInLimbo`, `orbitCam`/`stopCam`; server: `holdSpawn`/
   `releaseSpawn` (deferred `spawnAt` until the last token releases), `enterLimbo`/`exitLimbo`
   + `flashfw:limboEnter/Exit` (only from `playing`, idempotent). Charcreator template rebased
   on the primitives as living proof. Selftest covers hold bookkeeping + limbo gate.
4. **Spawn-point registry + death/respawn** ✅ *(implemented)* — `SpawnPoints.cs` (named,
   job-gated, server-validated points; `registerSpawnPoint`/`spawnAtPoint`/`spawnPlayer`
   exports with auto-revive + `respawning` state), `flashfw:preSpawn(netId, cid)` before the
   `spawnAt` answer, and `Death.cs` (a pure cause classifier — killer/weapon come from the
   death report via `setDead(netId, dead, killer, weapon)`; the `weaponDamageEvent` parse was
   dropped after the live test, see phase 6). `flashfw:playerDied` carries
   `{ killer, weapon, cause, x, y, z }`. Downed/bleed-out + respawn policy landed too (below).
5. **Queue + identity engine** ✅ *(implemented)* — `Identity.cs` in flash-admin (persistent
   graph, threshold-linked clusters, ip never links, cluster bans written into the existing
   `bans`/`ban_hardware_tokens` so the unchanged gate enforces them, `flash_identity_enrich`
   owner hook, `purgeIdentity`, selftest-covered) + `Queue.cs` (deferral hold loop after all
   gates, priority via `queue_priority`/`setQueuePriority`, grant reservation settled by
   `playerJoining` or a 60 s reaper; bookkeeping selftest-covered, hold loop
   client-verification-pending).

All five slices are implemented. Phase-6 extras also landed: the `downed` state with
optional bleed-out (`flash_downed_bleedout_s`), `revive`, and the server respawn policy
(`flash_respawn_point` / `flash_respawn_delay_s`). What remains before this design counts
as *proven* is the **live pass**: a real client driving connect → queue → spawn → ready →
death → respawn → limbo → reconnect on the local FXServer.

Each slice is independently shippable, managed-only, and additive to the DB. The neutral
events/state-bags mean existing resources keep working untouched at every step.
