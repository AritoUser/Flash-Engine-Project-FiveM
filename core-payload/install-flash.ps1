<#
.SYNOPSIS
  Installs Flash as a RUNTIME-LOADED component into a stock FXServer (no custom FXServer).

.DESCRIPTION
  FiveM loads components at runtime via components.json (-> LoadLibrary("<name>.dll")).
  This script:
    1) copies citizen-scripting-flash.dll next to the other citizen-scripting-*.dll,
    2) copies the .NET host to <ServerDir>\flash\ (matches the core's host-dir discovery:
       <dll dir>\flash\FlashHost.dll -> no FLASH_HOST_DIR needed),
    3) idempotently adds "citizen:scripting:flash" to components.json.

  IMPORTANT (ABI pin): FiveM has NO stable plugin ABI. The Flash payload must be built
  against exactly the FXServer source revision that belongs to THIS server artifact.
  Use a payload matching your server version.

.PARAMETER ServerDir
  Directory of the FXServer artifacts (contains FXServer.exe + components.json).

.PARAMETER PayloadDir
  Directory of the Flash payload: citizen-scripting-flash.dll + subfolder flash\ (host).

.EXAMPLE
  .\install-flash.ps1 -ServerDir "C:\FXServer\server" -PayloadDir ".\flash-payload"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $ServerDir,
    [Parameter(Mandatory)] [string] $PayloadDir
)

$ErrorActionPreference = "Stop"
$COMPONENT = "citizen:scripting:flash"
$DLL = "citizen-scripting-flash.dll"

# --- Validation ---------------------------------------------------------------
$componentsJson = Join-Path $ServerDir "components.json"
if (-not (Test-Path $componentsJson)) {
    throw "components.json not found in '$ServerDir' -- is this an FXServer artifacts directory?"
}
$payloadDll = Join-Path $PayloadDir $DLL
if (-not (Test-Path $payloadDll)) {
    throw "$DLL not found in payload '$PayloadDir'."
}
$payloadHost = Join-Path $PayloadDir "flash"
if (-not (Test-Path (Join-Path $payloadHost "FlashHost.dll"))) {
    throw "flash\FlashHost.dll not found in payload '$PayloadDir'."
}

# --- 0) .NET 10 check (warning only, no abort) ---------------------------------
# The Flash host needs the .NET 10 runtime. Warn early and in plain language -- the most
# common support case would otherwise be a cryptic boot error at server start.
$dotnetRoots = @()
if ($env:DOTNET_ROOT) { $dotnetRoots += $env:DOTNET_ROOT }
$dotnetRoots += (Join-Path $env:ProgramFiles "dotnet")
$has10 = $false
foreach ($root in $dotnetRoots) {
    $fxr = Join-Path $root "host\fxr"
    if ((Test-Path $fxr) -and (Get-ChildItem $fxr -Directory -ErrorAction SilentlyContinue | Where-Object Name -like "10.*")) {
        $has10 = $true; break
    }
}
if ($has10) {
    Write-Host "  = .NET 10 runtime found."
} else {
    Write-Warning ".NET 10 runtime NOT found -- Flash cannot start without it."
    Write-Warning "Download: https://dotnet.microsoft.com/download/dotnet/10.0 (.NET Runtime, x64)"
}

# --- 1) Copy the component DLL -------------------------------------------------
Copy-Item -Force $payloadDll (Join-Path $ServerDir $DLL)
Write-Host "  + $DLL -> $ServerDir"

# --- 2) Copy the .NET host to <ServerDir>\flash\ -------------------------------
$hostTarget = Join-Path $ServerDir "flash"
New-Item -ItemType Directory -Force $hostTarget | Out-Null
# Mirror the contents of Payload\flash\* (recursive).
Copy-Item -Force -Recurse (Join-Path $payloadHost "*") $hostTarget
Write-Host "  + host -> $hostTarget"

# --- 3) Patch components.json idempotently -------------------------------------
# The file is a JSON array of strings. Only add the entry if it's missing.
$raw = Get-Content -Raw $componentsJson
# @() forces an array even if the file (theoretically) had a single entry.
$list = @($raw | ConvertFrom-Json)
if ($list -contains $COMPONENT) {
    Write-Host "  = components.json: '$COMPONENT' already present (idempotent)."
} else {
    $list += $COMPONENT
    # Write with indentation; @() keeps it an array in case ConvertTo-Json would unwrap it.
    $json = ConvertTo-Json @($list) -Depth 5
    Set-Content -Path $componentsJson -Value $json -Encoding utf8
    Write-Host "  + components.json: '$COMPONENT' added."
}

Write-Host ""
Write-Host "Flash installed. Start the server -> the component loads at startup."
