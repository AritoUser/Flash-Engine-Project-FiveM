<div align="center">

# ⚡ Flash-Engine

**A server-side C# framework for FiveM.**
Write your server logic in modern C# (.NET 10) against a clean SDK —
you never touch the native foundation.

[![build](https://github.com/AritoUser/Flash-Engine-Project-FiveM/actions/workflows/build.yml/badge.svg)](https://github.com/AritoUser/Flash-Engine-Project-FiveM/actions/workflows/build.yml)
[![NuGet](https://img.shields.io/nuget/v/Flash.Sdk?label=Flash.Sdk)](https://www.nuget.org/packages/Flash.Sdk)
[![license](https://img.shields.io/badge/license-MIT%20(SDK)-blue)](LICENSE)

[Getting Started](docs/getting-started.md) ·
[API Reference](docs/api.md) ·
[Cookbook](docs/cookbook.md) ·
[Changelog](CHANGELOG.md)

</div>

---

## What is Flash?

Flash installs as a **runtime-loaded add-on** into a stock FXServer — no modified
server, no compiling FiveM yourself. It hosts .NET 10 inside the server and gives you a
modern, typed C# API for everything server-side:

| | |
|---|---|
| **Lifecycle** | `IResource` / `ITickable` — isolated, hot-restartable resources |
| **Events** | server ↔ client, msgpack interop with Lua/JS resources |
| **Players** | names, identifiers, ping, kick, per-player state bags |
| **Commands & deferrals** | chat/console commands, connect gating (whitelists/bans) |
| **State** | replicated state bags + a reactive server-side key/value store |
| **Exports** | synchronous resource↔resource calls with return values |
| **Async & HTTP** | `async/await` on the script thread, webhooks/external APIs |
| **Database** | parameterized queries, SQLite out of the box |
| **Natives** | effectively 100 % of the FiveM/GTA natives, fully typed |
| **Culling & grid** | engine-enforced anti-ESP culling + spatial queries |

```csharp
using Flash;

public sealed class Main : IResource
{
    public void OnStart()
    {
        Log.Info($"Started, {Players.Count} online");

        Commands.Register("hello", (src, args, raw) =>
            Players.Get(src).Emit("chat:addMessage", "Welcome!"));

        Events.On("myshop:buy", async args =>
        {
            var player = Players.Get(Events.SourceNetId);
            if (Exports.Call<bool>("flash-core", "removeMoney", player.NetId, "cash", 250))
                player.Emit("myshop:delivered", args[0]);
            await Http.Post(webhookUrl, $"{{\"content\":\"{player.Name} bought {args[0]}\"}}");
        });
    }
    public void OnStop() { }
}
```

> **Server-side.** Flash runs your logic on the **server**. Client-side scripting
> (peds/world/NUI on the game client) stays Lua; client↔server goes through net events.

## Quick start

1. **Server:** download the FXServer artifact **pinned in the
   [compatibility matrix](CHANGELOG.md#compatibility-matrix)** and install the
   [.NET 10 runtime](https://dotnet.microsoft.com/download/dotnet/10.0).
2. **Install Flash:** the compiled core ships right in this repo (`core-payload/`).
   Clone or download the repository, then:
   ```powershell
   .\core-payload\install-flash.ps1 -ServerDir "C:\FXServer\server" -PayloadDir .\core-payload
   ```
   (Same content is also attached to each [release](../../releases) as
   `flash-payload-<version>.zip`.)
3. **Your first resource:**
   ```powershell
   dotnet new install Flash.Templates
   dotnet new flash-resource -n MyShop
   dotnet build MyShop\MyShop.csproj -c Release
   ```
   Drop `MyShop.dll` (+ the template's `fxmanifest.lua`/`main.flash`) into
   `<server-data>\resources\MyShop\`, add `ensure MyShop`, start the server.

Full walkthrough incl. troubleshooting: **[docs/getting-started.md](docs/getting-started.md)**.

## Repository layout

```
├─ core-payload/     the compiled core (binary, ready to install — source is private)
├─ src/Flash.Sdk/    the public SDK (source, MIT) — also on NuGet as Flash.Sdk
├─ templates/        dotnet new flash-resource (NuGet: Flash.Templates)
├─ docs/             getting started · API reference · cookbook
├─ tools/            install-flash.ps1 (payload installer)
├─ CHANGELOG.md      versions + compatibility matrix
└─ LICENSE           MIT (SDK/templates/docs/installer)
```

The **core payload** (`core-payload/`: native component + .NET host) ships as a
**binary** — everyone can use it, its source code is private
(see `core-payload/LICENSE.txt`).

## Version binding (important)

FiveM has **no stable plugin ABI**. Each payload release is built against **one
specific FXServer artifact** — the exact version is stated in the release notes and in
the [compatibility matrix](CHANGELOG.md#compatibility-matrix). Always match payload and
artifact; the SDK additionally verifies the core version at startup and reports
mismatches in plain text.

## Requirements

- Windows FXServer (Linux support is planned)
- The pinned FXServer artifact (see release notes)
- .NET 10 runtime on the server, .NET 10 SDK on your dev machine

## Contributing & support

- Questions & ideas → [Discussions](../../discussions)
- Bugs → [Issues](../../issues/new/choose) (please fill in the version fields)
- PRs → welcome for SDK, templates, docs and installer — see
  [CONTRIBUTING.md](CONTRIBUTING.md)

## License

The contents of this repository (`src/`, `templates/`, `docs/`, `tools/`) are licensed
under **MIT** ([LICENSE](LICENSE)).

The **core payload** (`core-payload/`, also attached to releases) is
**proprietary, closed-source software** under its own license
([core-payload/LICENSE.txt](core-payload/LICENSE.txt)): use on your own servers is
free; modifying, decompiling, reverse engineering or redistributing the payload is not
permitted.
