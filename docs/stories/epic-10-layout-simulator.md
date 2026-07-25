# Epic 10: Layout Simulator — Test Effects on Any Monitor Setup (Dev/QA)

## Status
Draft — created 2026-06-13. Scope locked with owner (see Decisions); seams confirmed feasible by a
five-seam feasibility pass (all Medium effort). Stories 10.1–10.5 below are ready to draft into
full story files. Dev/QA-only: this epic ships **nothing** in the signed installer.

## Context
Through Epic 9, AmbientFx is a complete, monetized desk-wide Ambilight: multi-monitor on-screen
glow, position-mapped RGB peripherals, audio reactivity, signed installer. The pipeline is built to
handle *any* Windows display arrangement — offsets, gaps, mixed resolutions/DPI, portrait, diagonal
corners (the whole point of Story 7.5's positional projection). But it can only be exercised against
the monitors physically attached to the dev machine. With two monitors on hand, the layouts the
product is supposed to support — three-wide, L-shapes, vertical stacks, mixed-DPI, a six-panel wall —
**cannot be seen or tested at all.**

This epic builds an in-app **Layout Simulator**: a dev/QA tool that fabricates an arbitrary monitor
topology, feeds it synthetic / media / mirrored content, runs it through the **real engine**, and
composites every virtual monitor (plus its effects and RGB peripherals) into a **single window** you
can look at — so effects can be validated across the full range of intended setups on a 2-monitor
machine, with no extra hardware and no driver installs.

## Decisions (locked with owner, 2026-06-13)
- **In-app simulator, not a virtual display driver.** An OS-level Indirect Display Driver (IDD)
  would give real phantom monitors and exercise the *entire* unmodified pipeline (real WGC capture,
  real `SetWindowPos`, real DPI, real hot-plug events), but it needs admin + a signed driver, the
  phantom screens are blank until you drag content onto them, and it isn't a shippable artifact. We
  chose the in-app route: a window we control, fully scriptable, zero driver install. (IDD remains a
  documented future option for true capture/placement fidelity — see Out of scope.)
- **Integrated real engine, not a web-only visualizer.** The simulator runs the actual
  `EngineCoordinator` with *simulated* monitor/capture/audio services, so projection
  (`MonitorLayout.ComputeRelation` + `monitorProjection.ts`), the effect runtime, the RGB
  `LedProjection`, and the audio path are all the **real code under test**. A web-only visualizer
  was simpler but could not validate the C# RGB `LedProjection` or the real audio path — two of the
  four things we need to validate. The existing browser simulator (`web/src/shared/simulator.ts`)
  stays as-is for engine-free effect dev; this epic is its engine-integrated complement.
- **Content: synthetic + media + mirror.** Virtual monitors can be fed deterministic test patterns,
  a media file, or a mirror of a real physical monitor (the one place real WGC capture runs).
- **Validate all four:** projection/layout correctness, visual look, RGB peripheral placement, and
  audio-reactive behavior.
- **Dev/QA-only.** Behind a compile-time `SIMULATOR_ENABLED` constant (Debug config) + a runtime
  `--simulator` / `AMBIENTFX_SIMULATOR` gate. Compiled out of the signed Velopack build entirely;
  **zero** impact on Free/Premium licensing or packaging.
- **Deliverable = manual visual tool + automation hooks.** Topologies are reusable JSON scenario
  fixtures that drive the manual tool now and can later drive an automated snapshot suite (the hook
  is in scope; the full CI golden-image suite is not).

## Architecture
The simulator swaps four DI seams for simulated implementations and redirects the per-monitor effect
windows into one composite surface. Everything else — `EngineCoordinator`, `DataProcessingService`,
`EdgeZoneExtractor`, the bridge, the effect runtime, `LedProjection`, `AudioModulation` — runs
**unmodified**. That is the design invariant: if the simulator forks the projection or effect logic,
it stops being a valid test.

```
[JSON scenario] -> SimulatedMonitorDetectionService : IMonitorDetectionService   (App.xaml.cs:179)
[pattern/media/mirror] -> SimulatedScreenCaptureService : IScreenCaptureService  (App.xaml.cs:174)
[synthetic audio] -> SimulatedAudioCaptureService : IAudioCaptureService         (App.xaml.cs:175)
                          |
                  real EngineCoordinator  (ComputeRelation, BuildWindowConfigFor, source/target sync)
                          |  WindowConfigPayload per virtual monitor  (bridge unchanged)
                          v
   WebViewWindowManager --(injected surface factory)--> IEffectSurfaceHost
        |                                                    |
   EffectWindow (prod: SetWindowPos)         SimulatorEffectSurface (child WebView2 in a viewport)
                                                             |
                                              SimulatorWindow: N viewports laid out to scale as the
                                              virtual desktop; real effect runtime + monitorProjection.ts
                          |
   real RgbNetAmbientDeviceService --(VisualizationBackend : IRgbDeviceBackend)--> virtual peripheral LED viz
```

Confirmed seams (file:line from the feasibility pass):
- **Capture swap** — `IScreenCaptureService` (`src/Engine/Capture/IScreenCaptureService.cs:10`);
  drop-in at `App.xaml.cs:174`. `ScreenFrameEventArgs` is tightly-packed BGRA, top-down, no stride,
  buffer **reused** per frame (subscriber copies synchronously); single capture source at a time
  (`EngineCoordinator.ResolveSource`, `ScreenCaptureService.cs:205` is the WGC `CreateForMonitor`).
- **Monitor topology** — `IMonitorDetectionService` (`GetMonitors`, `StartMonitoring/StopMonitoring`,
  `MonitorsChanged`); real impl debounces `SystemEvents.DisplaySettingsChanged` 500 ms. Coordinator
  handles change at `EngineCoordinator.cs:1108-1141`. `MonitorInfo` has no rotation field — portrait
  is just `width < height`; add an explicit orientation flag only if a test needs it.
- **Effect-window redirection** — introduce `IEffectSurfaceHost`; `EffectWindow` implements it
  (no behavior change); `WebViewWindowManager` builds surfaces via an injected
  `Func<MonitorInfo, IEffectSurfaceHost>` instead of `new EffectWindow()`. `WindowConfigPayload`,
  `EngineCoordinator`, and the bridge are untouched.
- **RGB readback** — tap `IRgbDeviceBackend.Apply()` (`RgbNetAmbientDeviceService.cs:327`) with a
  `VisualizationBackend` that records per-device per-LED sRGB colors from the real `LedProjection`.
- **Audio injection** — `SimulatedAudioCaptureService : IAudioCaptureService` emits synthetic
  bands/intensity on the real cadence; `DataProcessingService` consumes it with no change.
- **Gating** — `#if SIMULATOR_ENABLED` (defined only in Debug config) wrapping all simulator code +
  its UI entry; runtime `--simulator` arg (precedent: `--minimized`, `App.OnStartup`) /
  `AMBIENTFX_SIMULATOR` env. Release publish (`build.ps1`, `-c Release` → `vpk pack`) strips it.

## Stories
| # | Title | Summary | Status |
|---|-------|---------|--------|
| 10.1 | Simulated Topology & Capture (Headless Engine Harness) | `Simulated{MonitorDetection,ScreenCapture}Service`, JSON scenario fixtures, on-demand `MonitorsChanged`, and the dev-only `SIMULATOR_ENABLED` + `--simulator` gating. Engine runs end-to-end on a fabricated topology with synthetic frames; validated by tests. | Draft |
| 10.2 | Composite Simulator Window (See the Effects) | `IEffectSurfaceHost` seam, `SimulatorEffectSurface`, `SimulatorWindow` — N virtual monitors as scaled viewports running the real effect runtime; source content drawn behind each. Projection + visual validation. | Draft |
| 10.3 | Content Sources: Media Files & Mirror Real Monitors | Extend the capture seam with media-file decode and mirroring a real physical monitor into a virtual slot; per-monitor content assignment. | Draft |
| 10.4 | RGB Peripherals & Audio in the Simulator | `VisualizationBackend` taps real per-LED colors; virtual peripherals rendered per `DevicePlacement` anchor; `SimulatedAudioCaptureService` drives the real audio path. RGB + audio validation, hardware-free. | Draft |
| 10.5 | Topology Editor, Scenario Library & Automation Hooks | Interactive editor (add/move/resize/rotate/DPI, source, per-monitor effect, "simulate display change"); curated JSON scenario library; headless render hook for future snapshot regression; `docs/SIMULATOR.md`. | Draft |
| 10.6 | Simulator Usability — Launcher, Linked-Window Shutdown, Readable Controls & a Pannable Canvas | Double-click launcher script (Debug + `--simulator`); closing either simulator/control window exits the whole session (dev-mode only); full dark-theme readability pass on the editor controls; pannable + wheel-zoomable canvas with a Fit reset (settings panel fixed). Dev/QA ergonomics only. | Ready for Review |

### 10.1 — Simulated Topology & Capture (Headless Engine Harness)
**As a** developer, **I want** the real engine to run against a fabricated monitor topology fed by
synthetic frames, gated to dev builds, **so that** the projection/relation/window-config pipeline can
be exercised on any layout without real monitors and without touching the shipped product.
- `SimulatedMonitorDetectionService : IMonitorDetectionService` serves a topology from a JSON scenario
  (count, position, resolution, primary, orientation); synthetic stable `Id`s and sentinel `HMonitor`
  (never reaches WGC because capture is also simulated). Can add/remove/resolution/orientation-mutate
  and fire `MonitorsChanged`, driving the real `EngineCoordinator.cs:1108-1141` re-sync path.
- `SimulatedScreenCaptureService : IScreenCaptureService` is a drop-in emitting test-pattern BGRA
  (animated gradient / bars / test card) at the source resolution, honoring the buffer-reuse +
  synchronous-consume contract and `maxFps`; correct on source switch; never throws (NFR5).
- Dev-only gating per Architecture; Release/Velopack build excludes it; zero licensing/packaging impact.
- JSON scenario format defined; the existing `SIM_MONITORS` arrangement reproduced as a fixture.
- Tests: xUnit unit tests (BGRA correctness, fixture load, `MonitorsChanged`) + a coordinator
  integration test asserting correct `WindowConfigPayload`/relations across several fabricated layouts.
- *Note:* effect windows are not rendered here — engine runs headless (surface creation suppressed in
  sim mode until 10.2); validated via `WindowConfigPayload` assertions.

### 10.2 — Composite Simulator Window (See the Effects)
**As a** developer/QA, **I want** every virtual monitor and its real effect composited into one
window laid out as the virtual desktop, **so that** I can visually verify projection and look across
any layout.
- `IEffectSurfaceHost` abstraction; `EffectWindow` implements it (production `SetWindowPos` path
  unchanged); `WebViewWindowManager` creates surfaces via injected factory. Bridge/coordinator untouched.
- `SimulatorEffectSurface : Control, IEffectSurfaceHost` hosts a child WebView2 running the **real**
  effect runtime; `RepositionTo` positions it in `SimulatorWindow`.
- `SimulatorWindow` renders the virtual desktop to scale (negative coords, gaps, mixed sizes
  preserved); source content drawn as each viewport's background, real effect on top.
- Topology/scenario change re-lays-out and re-orients effects with no restart; per-monitor effects work.
- Fidelity guardrail: surfaces get the real `WindowConfigPayload` and unmodified
  `monitorProjection.ts`; canvas scaling is the only adaptation.
- Supports 1–6 monitors; documents/guards the multi-WebView2 GPU-resource ceiling.

### 10.3 — Content Sources: Media Files & Mirror Real Monitors
**As a** developer/QA, **I want** to fill virtual monitors with a video/image or a mirror of a real
monitor, **so that** effects can be judged on realistic content, not just test patterns.
- Capture seam extended with a media-file source (decode → scale/letterbox → loop) and a mirror
  source (real WGC capture of a chosen physical monitor re-emitted into a virtual source slot — the
  one place real capture runs through the simulator).
- Per-monitor content assignment UI; only the engine's single source monitor drives the effect (others
  are visual context — documented to match real single-source behavior). Source change without full
  restart.
