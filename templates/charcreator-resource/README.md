# Character creator template (Flash-Engine)

A working **character creator** that plugs into flash-core's neutral spawn contract. It is
the reference for the "bring your own spawn" case: instead of flash-core's built-in
`spawn_native.lua`, *this* resource decides what happens between join and spawn.

What you get:
- **Client** (`client.lua`) — hides the player on join, runs a creator scene (camera + NUI),
  then drives the spawn contract (`flashfw:requestSpawn` → `flashfw:spawnAt`).
- **Server** (C#) — persists the chosen appearance per **character id (cid)** in its own
  table, over flash-core's RPC bridge.
- **UI** (`html/index.html`) — a small self-contained NUI (no build step, no external assets).

## How it hooks the spawn system

```
join ─▶ hide + freeze the ped
      ─▶ rpcCall('charcreator:load')  ── has a look? ──┐
                                                        │ no  ─▶ open creator ─▶ save ─▶┐
                                                        │ yes ─▶ apply the look ────────┤
                                                        └───────────────────────────────┤
      ◀── flashfw:spawnAt(x,y,z,heading,hasPos) ◀── requestSpawn ◀──────────────────────┘
      ─▶ place ped, make visible, fade in
```

flash-core still owns the **position** (it answers `flashfw:spawnAt` with the saved spot, or
`hasPos=false` for a fresh character). This resource owns the **appearance** and the **spawn
act**. That split is the whole point.

## 1. Install the template (once)
```
dotnet new install Flash.Templates
```
(Or locally from the repo: `dotnet new install ./templates/charcreator-resource`.)

## 2. Create your resource
```
dotnet new flash-charcreator -n MyCreator
```
Everything is renamed to `MyCreator` (`MyCreator.csproj`, `namespace MyCreator`,
`<AssemblyName>MyCreator`).

## 3. Build the server DLL
```
dotnet build MyCreator.csproj -c Release
```
Result: `bin/Release/net10.0/MyCreator.dll` — **just this one DLL** (`Flash.Sdk.dll` is
provided by the host at runtime; see `ExcludeAssets=runtime` in the `.csproj`).

## 4. Drop it in as a resource
Create `<serverDataPath>/resources/MyCreator/` with:
- `fxmanifest.lua`
- `main.flash` (marker)
- `client.lua`
- `html/index.html`
- `MyCreator.dll` (from `bin/Release/net10.0/`)

## 5. Wire it up in `server.cfg`
```cfg
# REQUIRED: turn off flash-core's built-in spawn so it doesn't race this creator.
setr flash_spawn_adapter "custom"

ensure flash-core     # identity, characters, position persistence, RPC + spawn contract
ensure MyCreator      # this resource
```

If you forget the `setr` line, flash-core's `spawn_native.lua` will *also* spawn the player
and you'll see them flicker into the world before the creator takes over.

## Extending it
- **More appearance options**: `client.lua` keeps a deliberately small appearance table
  (gender, face blend, skin, hair, eyebrows). Add head overlays (beard, makeup, blemishes),
  clothing (`SetPedComponentVariation`), and face features — then add matching sliders to
  `html/index.html`. The server stores the blob opaquely, so no server change is needed for
  new fields.
- **Multichar**: this template creates/loads one look per `cid`. flash-core's schema already
  separates account from character, so a character-select screen would pick the `cid` first,
  then hand off to this creator.
- **Spawn point**: change the `hasPos == false` fallback coordinates in the `flashfw:spawnAt`
  handler (defaults to Legion Square).

## Notes
- Requires **flash-core** running (declared as a `dependency` in `fxmanifest.lua`): it
  provides the RPC bridge (`exports['flash-core']:rpcCall`), the `getCid` export, and the
  spawn contract.
- The heavy lifting comes from flash-core's **lifecycle primitives** (client exports):
  `freezeInLimbo(true)` holds the player hidden on join, `orbitCam`/`stopCam` run the creator
  camera, and `spawn(x, y, z, heading)` performs the canonical placement (collision streaming,
  apply pipeline → `flashfw:playerReady`, fade-in). This template only decides *what happens
  in between* — that's the intended division of labour.
- Appearance travels to the server as a **JSON string** (not a msgpack table) — simplest and
  avoids nested-table encoding surprises on the event bus.
- The server table uses SQLite syntax (`INSERT OR REPLACE`), the Flash default DB. Adjust if
  you point `flash_db_provider` at MySQL.
