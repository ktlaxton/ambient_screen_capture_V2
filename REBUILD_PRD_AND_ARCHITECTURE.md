# AmbientFx — Rebuild PRD & Recommended Architecture

**Status:** Handoff specification for implementation
**Date:** 2026-06-10
**Audience:** The implementing AI model (assume **no access to prior chat context** — this document is self-contained)
**Supersedes:** `docs/ue5-transition-context.md`, `docs/brownfield-prd-epic4.md`, and all Epic 4 (Unreal Engine) plans. **Unreal Engine is not part of this product.**

---

## 0. TL;DR for the implementer

You are rebuilding a rough MVP called the **Ambient Effects Engine** into a polished product, **AmbientFx**. It is a Windows desktop app that turns idle secondary monitors into reactive "ambient lighting" (think software Philips Ambilight) for gamers: it captures the **primary monitor's** screen colors + system audio, and renders reactive visuals across **secondary monitors**.

**The single most important fact:** in the current MVP the screen color analysis is **fake** — it does not read screen pixels at all. Fixing that is Priority 1.

**Chosen architecture:** a native installed Windows app where the **C# process is a headless engine** (capture, audio/FFT, color analysis, window/settings/tray management) and the **entire front end — both the control UI and the visual effects — is a bundled local web app rendered in WebView2** (HTML/CSS for UI, WebGL/Three.js for effects).

**This is still a normal installed `.exe`/MSIX app.** It runs offline, has no server, and is not "a web app" in the hosted sense. WebView2 is only the UI rendering layer (same approach as VS Code, Discord, Spotify).

---

## 1. Product Vision & Goals

### 1.1 What it is
AmbientFx extends the on-screen experience onto unused secondary monitors. During gameplay (or media), the secondary monitors glow and animate in sync with what's happening on the primary monitor — driven by the primary monitor's edge colors and the system audio — without any per-game integration.

### 1.2 Goals
- **G1 — Immersion:** Make multi-monitor setups feel like one connected, reactive space.
- **G2 — Honesty:** Visuals must genuinely reflect screen content and audio (the MVP faked this).
- **G3 — Polish:** Look and feel like a real product, not a Windows system dialog.
- **G4 — Performance:** Negligible impact on gaming — GPU-bound, low CPU, capped frame rate, idle when nothing changes.
- **G5 — Extensibility:** Adding a new effect should be adding a small web module, not surgery.

### 1.3 Non-goals
- No Unreal Engine, no separate render process, no Spout/NDI IPC.
- No hardware LED control (this is screen-based ambient lighting, not Govee/Hue integration) — though that is a plausible future extension.
- No cloud, no accounts, no telemetry server.

### 1.4 Target user
PC gamers on Windows 10/11 with 2+ monitors and a gaming-capable GPU.

---

## 2. Current State (what exists today) & What's Wrong

The existing repo is `AmbientEffectsEngine` — C#/.NET 8, **WPF + WinForms** (despite docs claiming WinUI 3), using `Microsoft.Extensions.DependencyInjection`. It has a clean event-driven pipeline:

```
ScreenCaptureService ─┐
                      ├─► DataProcessingService ─► EffectsRenderingService ─► IEffect (per monitor)
AudioCaptureService ──┘     (color + intensity)        (strategy pattern)
```

