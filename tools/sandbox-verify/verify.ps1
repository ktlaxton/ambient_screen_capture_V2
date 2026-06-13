# Story 8.4 AC7 — automated clean-machine verification, run INSIDE Windows Sandbox.
# The sandbox has no iCUE and no RGB software: the app must install, launch, run, and show
# the peripheral feature as cleanly unavailable. Results land in results\verify-report.txt
# on the host (the mapped folder is read-write).
$ErrorActionPreference = 'Continue'
$mapped = 'C:\AmbientFxVerify'
$report = Join-Path $mapped 'results\verify-report.txt'
New-Item -ItemType Directory -Force (Split-Path $report) | Out-Null

function Log([string]$msg) {
    $line = "{0:HH:mm:ss}  {1}" -f (Get-Date), $msg
    $line | Tee-Object -FilePath $report -Append
}

Log "=== AmbientFx clean-machine verification (Story 8.4 AC7) ==="

# 1. Install (Velopack setup is zero-click; it auto-launches the app when done).
$setup = Get-ChildItem (Join-Path $mapped 'releases') -Filter '*Setup*.exe' | Select-Object -First 1
if (-not $setup) { Log 'FAIL: no Setup.exe found in mapped releases folder'; exit 1 }
Log "Installing $($setup.Name)..."
Start-Process $setup.FullName
$deadline = (Get-Date).AddMinutes(5)
while (-not (Get-Process AmbientFx -ErrorAction SilentlyContinue)) {
    if ((Get-Date) -gt $deadline) { Log 'FAIL: AmbientFx.exe never started within 5 minutes'; exit 1 }
    Start-Sleep -Seconds 3
}
Log 'PASS: installed and AmbientFx.exe is running'
Start-Sleep -Seconds 20  # let the engine finish startup + write logs

# 2. Enable the pipeline + the peripheral feature via settings, then restart the app —
#    this exercises the no-iCUE path (must degrade to a state, never crash).
Log 'Enabling effects + ambient peripherals via settings.json and restarting...'
Get-Process AmbientFx -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 3
$settingsPath = Join-Path $env:APPDATA 'AmbientFx\settings.json'
$settings = if (Test-Path $settingsPath) { Get-Content $settingsPath -Raw | ConvertFrom-Json } else { [pscustomobject]@{} }
$settings | Add-Member -Force NoteProperty isEnabled $true
$settings | Add-Member -Force NoteProperty sourceMonitorId '\\.\DISPLAY1'
$settings | Add-Member -Force NoteProperty ambientDevicesEnabled $true
$settings | Add-Member -Force NoteProperty firstRunCompleted $true
$settings | ConvertTo-Json -Depth 10 | Set-Content $settingsPath
Start-Process "$env:LOCALAPPDATA\AmbientFx\current\AmbientFx.exe" -ArgumentList '--minimized'
Start-Sleep -Seconds 25

# 3. Judge the outcome from process state + engine logs.
$alive = Get-Process AmbientFx -ErrorAction SilentlyContinue
if ($alive) { Log 'PASS: app still running with effects + peripherals enabled (no crash)' }
else { Log 'FAIL: app is not running after enabling the peripheral feature' }

$log = Get-ChildItem "$env:APPDATA\AmbientFx\logs\*.log" -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime | Select-Object -Last 1
if ($log) {
    $content = Get-Content $log.FullName -Raw
    if ($content -match 'Engine started') { Log 'PASS: engine startup completed' }
    if ($content -match 'Ambient devices: (\w+)') { Log "PASS: peripheral state surfaced cleanly -> $($Matches[1])" }
    else { Log 'WARN: no ambient-device state line found (feature may not have started — check log copy)' }
    if ($content -match '\[FTL\]|Fatal unhandled') { Log 'FAIL: fatal errors present in the log' }
    else { Log 'PASS: no fatal errors in the log' }
    Copy-Item $log.FullName (Join-Path $mapped 'results\') -Force
    Log "Log copied to results\$($log.Name)"
} else {
    Log 'FAIL: no engine log found'
}

Log '=== Verification finished — review results above ==='