- Tests: media decode → BGRA correctness; mirror passthrough; graceful missing-file / unavailable-monitor.

### 10.4 — RGB Peripherals & Audio in the Simulator
**As a** developer/QA, **I want** virtual RGB peripherals and audio reactivity driven by the real
engine across the virtual layout, **so that** position-mapped LED output and audio behavior are
verifiable without hardware or sound.
- `VisualizationBackend : IRgbDeviceBackend` taps `Apply()` for real per-device per-LED sRGB colors
  from the real `LedProjection`; uses the simulated device set (keyboard/mouse/strip).
- Virtual peripherals rendered around the layout per each device's `DevicePlacement` anchor
  (auto/left/right/above/below/behind/surround) with live colors; anchor/flip/brightness visible.
- `SimulatedAudioCaptureService : IAudioCaptureService` emits synthetic bands/intensity (sine sweep /
  file / the 124 bpm `makeSimFrame` pattern ported to C#) on the real cadence; real audio path runs.
- Premium gating respected (RGB is Premium per Epic 9): sim activates a sim-Premium entitlement so the
  gated path is exercised (mirrors the web simulator's `?premium=1`).
- Tests: per-LED color correctness for known frames/placements; in-range synthetic bands on cadence.

### 10.5 — Topology Editor, Scenario Library & Automation Hooks
**As a** developer/QA, **I want** to author and save monitor layouts interactively and replay a
library of standard ones, **so that** I can sweep the configurations the product must support and seed
future automated regression.
- Interactive editor: add/remove monitors; set position (drag + numeric), resolution, orientation,
  DPI/scale, primary; pick source; per-monitor effect; FPS; "simulate display change" button (driving
  the 10.1 path).
- Curated named JSON scenario library: 3-wide, L-shape, vertical stack, portrait-flanked, mixed-DPI,
  gapped, dense 6-grid; save/load custom; existing `SIM_MONITORS` reproduced.
- Automation hook: a headless entry point that runs a scenario and renders/captures the composite (or
  per-surface) output to image(s) — the seam a future CI snapshot suite diffs. Deterministic output for
  ≥1 scenario; the full CI golden-image suite is **out of scope**.
- `docs/SIMULATOR.md`: launch, edit, scenario library, content sources, RGB/audio viz, and known
  fidelity limitations.

### 10.6 — Simulator Usability — Launcher, Linked-Window Shutdown, Readable Controls & a Pannable Canvas
**As a** developer/QA using the simulator, **I want** a one-double-click launch, a session whose two
windows close together, readable editor controls, and a pannable/zoomable canvas with the settings
fixed, **so that** the tool is fast to start and comfortable to drive — without touching the pipeline it
tests or the shipped product. (Added 2026-06-13 from owner ergonomics feedback after 10.1–10.5 shipped.)
- **Launcher (owner: script).** A repo-root `run-simulator.ps1` + double-clickable `run-simulator.cmd`
  that builds the **Debug** engine and starts it with `--simulator` (no typed args/env); optional
  scenario preselect. Lives beside `build.ps1`; hard-coded `-c Debug` so it can never start a build the
  signed Release doesn't contain.
- **Linked-window shutdown (owner: close both & exit).** In simulator mode, closing **either** the
  `SimulatorWindow` or the `ControlWindow` cleanly shuts the whole session down
  (`Application.Current.Shutdown()` → existing `App.OnExit` teardown), no orphan window / tray remnant;
  guarded to fire once. Production tray-first behavior (`ShutdownMode=OnExplicitShutdown`,
  `ControlWindow` hide-to-tray) is unchanged — the override is dev-mode only.
- **Readable controls (owner: full theme pass).** Every editor control — buttons (the black/no-fill
  bug), dropdowns, the monitor list (light-on-white bug), text boxes, checkbox — gets consistent
  readable light-on-dark styling across normal/hover/focus/selected/disabled. Defined once, not ad hoc.
- **Pan + zoom + Fit (owner).** The virtual-desktop canvas pans by dragging empty background and zooms
  with the wheel (zoom-to-cursor, clamped); a **Fit** control resets to the auto-fit scale + clears pan.
  Pan/zoom is **folded into the existing `Reflow` scale/offset math** (not a `RenderTransform`) so the
  windowed WebView2 surfaces stay correctly placed; the editor panel and warning text stay screen-fixed.
- Fidelity invariant upheld (presentation-only; real `WindowConfigPayload`, no bridge change); all behind
  the 10.1 dev gate; Release strips it; xUnit + Vitest stay green (adds pan/zoom geometry + shutdown-guard
  tests). `docs/SIMULATOR.md` updated.

## Cross-cutting requirements / guardrails
- **Never in the signed build.** Every story's code + UI entry sits behind `SIMULATOR_ENABLED` +
  the runtime gate from 10.1. Release publish must contain no simulator IL; no Free/Premium impact.
- **Reuse the real pipeline.** No forking of projection / effect / LED / audio logic — fidelity is the
  whole point of choosing the integrated approach.
- **Bridge contract unchanged.** No new C#/TS bridge drift; if any payload must change, mirror it in
  `src/Engine/Bridge/` per the standing Epic 7/8 guardrail.
- **Tests stay green.** xUnit + Vitest suites green; each story adds its own coverage.
- **Leave `web/src/shared/simulator.ts` alone.** That browser fake-bridge serves engine-free effect
  dev; this epic complements it. Reuse its synthetic-pattern and monitor-preset *ideas* in C# rather
  than coupling to it.

## Out of scope
- **Real-hardware fidelity for capture & placement.** The composite *approximates* OS window
  placement and per-monitor-V2 DPI; synthetic/media frames bypass WGC (only "mirror" exercises real
  capture). This tool does not replace owner verification on real extra monitors.
- **Indirect Display Driver path.** Considered and declined for this epic; remains the future option
  if true OS-level extra-monitor capture/placement fidelity is ever required.
- **Full CI golden-image regression suite.** Only the headless render hook (10.5) is in scope.

## Change Log
| Date | Version | Description | Author |
|------|---------|-------------|--------|
| 2026-06-13 | 1.0 | Epic drafted. Owner decisions locked: in-app integrated simulator (not IDD, not web-only), synthetic+media+mirror content, validate all four (projection/look/RGB/audio), dev/QA-only behind `SIMULATOR_ENABLED`, deliverable = manual tool + automation hooks. Five seams confirmed feasible (all Medium). | Kirk + Claude |
| 2026-06-13 | 1.1 | Added Story 10.6 (Simulator Usability) from owner ergonomics feedback after 10.1–10.5 shipped: double-click launcher script, linked-window shutdown (close either window → exit, dev-mode only), full dark-theme readability pass on the editor controls, and a pannable + wheel-zoomable canvas with a Fit reset (settings fixed). Dev/QA ergonomics only; fidelity invariant + dev-only gate + zero packaging impact preserved. | Kirk + Claude |