### 2.1 Critical flaws to fix
1. **FAKE screen color (Priority 1).** `Services/Processing/DataProcessingService.cs` → `CalculateDominantColor()` ignores pixels and computes a color from `frame.Width * frame.Height + timestamp.Millisecond`. The captured bitmap is PNG-encoded into a throwaway `MockDirect3DSurface` and never analyzed. **The product's headline feature is currently random noise.**
2. **Slow capture.** `Services/Capture/ScreenCaptureService.cs` uses GDI+ `CopyFromScreen` on a 33 ms timer and PNG-encodes every frame. Replace with **Windows.Graphics.Capture** (Direct3D11, stays on GPU).
3. **GDI/WinForms rendering.** `SoftGlowEffect` (sets a `Form.BackColor`) and `GenerativeVisualizerEffect` (GDI+ particles) render via maximized borderless `Form`s. Won't scale to quality visuals. Replace with WebGL.
4. **Audio is RMS-only.** `AudioCaptureService` computes a single average volume level. Add an **FFT** for frequency bands (bass/mid/treble).
5. **"Microsoft popup" UI.** `Views/MainWindow.xaml` + `Views/MonitorSetupPage.xaml` use the default OS title bar, stock `CheckBox`/`ComboBox`/`Slider`, gray `#F5F5F5` cards. Errors are raised as literal `MessageBox.Show()` (`ViewModels/MainViewModel.cs`).
6. **Settings persistence bug.** `ViewModels/MainViewModel.cs` → `SaveSettingsAsync()` always writes `TargetMonitorIds = new List<string>()` and `SourceMonitorId = string.Empty`, so monitor selection is not persisted from the main view model. Fix during rebuild.

### 2.2 Good bones — KEEP and adapt
- DI container + `App` startup composition (`App.xaml.cs`).
- The event-driven **service pipeline pattern** and interfaces: `IScreenCaptureService`, `IAudioCaptureService`, `IDataProcessingService`, `IEffectsRenderingService`.
- `ISettingsService` / `SettingsService` (JSON persistence) + `Models/ApplicationSettings.cs` (extend it).
- `IMonitorDetectionService` / `MonitorDetectionService` (monitor enumeration + change events).
- `SystemTrayService`.
- `AudioCaptureService` — WASAPI loopback works; extend with FFT.
- The `AmbientEffectsEngine.Tests` xUnit + Moq project.

---

## 3. Functional Requirements

| ID | Requirement |
|----|-------------|
| FR1 | Capture the user-selected **source monitor** in real time using Windows.Graphics.Capture (Direct3D11). |
| FR2 | Extract **real edge-zone colors** from captured pixels: N zones per screen edge (default 8/edge), produced by GPU-downscaling the frame and reading back a small image. No more synthetic colors. |
| FR3 | Capture system audio via WASAPI loopback and compute an **FFT** into a small set of frequency bands (default 8–16 bands) plus an overall intensity. |
| FR4 | Stream processed data (edge colors + FFT bands + timestamp) from the C# engine to the WebView2 effect surfaces at ~60 fps via `PostWebMessage` (JSON). Raw video frames are **not** sent across the bridge. |
| FR5 | Render visual effects as **WebGL (Three.js)** inside a borderless, fullscreen, topmost WebView2 window on each selected **target monitor**. |
| FR6 | Provide multiple selectable effects (strategy pattern preserved as web modules). Ship at minimum: **Edge Glow (Ambilight)**, **Plasma/Fluid**, **Audio Bars**, **Particle Field**. |
| FR7 | Map physical monitor layout so the correct screen edge "spills" onto the physically adjacent target monitor. |
| FR8 | Provide a **control UI** (WebView2, custom chrome): master on/off, source/target monitor selection (spatial map), effect gallery with **live previews**, per-effect parameters, global intensity / smoothing / brightness, audio sensitivity, max-FPS cap. |
| FR9 | Persist all settings to a local JSON file in `%AppData%`; support named **presets/profiles** switchable from the tray. |
| FR10 | System tray with quick toggle, preset switch, open-settings, quit. Optional autostart-on-login and global hotkeys. |
| FR11 | First-run **onboarding**: detect monitors → choose source + targets → pick a starting effect → minimize to tray. |
| FR12 | Surface errors/status as **in-UI toasts/banners**, never blocking `MessageBox` dialogs. |
| FR13 | When effects are disabled, effect windows are hidden/closed so secondary monitors are fully usable. |

---

## 4. Non-Functional Requirements

