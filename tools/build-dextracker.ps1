# Builds DexTracker (TheNinjask/DexTracker, MIT - submodule external\DexTracker) into ShinyForge\Assets\DexTracker.
# ShinyForge serves that folder on 127.0.0.1 (Services\DexTrackerService) and adds its own bridge script, so the built
# files are copied unchanged. Needs Node.js (winget install OpenJS.NodeJS.LTS). The output is committed, so build.ps1
# doesn't need Node - run this only to move to a new DexTracker version:
#   cd external\DexTracker; git fetch --tags; git checkout vX.Y.Z; cd ..\..; .\tools\build-dextracker.ps1
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$src = Join-Path $root "external\DexTracker"
$out = Join-Path $root "ShinyForge\Assets\DexTracker"
if (-not (Test-Path (Join-Path $src "package.json"))) { throw "external\DexTracker is missing - run: git submodule update --init external/DexTracker" }
$node = "C:\Program Files\nodejs"
if (Test-Path $node) { $env:Path = "$node;$env:Path" }
if (-not (Get-Command npm -ErrorAction SilentlyContinue)) { throw "npm not found - install Node.js LTS first" }

Push-Location $src
try {
    cmd /c "npm ci --no-audit --no-fund 2>&1"
    if ($LASTEXITCODE -ne 0) { throw "npm ci failed" }
    $env:BASE_PATH = "/"          # served from the root of ShinyForge's local DexTracker server
    cmd /c "npm run build 2>&1"
    if ($LASTEXITCODE -ne 0) { throw "DexTracker build failed" }
    $version = (Get-Content package.json -Raw | ConvertFrom-Json).version
} finally {
    Remove-Item Env:BASE_PATH -ErrorAction SilentlyContinue
    Pop-Location
}

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force $out | Out-Null
# The offline service worker is left out: ShinyForge serves the files itself, and a cached worker would keep showing an
# old DexTracker after an update.
Get-ChildItem (Join-Path $src "dist") | Where-Object { $_.Name -notin @("sw.js", "registerSW.js") -and $_.Name -notlike "workbox-*.js" } |
    Copy-Item -Destination $out -Recurse
Copy-Item (Join-Path $src "LICENSE") (Join-Path $out "LICENSE.txt")
Set-Content (Join-Path $out "version.txt") $version -Encoding ascii
Write-Host "DexTracker $version -> $out"
