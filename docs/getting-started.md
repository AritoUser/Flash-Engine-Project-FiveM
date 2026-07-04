# Getting Started — Flash-Engine

From zero to a running server with your own **C# server logic**. Three parts:
1. [Install Flash on an FXServer](#1-install-flash) (once per server)
2. [Write your own C# resource](#2-your-own-c-resource)
3. [Start & verify](#3-start--verify) — plus [Troubleshooting](#troubleshooting)

> **Server-side.** Flash runs your logic on the **server** (events, players, state, DB,
> commands). Client-side scripting (peds/world/NUI on the game client) stays Lua;
> client↔server communication goes through net events.

---

## Prerequisites
- **Windows server** (the Flash component is currently Windows x64).
- **.NET 10 runtime** installed on the server (the core finds `hostfxr` automatically —
  no fixed version wired in). Check: `dotnet --list-runtimes` shows a `Microsoft.NETCore.App 10.*`.
- **An FXServer artifact** that matches the Flash payload (see the box below).
- **.NET 10 SDK** on your development machine (to build resources).

> ⚠️ **Version pin (important).** FiveM has **no stable plugin ABI**. A Flash payload is
> built against **exactly one** FXServer source revision and only runs with the matching
> artifact. Download the FXServer build **stated for the payload** — not "whatever is newest".
>
> **Current pin: artifact `31689`** (source revision `06d4d348c`) —
> [download](https://runtime.fivem.net/artifacts/fivem/build_server_windows/master/31689-06d4d348c381af33219579cf8d5e2fb27665ffa8/server.7z).

---

## 1. Install Flash

The compiled core ships right in this repository under **`core-payload/`**
(`citizen-scripting-flash.dll` + a `flash\` folder with the .NET host + the installer).
Clone or download the repo, then install into the FXServer artifacts directory (the one
with `FXServer.exe` + `components.json`):

```powershell
.\core-payload\install-flash.ps1 -ServerDir "C:\FXServer\server" -PayloadDir .\core-payload
```

(The same payload is attached to every release as `flash-payload-<version>.zip`, if you
prefer a standalone download.)

The script is **idempotent** and does three things:
- copies `citizen-scripting-flash.dll` next to the other `citizen-scripting-*.dll`,
- copies the .NET host to `<ServerDir>\flash\` (the core finds it there automatically —
  **no** `FLASH_HOST_DIR` needed),
- adds `"citizen:scripting:flash"` to `components.json`.

No modified FXServer: FiveM loads the component at startup from `components.json`.
Re-running does no harm (idempotent); after a server update to the same pinned version,
simply run it again.

---

## 2. Your own C# resource

You only write C# against the public **`Flash.Sdk`** — you never touch the native core/host.

```powershell
dotnet new install Flash.Templates        # once
dotnet new flash-resource -n MyShop       # creates folder MyShop\
dotnet build MyShop\MyShop.csproj -c Release
```

Result: `MyShop\bin\Release\net10.0\MyShop.dll` — **just this one DLL** (the host provides
the SDK at runtime, see `ExcludeAssets=runtime` in the `.csproj`).

Minimal example (`Main.cs`):
```csharp
using Flash;

public sealed class Main : IResource
{
    public void OnStart()
    {
        Log.Info($"MyShop started, {Players.Count} online");

        // Command: /hello  ->  greets the caller
        Commands.Register("hello", (src, args, raw) =>
            Players.Get(src).Emit("chat:addMessage", "Welcome!"));

        // Receive an event from a client and respond
        Events.On("myshop:buy", a =>
        {
            var p = Players.Get(Events.SourceNetId);
            Log.Info($"{p.Name} buys {a[0]}");
        });
    }

    public void OnStop() { }
}
```

Full API overview: [API reference](api.md) · [`templates/csharp-resource/README.md`](../templates/csharp-resource/README.md).

---

## 3. Start & verify

**Place the resource** under `<serverDataPath>/resources/MyShop/`:
- `fxmanifest.lua` (contains `server_script 'main.flash'`)
- `main.flash` (marker file, content irrelevant)
- `MyShop.dll` (from `bin/Release/net10.0/`)

(`fxmanifest.lua` + `main.flash` ship with the template — just copy them along.)

**In `server.cfg`:**
```
ensure MyShop
```

**Recommended security hardening** (in `server.cfg`, for any production server):
```cfg
set sv_stateBagStrictMode true   # revoke client write access to replicated state bags
                                 # (prevents state-bag overflow network DoS + logic spoofing)
setr sv_filterRequestControl 2   # reject unauthorized entity-ownership takeover
                                 # (kills the classic ejector/flinging + delete-gun exploits)
set flash_require_identifier true  # reject connections without a cryptographic identifier
set flash_event_rate_limit 32      # per-player client-event rate limit (0 = off)
```

**Start the server.** The Flash markers appear in the log:
```
[Flash-Engine] citizen:scripting:flash loaded
[Flash-Engine] .NET resource host started
[MyShop] [INFO] MyShop started, 0 online
```
If you see the last line, your `OnStart()` is running — done. Change the resource →
rebuild the DLL → `restart MyShop` (no server restart needed; every resource runs
isolated + unloadable).

> **Tip — chat:** The artifact ships the chat resource prebuilt
> (`<ServerDir>\citizen\system_resources\chat`) — copy it to `resources\` and add
> `ensure chat`. More recipes (client↔server, whitelist, webhooks):
> **[Cookbook](cookbook.md)**, all SDK areas: **[API reference](api.md)**.

---

## Troubleshooting

| Symptom in the log | Cause | Fix |
|---|---|---|
| `Could not load component citizen-scripting-flash` | DLL missing or **ABI mismatch** (wrong FXServer version) | Use the FXServer build pinned for the payload; re-run `install-flash` |
| Component loads but **no** `.NET resource host started` | .NET 10 runtime missing **or** host not found | Check `dotnet --list-runtimes`; make sure `<ServerDir>\flash\FlashHost.dll` exists (re-run `install-flash`), or set `FLASH_HOST_DIR` |
| `ERROR: the .NET host could not start` | see the plain-text hint printed right below it | The log message explains the exact cause + fix |
| Your resource doesn't start | not in `server.cfg` / DLL name ≠ folder name | Add `ensure <name>`; `<AssemblyName>` == resource folder name |
| `global::X` compile error inside `$"..."` | known C# pitfall | Pull the `global::` expression into a local variable before the interpolation |

---

## Appendix: building the payload yourself (core developers)
If you build the core yourself, this counterpart assembles the payload from the build outputs:
```powershell
.\stage-flash-payload.ps1 `
  -ComponentDll "<fivem-build>\citizen-scripting-flash.dll" `
  -HostDir "src\dotnet\FlashHost\bin\Release\net10.0" `
  -OutDir ".\flash-payload"
```
The `citizen-scripting-flash.dll` must be built against the **pinned** FXServer source
revision. (The core build itself lives in the private repository.)
