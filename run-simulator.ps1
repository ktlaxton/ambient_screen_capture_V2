<#
.SYNOPSIS
    Dev/QA one-double-click launcher for the AmbientFx Layout Simulator (Epic 10, Story 10.6).

.DESCRIPTION
    Builds the Debug engine — the ONLY configuration where SIMULATOR_ENABLED is defined — and launches it
    with --simulator. The simulator is compiled out of the signed Release build, so this script hard-codes
    -c Debug and can never start a build the installer contains.

    Steps:
      1. (only if web assets are missing, or -Web) npm ci + vite build -> src/Engine/wwwroot
      2. dotnet build src/Engine -c Debug       (skip with -NoBuild to reuse the last Debug build)
      3. launch the Debug AmbientFx.exe with --simulator, preselecting a scenario if one was given

.PARAMETER Scenario
    Optional curated scenario name (e.g. six-grid, L-shape, 3-wide) or a path to a scenario .json to
    preselect. Defaults to the simulator's built-in default scenario (SIM_MONITORS).

.PARAMETER NoBuild
    Reuse the existing Debug binary without rebuilding (fast relaunch).

.PARAMETER Web
    Force a web (vite) rebuild even if src/Engine/wwwroot already exists.

.EXAMPLE
    ./run-simulator.ps1
.EXAMPLE
    ./run-simulator.ps1 six-grid
.EXAMPLE
    ./run-simulator.ps1 -NoBuild
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$Scenario = '',

    [switch]$NoBuild,

    [switch]$Web
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$engine = Join-Path $root 'src/Engine'
$webDir = Join-Path $root 'web'
$wwwroot = Join-Path $engine 'wwwroot/control.html'

function Step([string]$name, [scriptblock]$body) {
    Write-Host "`n=== $name ===" -ForegroundColor Cyan
    & $body
    if ($LASTEXITCODE -ne 0) { throw "Step failed: $name (exit $LASTEXITCODE)" }
}

# 1. Web assets (the simulator's WebView2 needs them). Build only when missing, or when -Web is passed.
if ($Web -or -not (Test-Path $wwwroot)) {
    Step 'Web build (vite -> src/Engine/wwwroot)' {
        npm --prefix $webDir ci
        if ($LASTEXITCODE -ne 0) { throw 'npm ci failed' }
        npm --prefix $webDir run build
    }
} else {
    Write-Host 'Reusing existing src/Engine/wwwroot (pass -Web to rebuild the web UI)' -ForegroundColor Yellow
}

# 2. Debug build (the only config that defines SIMULATOR_ENABLED). Skip with -NoBuild.
if (-not $NoBuild) {
    Step 'dotnet build (Debug — defines SIMULATOR_ENABLED)' {
        dotnet build (Join-Path $engine 'AmbientFx.csproj') -c Debug
    }
} else {
    Write-Host 'NoBuild: reusing the existing Debug binary' -ForegroundColor Yellow
}

# 3. Locate the built exe (resilient to the target-framework folder name).
$exe = Get-ChildItem -Path (Join-Path $engine 'bin/Debug') -Filter 'AmbientFx.exe' -Recurse -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $exe) { throw "No Debug AmbientFx.exe found under $engine/bin/Debug — run without -NoBuild first." }

# 4. Optional scenario preselect (read by SimulatorComposition via AMBIENTFX_SIMULATOR_SCENARIO).
if ($Scenario) {
    $env:AMBIENTFX_SIMULATOR_SCENARIO = $Scenario
    Write-Host "Scenario: $Scenario" -ForegroundColor Green
} else {
    Remove-Item Env:\AMBIENTFX_SIMULATOR_SCENARIO -ErrorAction SilentlyContinue
}

Write-Host "Launching $($exe.FullName) --simulator" -ForegroundColor Green
# Start detached so the launcher console can close while the simulator runs. The child process inherits
# AMBIENTFX_SIMULATOR_SCENARIO from this session.
Start-Process -FilePath $exe.FullName -ArgumentList '--simulator'
