<#
.SYNOPSIS
    One-command AmbientFx release build (Story 7.4): web -> wwwroot -> self-contained
    publish -> Velopack installer + update feed.

.DESCRIPTION
    Steps:
      1. npm ci + vite build in web/ (emits to src/Engine/wwwroot)
      2. dotnet publish src/Engine (Release, win-x64, self-contained — no .NET needed on target)
      3. vpk pack -> build/releases/ (AmbientFx-win-Setup.exe + delta/full packages + RELEASES feed)

    Code signing is wired but optional: pass -SignParams with your signtool arguments once the
    owner's certificate is available (see docs/RELEASING.md). Unsigned builds work for testing.

.PARAMETER Version
    Semantic version stamped into the assembly AND the Velopack release (ordering matters).

.PARAMETER SignParams
    Optional signtool.exe parameters (everything after 'sign'), e.g.
    '/td sha256 /fd sha256 /f C:\secrets\ambientfx.pfx /p <password> /tr http://timestamp.digicert.com'
    NEVER commit certificates or passwords; pass at the command line or from CI secrets.

.PARAMETER SkipWeb
    Reuse the existing src/Engine/wwwroot output (faster iteration on packaging itself).

.EXAMPLE
    ./build.ps1 -Version 2.1.0
.EXAMPLE
    ./build.ps1 -Version 2.1.0 -SignParams '/td sha256 /fd sha256 /f cert.pfx /tr http://timestamp.digicert.com'
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')]
    [string]$Version,

    [string]$SignParams = '',

    [switch]$SkipWeb
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$publishDir = Join-Path $root 'build/publish'
$releaseDir = Join-Path $root 'build/releases'

function Step([string]$name, [scriptblock]$body) {
    Write-Host "`n=== $name ===" -ForegroundColor Cyan
    & $body
    if ($LASTEXITCODE -ne 0) { throw "Step failed: $name (exit $LASTEXITCODE)" }
}

# 0. Tooling checks --------------------------------------------------------
Step 'Check tooling' {
    node --version | Out-Null
    dotnet --version | Out-Null
    if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
        Write-Host 'vpk not found — installing the Velopack CLI (dotnet tool install -g vpk)...'
        dotnet tool install -g vpk
    }
    $global:LASTEXITCODE = 0
}

# 1. Web build -> src/Engine/wwwroot ---------------------------------------
if (-not $SkipWeb) {
    Step 'Web build (vite -> src/Engine/wwwroot)' {
        npm --prefix (Join-Path $root 'web') ci
        if ($LASTEXITCODE -ne 0) { throw 'npm ci failed' }
        npm --prefix (Join-Path $root 'web') run build
    }
} else {
    Write-Host 'SkipWeb: reusing existing src/Engine/wwwroot' -ForegroundColor Yellow
}

# 2. Self-contained publish -------------------------------------------------
Step 'dotnet publish (Release, win-x64, self-contained)' {
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
    dotnet publish (Join-Path $root 'src/Engine/AmbientFx.csproj') `
        -c Release -r win-x64 --self-contained true `
        -p:Version=$Version `
        -o $publishDir
}

if (-not (Test-Path (Join-Path $publishDir 'AmbientFx.exe'))) { throw 'Publish output missing AmbientFx.exe' }
if (-not (Test-Path (Join-Path $publishDir 'wwwroot/control.html'))) { throw 'Publish output missing wwwroot (web assets)' }

# 3. Velopack pack ----------------------------------------------------------
Step 'vpk pack (installer + update packages)' {
    $vpkArgs = @(
        'pack',
        '--packId', 'AmbientFx',
        '--packTitle', 'AmbientFx',
        '--packAuthors', 'Kirk Laxton',
        '--packVersion', $Version,
        '--packDir', $publishDir,
        '--mainExe', 'AmbientFx.exe',
        '--framework', 'webview2',   # bootstraps the Evergreen WebView2 runtime on clean machines
        '--outputDir', $releaseDir
    )
    if ($SignParams) { $vpkArgs += @('--signParams', $SignParams) }
    vpk @vpkArgs
}

# 4. Smoke-assert artifacts --------------------------------------------------
$setup = Get-ChildItem $releaseDir -Filter '*Setup*.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
$portablePkg = Get-ChildItem $releaseDir -Filter "AmbientFx-$Version*.nupkg" -ErrorAction SilentlyContinue
if (-not $setup) { throw "No installer produced in $releaseDir" }

Write-Host "`nDone. Artifacts in $releaseDir :" -ForegroundColor Green
Get-ChildItem $releaseDir | ForEach-Object { Write-Host "  $($_.Name)" }
if (-not $SignParams) {
    Write-Host "`nNOTE: build is UNSIGNED (no -SignParams given). Fine for testing; releases should be signed." -ForegroundColor Yellow
}
Write-Host "Next: upload to GitHub Releases (see docs/RELEASING.md), e.g."
Write-Host "  vpk upload github --repoUrl https://github.com/ktlaxton/ambient_screen_capture_V2 --publish --releaseName `"AmbientFx $Version`" --tag v$Version --token <gh-token> --outputDir `"$releaseDir`""
