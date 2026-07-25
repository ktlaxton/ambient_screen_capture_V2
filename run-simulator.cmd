@echo off
REM Dev/QA double-click launcher for the AmbientFx Layout Simulator (Epic 10, Story 10.6).
REM Runs run-simulator.ps1 (build Debug + launch --simulator) with an execution-policy bypass so a
REM double-click from Explorer works without the PowerShell "do you want to run this script" prompt.
REM Prefers PowerShell 7 (pwsh) and falls back to Windows PowerShell. Forwards all args, e.g.:
REM   run-simulator.cmd six-grid
REM   run-simulator.cmd -NoBuild
setlocal
where pwsh >nul 2>nul
if %errorlevel%==0 (
  pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-simulator.ps1" %*
) else (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-simulator.ps1" %*
)
if %errorlevel% neq 0 (
  echo.
  echo run-simulator failed ^(exit %errorlevel%^).
  pause
)
endlocal
