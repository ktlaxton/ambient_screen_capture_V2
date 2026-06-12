# AmbientFx

Turn idle secondary monitors into reactive ambient lighting. AmbientFx captures your **primary monitor's** screen colors and **system audio**, and renders synchronized WebGL visuals across your **secondary monitors** — software Ambilight for multi-monitor gaming setups. Fully offline, no accounts, no telemetry.

> This is the rebuild of the original *Ambient Effects Engine* MVP, per [`REBUILD_PRD_AND_ARCHITECTURE.md`](REBUILD_PRD_AND_ARCHITECTURE.md) — the single source of truth for product scope. The legacy `AmbientEffectsEngine/` project and the UE5 documents under `docs/` are superseded.

## How it works

```
┌────────────────────────── AmbientFx.exe ──────────────────────────┐
│  C# ENGINE (headless)                  WEBVIEW2 LAYER (bundled)   │
│  Windows.Graphics.Capture (D3D11)      Control window (React UI)  │
│   └─ GPU downscale → edge-zone colors  Effect windows (Three.js / │
│  WASAPI loopback → FFT bands            WebGL, one per monitor)   │
│  Settings / tray / hotkeys      ──60fps tiny JSON──▶  visuals     │
└───────────────────────────────────────────────────────────────────┘
```

- The engine does all heavy analysis natively; only ~2 KB JSON frames (edge colors + FFT bands) cross the bridge per tick. Raw video never leaves the engine.
- Effects are self-contained web modules (`web/src/effects/<id>/`) — adding one requires **no engine changes** ([guide](#adding-an-effect)).
- Ships five effects: **Edge Glow** (layout-aware Ambilight), **Plasma Flow**, **Spectrum Bars**, **Particle Field**, **Aurora**.

## Requirements

- Windows 10 1903+ / Windows 11, x64, 2+ monitors
- [WebView2 Evergreen Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) (preinstalled on Windows 11)
- Build: [.NET SDK 8+](https://dotnet.microsoft.com/download) and [Node.js 20+](https://nodejs.org/)

## Build & run

```powershell
# 1. Build the web layer (outputs into src/Engine/wwwroot)
cd web
npm install
npm run build

# 2. Build + run the engine
cd ..
dotnet build AmbientFx.sln
dotnet run --project src/Engine/AmbientFx.csproj
```

First run shows an onboarding wizard: pick the source monitor, the target monitor(s), and a starting effect. The app lives in the system tray; closing the window hides it (tray → Exit to quit). `--minimized` starts straight to the tray (used by autostart).

## Project layout

```
AmbientFx.sln
src/Engine/            C# engine (net8.0-windows, WPF hosts for WebView2)
  Bridge/              typed engine⇄web contract (mirrored by web/src/shared/bridge.ts)
  Capture/             Windows.Graphics.Capture + WASAPI/FFT
  Processing/          edge-zone extraction, smoothing, idle detection
  Hosting/             control window, effect windows, WebView2 manager
  Services/            settings, monitors, tray, autostart, hotkeys, coordinator
web/                   Vite + React + TypeScript + Three.js front end
  src/control/         control UI (dashboard, monitor map, gallery, onboarding)
  src/effect-host/     runtime loaded into each effect window
  src/effects/<id>/    one folder per effect module
  src/shared/          bridge.ts (contract) + engine simulator for browser dev
tests/Engine.Tests/    xUnit + Moq engine tests
docs/                  product docs (UE5-era docs are marked superseded)
```

## Developing

**Web UI / effects in a plain browser** — no engine needed. The bridge auto-falls back to a built-in engine **simulator** (synthetic screen colors + 124 bpm audio):

```powershell
cd web
npm run dev
# control UI:    http://localhost:5173/control.html        (?firstrun=1 forces onboarding)
# any effect:    http://localhost:5173/effects.html?effectId=plasma   (&fps=1 shows an FPS overlay)
```

**Engine ↔ web contract**: `src/Engine/Bridge/*.cs` and `web/src/shared/bridge.ts` are mirrors. Change them together or not at all.

### Adding an effect

1. Create `web/src/effects/my-effect/index.ts` default-exporting an `EffectModule` (see `web/src/effects/types.ts`).
2. Register it in `web/src/effects/registry.ts` and add the same id/name/description to `manifest.json` (a test enforces the sync).
3. `npm run build`. Done — it appears in the gallery with auto-generated parameter controls.

## Tests

```powershell
dotnet test AmbientFx.sln          # engine: processing math, FFT bands, persistence, coordinator
cd web && npm test                 # web: bridge contract, simulator, registry/manifest sync, host logic
```

## Where things live at runtime

| What | Where |
|------|-------|
| Settings + presets | `%AppData%\AmbientFx\settings.json` (+ `.backup`) |
| Logs (rolling, 7 days) | `%AppData%\AmbientFx\logs\` |
| WebView2 profile | `%LocalAppData%\AmbientFx\WebView2\` |
| Autostart entry | `HKCU\...\Run\AmbientFx` (`--minimized`) |

## Troubleshooting

- **Black effect windows** — verify WebView2 runtime is installed; check the log for `WebView2 environment ready`.
- **Effects don't react to the screen** — confirm the right *source* monitor is selected; the "Live signal" panel in the control UI shows exactly what the engine sees.
- **No audio reaction** — the engine analyzes the *default render device* via loopback; exclusive-mode audio apps bypass loopback.
- **Capture stops after display changes** — the engine auto-recovers; if a source monitor disconnects, effects pause with a toast and can be re-enabled.

## Out of scope (this release)

MSIX packaging / code signing / auto-update (planned next), hardware LEDs (Hue/Govee/OpenRGB), per-game integrations, anything Unreal Engine.