| ID | Requirement |
|----|-------------|
| NFR1 | **Performance:** target < 5% CPU and modest GPU use at 60 fps on mid-range gaming hardware. Cap render FPS (configurable). Skip work when capture frames are unchanged (idle detection). |
| NFR2 | **Latency:** screen/audio → on-screen reaction under ~50 ms end to end. |
| NFR3 | **Footprint:** installed size in the low tens of MB (excluding the WebView2 runtime, which is preinstalled on Windows 11). No multi-GB dependencies. |
| NFR4 | **Startup:** visible/usable within ~2 seconds. |
| NFR5 | **Stability:** capture/audio/render failures degrade gracefully (effect off + toast), never crash the host. A global exception handler logs to file (Serilog). |
| NFR6 | **Offline:** fully functional with no network. No data leaves the machine. |
| NFR7 | **Compatibility:** Windows 10 v1903+ and Windows 11; x64. Requires Windows.Graphics.Capture support (Win10 1803+). |
| NFR8 | **Maintainability:** adding an effect = one self-contained web module + a manifest entry; no engine changes. |

---

## 5. Recommended Architecture

### 5.1 High-level

```
┌──────────────────────── AmbientFx.exe (single installed native process) ────────────────────────┐
│                                                                                                   │
│  C# ENGINE (headless host)                          WebView2 LAYER (bundled local web app)        │
│  ─────────────────────────                          ──────────────────────────────────────       │
│  ┌──────────────────────────┐                       ┌──────────────────────────────────────────┐ │
│  │ Capture (D3D11)          │                        │ Control window (custom chrome, WebView2) │ │
│  │  Windows.Graphics.Capture│                        │  • settings / monitor map / gallery      │ │
│  │  → GPU downscale → readback                       │  • React + TypeScript                    │ │
│  └───────────┬──────────────┘                        └──────────────────────────────────────────┘ │
│  ┌───────────▼──────────────┐   engine → web         ┌──────────────────────────────────────────┐ │
│  │ Processing               │  PostWebMessageAsJson  │ Effect window  (1 per target monitor)    │ │
│  │  edge-zone colors + FFT  │  ───────────────────▶  │  • fullscreen WebGL canvas (Three.js)    │ │
│  └───────────┬──────────────┘  ~60fps tiny JSON      │  • consumes FramePayload, renders shader  │ │
│  ┌───────────▼──────────────┐                        └──────────────────────────────────────────┘ │
│  │ Audio (NAudio WASAPI+FFT)│   web → engine                                                       │
│  ├──────────────────────────┤  commands (JSON)  ◀──────────────  (set effect, params, monitors…)   │
│  │ Settings / Monitors /    │                                                                      │
│  │ Tray / Autostart / Hotkey│                                                                      │
│  └──────────────────────────┘                                                                      │
└───────────────────────────────────────────────────────────────────────────────────────────────────┘
```

### 5.2 Why this shape
- The **only** data that must cross the C#↔JS boundary per frame is tiny (a handful of colors + FFT floats), because all heavy analysis is done in C#. JSON `PostWebMessage` is therefore more than fast enough. **Do not** pipe raw frames to the web layer.
- The web layer owns *all* visuals (UI chrome + effects) → one design language, total styling freedom, and it plays to the implementing model's strength at generating HTML/CSS/GLSL.
- The C# engine owns *all* OS integration → testable services, no OS calls in the UI.

### 5.3 The bridge (contract is the spine of the system)

**Transport:** `CoreWebView2.PostWebMessageAsJson` (engine → web) and `window.chrome.webview.postMessage` (web → engine), with a typed envelope `{ "type": string, "payload": object }`.

**Engine → Web messages**
- `frame` — the per-frame data stream (high frequency). Schema:
  ```jsonc
  {
    "type": "frame",
    "payload": {
      "t": 1234567.89,                 // engine timestamp (ms, monotonic)
      "edges": {                        // edge-zone colors, sRGB 0-255
        "top":    [[r,g,b], ...],       // length = zonesPerEdge
        "bottom": [[r,g,b], ...],
        "left":   [[r,g,b], ...],
        "right":  [[r,g,b], ...]
      },
      "dominant": [r,g,b],              // overall dominant color
      "audio": {
        "intensity": 0.0,               // 0..1 overall
        "bands": [0.0, ...]             // normalized 0..1 per frequency band
      }
    }
  }
  ```
- `status` — `{ "type":"status", "payload": { "level":"info|warn|error", "message": "…" } }` → drives toasts.
- `config` — full current settings snapshot pushed to the control UI on load/change.
- `monitors` — current monitor topology (ids, names, bounds, isPrimary, position).

