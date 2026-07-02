# Changelog

All notable changes to Flash-Engine. Versioning: SemVer for the public `Flash.Sdk`;
the payload is additionally bound to one FXServer artifact version (FiveM has no
stable plugin ABI).

## Compatibility matrix

| Flash release | Flash.Sdk (NuGet) | Core contract | FXServer artifact (Windows) |
|---|---|---|---|
| 0.1.0 | 0.1.0 | v12 | **31689** (`06d4d348c`) |

Rules:
- **SDK ↔ payload:** The SDK checks the core version at startup and rejects a payload
  that is too old with a plain-text message. A newer payload with an older SDK is always
  fine (the contract only grows by appending).
- **Payload ↔ artifact:** pinned exactly. A different artifact version needs a payload
  release built for it.

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
