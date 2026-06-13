# Releasing AmbientFx

AmbientFx ships as a **Velopack** installer: a self-contained win-x64 build (no .NET runtime
needed on the target machine) with delta auto-updates served from GitHub Releases.

## One-time setup

```powershell
dotnet tool install -g vpk          # Velopack CLI (build.ps1 installs it automatically too)
```

## Cutting a release

1. **Pick the next semantic version.** Velopack orders updates by this version — never reuse
   or decrease it. The current baseline lives in `src/Engine/AmbientFx.csproj` (`<Version>`).

2. **Build everything with one command** (web → wwwroot → publish → installer):

   ```powershell
   ./build.ps1 -Version 2.1.0
   ```

   Outputs land in `build/releases/`:
   - `AmbientFx-win-Setup.exe` — the double-click installer (per-user, Start-menu shortcut,
     "Apps & features" entry, bootstraps the Evergreen **WebView2 runtime** if missing)
   - `AmbientFx-2.1.0-full.nupkg` (+ `-delta.nupkg` after the first release) — update packages
   - `releases.win.json` / `RELEASES` — the update feed manifest

3. **Sign it** (once the owner's code-signing certificate exists — see *Code signing* below):

   ```powershell
   ./build.ps1 -Version 2.1.0 -SignParams '/td sha256 /fd sha256 /f C:\secrets\ambientfx.pfx /p <password> /tr http://timestamp.digicert.com'
   ```

4. **Publish to GitHub Releases** (the default update feed):

   ```powershell
   vpk upload github --repoUrl https://github.com/ktlaxton/ambient_screen_capture_V2 `
       --publish --releaseName "AmbientFx 2.1.0" --tag v2.1.0 `
       --token <github-pat> --outputDir build/releases
   ```

5. **Verify the update path**: launch an older installed build → it checks the feed on
   startup, downloads in the background, toasts "will be applied the next time AmbientFx
   starts", and runs the new version on the next launch. `Settings → Check for updates`
   does the same on demand.

## How updating works in the app

- `Program.Main` runs `VelopackApp.Build().Run()` **before** WPF/DI/single-instance, so
  install/update/uninstall hooks short-circuit cleanly.
- `UpdateService` checks the feed (`UpdateManager.CheckForUpdatesAsync`), downloads
  (`DownloadUpdatesAsync`), then stages with `WaitExitThenApplyUpdates(silent)` — the update
  applies when the process exits, so the next start is the new version.
- The feed URL defaults to this repo's GitHub Releases. Override per machine via
  `%AppData%\AmbientFx\settings.json` → `"updateFeedUrl"` (a GitHub repo URL or any static
  feed URL/path hosting the `vpk` output).
- Dev/unpackaged runs are detected (`UpdateManager.IsInstalled == false`) and skip update
  checks entirely.

## Code signing (currently BLOCKED on the owner's certificate)

- AC5 of Story 7.4 needs an OV/EV code-signing certificate purchased by the owner.
  Until then, builds are unsigned (fine for personal testing; SmartScreen will warn).
- When available, pass `-SignParams` (signtool syntax, see above) — Velopack signs the exe,
  packages and installer in one pass. For cloud signing (Azure Trusted Signing), `vpk` also
  supports `--azureTrustedSignFile`; see https://docs.velopack.io/packaging/signing.
- **Never commit the certificate or its password.** Keep them outside the repo and pass them
  on the command line or via CI secrets.

## Notes / gotchas

- **Autostart** uses `Environment.ProcessPath` and Velopack's stable
  `%LocalAppData%\AmbientFx\current\AmbientFx.exe` path, so the registry entry survives
  updates. A Debug-path autostart entry from development reads as *disabled* in the installed
  app (by design — the service verifies the path), so just re-enable it once after installing.
- **Single instance / Quit** (Story 7.3) behave identically in the installed build; the
  mutex is machine-wide per user.
- A clean Windows 11 machine needs nothing preinstalled: the publish is self-contained and
  the installer bootstraps WebView2 (`--framework webview2`).