**Web → Engine commands**
- `setEnabled { enabled }`
- `setSourceMonitor { monitorId }`
- `setTargetMonitors { monitorIds: [] }`
- `setEffect { monitorId|all, effectId }`
- `setEffectParams { effectId, params: {} }`
- `setGlobal { intensity, smoothing, brightness, audioSensitivity, maxFps }`
- `savePreset { name }` / `loadPreset { name }` / `deletePreset { name }`
- `setAutostart { enabled }` / `setHotkey { action, keys }`
- `requestState {}` (UI asks engine to re-push `config` + `monitors`)

> Keep these DTOs defined once in C# and mirrored in a TypeScript `bridge.ts`; treat the schema as a versioned contract.

### 5.4 Capture & color analysis (the Priority-1 fix)
1. Acquire a `GraphicsCaptureItem` for the source **monitor** via `IGraphicsCaptureItemInterop::CreateForMonitor(HMONITOR)`.
2. Create a D3D11 device, a `Direct3D11CaptureFramePool` (recommend `CreateFreeThreaded`), and a `GraphicsCaptureSession`.
3. On each frame: take the `IDirect3DSurface`, **GPU-downscale** to a small target (e.g. 64×36) — via a render pass or `CopyResource` into a smaller staging texture — then map the staging texture and read back the small pixel buffer (cheap).
4. From the downscaled image, compute **edge-zone averages** (default 8 zones/edge) and an overall dominant color. Apply temporal smoothing (keep the MVP's moving-average idea; default window ~5 frames) to avoid flicker.
5. Emit into the `frame` payload.

> Downscaling on GPU + reading back a tiny image is the key to low CPU. Never read back the full-resolution frame each tick.

### 5.5 Audio analysis
- Keep `WasapiLoopbackCapture`. Accumulate samples into a window, run an **FFT** (e.g. via a small FFT helper or MathNet.Numerics), and reduce magnitudes into `bands` (log-spaced, default 8–16). Compute overall `intensity`. Apply `audioSensitivity` scaling + smoothing. Emit into the `frame` payload.

### 5.6 Window management
- **Effect windows:** one borderless, topmost, fullscreen window per selected target monitor (WPF `Window`, `WindowStyle=None`, `ResizeMode=NoResize`, positioned to the monitor bounds), each hosting a `WebView2` that loads the effect runtime. Opaque full-screen canvas — no transparency/click-through needed because the monitor is dedicated to the effect while enabled.
- **Control window:** a single WebView2-hosted window with **custom chrome** (`WindowStyle=None`). Implement the draggable title bar using WebView2's non-client region support — enable `CoreWebView2Settings.IsNonClientRegionSupportEnabled` and use CSS `app-region: drag` on the title bar — so dragging/maximize behave natively without a host round-trip.
- **Asset loading:** bundle the built web app and serve it via `CoreWebView2.SetVirtualHostNameToFolderMapping("app.local", <dir>, DenyCors)`, loading `https://app.local/index.html`. Avoid `file://`.

### 5.7 Effects as web modules
Each effect is a self-contained module exposing a tiny interface, e.g.:
```ts
interface Effect {
  id: string;
  name: string;
  description: string;
  defaultParams: Record<string, number | string | boolean>;
  init(ctx: { gl: WebGLRenderingContext | THREE.WebGLRenderer; canvas: HTMLCanvasElement }): void;
  onFrame(payload: FramePayload, params: EffectParams): void;  // called per engine 'frame'
  dispose(): void;
}
```
A manifest (`effects/manifest.json`) lists available effects so the gallery and the engine share one source of truth. **Adding an effect = new module + manifest entry. No C# change.**

---

## 6. Technology Stack

| Layer | Choice | Notes |
|-------|--------|-------|
| Host language | C# 12 / .NET 8 | Keep. |
| Host UI shell | WPF (windows only — chrome lives in web) | Minimal XAML; just window hosts for WebView2. |
| Embedded UI | **WebView2** (`Microsoft.Web.WebView2`) | Runtime preinstalled on Win11; bootstrapper for Win10. |
| Capture | Windows.Graphics.Capture + Direct3D11 (CsWinRT / Windows App SDK or Win32 interop) | Replaces GDI+. |
| Audio | NAudio 2.x (`WasapiLoopbackCapture`) + FFT (MathNet.Numerics or hand-rolled) | Extends existing. |
| DI | Microsoft.Extensions.DependencyInjection | Keep. |
| Logging | Serilog (file sink) | Add. |
| Front-end build | **Vite + TypeScript + React** | Svelte is an acceptable lighter alternative; confirm with owner. |
| Effects | **Three.js** (WebGL) | Over raw WebGL2 to cut boilerplate. |
| Settings | JSON in `%AppData%/AmbientFx/` | Keep `ISettingsService` pattern. |
| Tests | xUnit + Moq (C#); Vitest (web) | Extend existing test project; add web tests. |
| Packaging | MSIX + optional MSI/Inno; code signing; auto-update (e.g. Velopack/Squirrel) | Phase 5. |

---

## 7. Recommended Project Structure

```
/AmbientFx/                         (repo root — rename optional; see Open Questions)
  AmbientFx.sln
  /src/
    /Engine/                        (C# host project)
      App.xaml(.cs)                 DI composition + lifecycle
      /Capture/                     ScreenCaptureService (WGC), AudioCaptureService (+FFT)
      /Processing/                  DataProcessingService (edge zones + bands)
      /Hosting/                     ControlWindow, EffectWindow, WebViewWindowManager
      /Bridge/                      MessageEnvelope, FramePayload, command DTOs, serializer
      /Services/                    SettingsService, MonitorDetectionService,
                                    SystemTrayService, AutostartService, HotkeyService
      /Models/                      ApplicationSettings, Preset, DisplayMonitor, EffectStyle
  /web/                             (front-end source; built output copied to Engine output)
    /control/                       control UI app (React)
    /effects/                       effect modules + manifest.json + shared runtime
    /shared/                        bridge.ts (typed contract), design system, theme
    vite.config.ts
  /tests/
    /Engine.Tests/                  xUnit + Moq (port/extend existing tests)
    /web.tests/                     Vitest
  /docs/                            (existing docs; mark UE5 docs as superseded)
```

> The existing `AmbientEffectsEngine/` and `AmbientEffectsEngine.Tests/` can be migrated into `/src/Engine` and `/tests/Engine.Tests` incrementally — port the keep-list services first.

---

## 8. Data Model (extend `ApplicationSettings`)

```csharp
class ApplicationSettings {
  bool   IsEnabled;
  string SourceMonitorId;              // FIX: actually persist this
  List<string> TargetMonitorIds;       // FIX: actually persist this
  string ActiveEffectId;
  float  AudioSensitivity;             // 0..1
  float  GlobalIntensity;              // 0..1
  float  Smoothing;                    // 0..1
  float  Brightness;                   // 0..1
  int    MaxFps;                       // e.g. 30/60/120
  int    ZonesPerEdge;                 // default 8
  int    AudioBands;                   // default 12
  bool   Autostart;
  Dictionary<string, EffectParams> EffectParamsById;
  List<Preset> Presets;
  string ActivePresetName;
}
class Preset { string Name; ApplicationSettings Snapshot; }  // or a subset
```

---

## 9. Build Sequence (phased; each phase ends in something demonstrable)

> **Sequencing principle:** make the foundation *real and visible* (Phases 0–2) before investing in breadth/polish. Phase 1 specifically eliminates the fake-color flaw so there is an honest, working product early.

### Phase 0 — Skeleton + bridge
- C# host opens one **custom-chrome** WebView2 control window loading bundled web assets via virtual-host mapping.
- Two-way message bridge working (typed envelope); system tray running.
- **Done when:** a branded "hello world" window appears that is clearly *not* a default Windows dialog, and round-trips a test command/echo over the bridge.

### Phase 1 — Make capture REAL (Priority 1)
- Replace `ScreenCaptureService` with Windows.Graphics.Capture; implement GPU-downscale + edge-zone color extraction.
- Add FFT bands to `AudioCaptureService`.
- Stream the `frame` payload; build a **debug overlay** in the web UI visualizing live edge colors + FFT bands.
- **Done when:** the overlay visibly tracks real screen content and real audio. (Delete the fake `CalculateDominantColor` and `MockDirect3DSurface`.)

### Phase 2 — Effect windows + first real effect
- `WebViewWindowManager` spawns a fullscreen effect window per target monitor; stream `frame` to each.
- Implement the **Edge Glow (Ambilight)** effect that mirrors real edge colors, layout-aware (FR7).
- **Done when:** a second monitor glows in sync with the primary monitor's content.

### Phase 3 — Polished control UI
- Custom chrome, dark gamer theme, **spatial monitor map** (click to set source/targets), effect gallery with **live previews**, sliders/toggles, first-run onboarding, toast notifications (replace all `MessageBox`).
- **Done when:** a new user can configure everything without touching a Windows-style dialog.

### Phase 4 — Effect library
- Add Plasma/Fluid, Audio Bars, Particle Field as web modules; per-effect parameters; manifest-driven gallery.
- **Done when:** ≥4 effects selectable with adjustable params.

### Phase 5 — Product polish
- Presets/profiles, autostart, global hotkeys, FPS cap + idle detection, settings persistence fixes, MSIX/installer, code signing, auto-update.
- **Done when:** installable, signed, updates itself, starts with Windows.

### Phase 6 — Tests + docs
- Port/extend C# unit tests (mock capture/audio/bridge); add Vitest for web; write setup + contribution docs; mark UE5 docs superseded.

---

## 10. Acceptance Criteria (product-level)
- **AC1:** With a video playing on the source monitor, target-monitor visuals demonstrably match its edge colors (verifiable by eye and by logging the `frame` payload). No synthetic color anywhere.
- **AC2:** With audio playing, effects react to intensity and at least bass vs. treble differ visibly.
- **AC3:** The entire UI uses custom chrome and custom controls — no default OS title bar, no stock `MessageBox`, no gray system cards.
- **AC4:** Disabling effects releases the target monitors for normal use.
- **AC5:** Settings + presets persist across restarts (including source/target monitor selection — the MVP bug is fixed).
- **AC6:** Measured CPU < 5% and stable 60 fps on a mid-range gaming PC; no orphaned windows/processes after exit.
- **AC7:** Capture/audio/render failure shows a toast and disables the effect without crashing.

---

## 11. Risks & Mitigations
- **WebView2 per-frame messaging overhead** → keep payloads tiny (colors + bands only); never send frames; coalesce to the configured FPS.
- **Windows.Graphics.Capture interop friction in .NET** → use the established `IGraphicsCaptureItemInterop` pattern; reference Microsoft's screen-capture sample; isolate all interop in `ScreenCaptureService`.
- **Multiple WebView2 instances (one per monitor) memory** → effect windows render a single lightweight canvas each; share assets via the virtual host; dispose on disable.
- **Custom-chrome drag/resize correctness** → use WebView2 non-client region support (`IsNonClientRegionSupportEnabled` + CSS `app-region`).
- **GPU contention with the game** → FPS cap + idle detection (skip unchanged frames) + downscaled analysis.

---

## 12. Open Questions for the Owner (carry these into implementation)
1. **Repo/name:** keep `AmbientEffectsEngine` or rename to `AmbientFx` with the new `/src` + `/web` layout? (Doc assumes the new layout; either is fine.)
2. **Front-end stack:** React (assumed) vs. Svelte. Minor; confirm preference.
3. **Effects when "off":** fully close effect windows (assumed) vs. keep a dim idle state.
4. **Future hardware LEDs** (Hue/Govee/OpenRGB) — out of scope now, but keep the `frame` data model general enough to feed an LED sink later.

---

## 13. Explicitly Out of Scope
Unreal Engine / Niagara, Spout/NDI/IPC, a second render process, cloud/accounts/telemetry, per-game integrations, and hardware LED control (this release).

---

*End of specification. This document is the single source of truth for the rebuild; the older `docs/` UE5 and Epic 4 materials are superseded and should not be implemented.*
