# C# resource template (Flash-Engine)

Starter scaffold for your own **server-side C# resource**. You only write C# against the
public `Flash.Sdk` (NuGet) — you never touch the native core/host.

> Note: client-side scripting (peds/world on the game client) stays Lua for now —
> Flash-Engine is server-side. This is about **server logic** in C#.

## 1. Install the template (once)
```
dotnet new install Flash.Templates
```
(Or locally from this folder: `dotnet new install ./templates/csharp-resource`.)

## 2. Create a resource
```
dotnet new flash-resource -n MyShop
```
This creates a `MyShop/` folder — everything is already renamed to `MyShop`
(`MyShop.csproj`, `namespace MyShop`, `<AssemblyName>MyShop`). Class/namespace names are
up to you — the host finds the `IResource` class through the interface.

## 3. Build
```
dotnet build MyShop.csproj -c Release
```
Result: `bin/Release/net10.0/MyShop.dll`. **Just this one DLL** — `Flash.Sdk.dll` and its
dependencies are deliberately NOT copied (the host provides them at runtime; see
`ExcludeAssets=runtime` in the `.csproj`).

## 4. Drop it in as a resource
Create `<serverDataPath>/resources/MyShop/` with:
- `fxmanifest.lua` (contains `server_script 'main.flash'`)
- `main.flash` (marker file, content irrelevant)
- `MyShop.dll` (from `bin/Release/net10.0/`)

## 5. Start
In `server.cfg`:
```
ensure MyShop
```
Start the server → your `OnStart()` runs.

## SDK overview
- **`Flash.IResource`** — `OnStart()` / `OnStop()` (mandatory lifecycle).
- **`Flash.ITickable`** — `OnTick()` (optional, per server frame).
- **`Flash.Events`**
  - `On(name, args => …)` / `On(name, () => …)` — receive events (server-internal **and**
    from clients; source via `Events.Source` / `Events.SourceNetId`).
  - `Emit(name, args…)` — server-internal event.
  - `EmitClient(netId, name, args…)` / `EmitAllClients(name, args…)` — server → client.
- **`Flash.Players`** — `All`, `Get(netId)`, `Count`; `player.Name`, `player.Identifiers`,
  `player.IdentifierOfType("license")`, `player.Ping`, `player.Kick(reason)`,
  `player.Emit(event, args…)`, `player.State` (state bag).
- **`Flash.Commands`** — `Register(name, (src, args, raw) => …)`.
- **`Flash.StateBag` / `Flash.GlobalState`** — server-authoritative, replicated state.
- **`Flash.Exports`** — `Register` / `Call<T>` (resource↔resource, with return values).
- **`Flash.State`** — reactive key/value store (`SetInt/GetInt`, `SetString/…`, `Delete`, `OnChange`).
- **`Flash.Async`** — `await Async.Delay(ms)` / `await Async.NextFrame()`, `SetTimeout(ms, cb)` /
  `SetInterval(ms, cb)` (returns `IDisposable` to cancel). `async` code is allowed everywhere:
  in `OnStart`/`OnTick` **and** in event/command/deferral handlers (continuations resume on
  the server script thread).
- **`Flash.Db`** — database API (SQLite): `Execute` / `Scalar` / `Query`.
- **`Flash.Http`** — `await Http.Get(url)` / `Post(url, body)` / `Request(...)` → `HttpResponse`
  (`Status`, `Body`, `Headers`, `Ok`, `Json<T>()`). For webhooks/external APIs/auth.
- **`Flash.Log`** — `Info` / `Warn` / `Error` / `Debug`.
- **`Flash.Natives.*`** — all FiveM/GTA natives (server-side mostly `Flash.Natives.Cfx`).
- **`Flash.Culling`** — drives FiveM's **engine-enforced** entity culling (anti-ESP/VRAM)
  for *networked* entities: `Culling.Apply(entity, Priority.Cosmetic)` (category → culling
  radius), `Culling.SetPlayerRadius(netId, radius)` (weak client). The engine then only
  sends a player entities within the radius.
- **`Flash.Grid`** — server-authoritative 2D spatial grid (150m cells = FiveM sector) for
  *your own/non-networked* objects (markers/3D texts) + spatial queries: `Insert(id, pos,
  Priority)`, `Query(center, radius, maxHeight, max)`, `QueryBudgeted(…, budgetPerCell, …)`
  (priority culling per chunk; Priority.Critical always passes).

## Notes
- Event/client arguments travel as **msgpack** (interop with Lua/JS resources).
- Every resource runs isolated in its own unloadable `AssemblyLoadContext`
  (resource stop/restart without a server restart).
- Pitfall: avoid `global::X` **inside** a `$"…"` interpolated string — pull it into a
  local variable first.
