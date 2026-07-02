# Contributing

Thanks for your interest in Flash-Engine!

## Where things go
- **Questions & ideas** → [Discussions](../../discussions). Please don't open issues for
  "how do I …?" questions.
- **Bugs** → [open an issue](../../issues/new/choose) using the bug template. The version
  fields (FXServer artifact, payload, SDK, .NET) are required — we can't reproduce
  anything without them.
- **Pull requests** are welcome for everything that lives in this repo: the SDK
  (`src/Flash.Sdk`), templates, docs and the installer.
- **The native core is closed-source.** It is developed in a private repository and
  distributed only as a binary payload under a proprietary license — core changes cannot
  be contributed, and modifying/decompiling the payload is not permitted. Bug reports
  about core *behavior* are very welcome; everything you need for scripting is in the
  public SDK.

## Building locally
```powershell
dotnet build src/Flash.Sdk/Flash.Sdk.csproj -c Release
dotnet pack  src/Flash.Sdk/Flash.Sdk.csproj -c Release -o artifacts
```
The SDK builds standalone — `Natives.g.cs` (generated) is included in the repo.

## Guidelines
- Keep the public API surface small and documented (XML docs on public members — they
  ship as IntelliSense).
- No new runtime dependencies in `Flash.Sdk` without prior discussion (the SDK is shared
  by every resource at runtime).
- Match the existing code style; comments explain *why*, not *what*.
