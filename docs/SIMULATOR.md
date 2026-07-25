# AmbientFx Layout Simulator (dev/QA)

The Layout Simulator (Epic 10) runs the **real** AmbientFx engine against a **fabricated** monitor
topology fed by mirrored / media / synthetic content, and composites every virtual monitor — plus its
effects and RGB peripherals — into a **single window**. It lets you test effects across the layouts the
product must support (3-wide, L-shape, vertical stack, portrait, mixed-DPI, gapped, a 6-panel wall) on a
2-monitor machine, with **no extra hardware and no driver installs**.

It is **dev/QA-only**: every line lives behind the `SIMULATOR_ENABLED` compile constant (defined in the
**Debug** configuration only) plus a runtime gate, so it is **compiled out of the signed Release/Velopack
build entirely** — zero Free/Premium, licensing, or packaging impact.

## Launch

**Easiest (recommended): double-click `run-simulator.cmd`** in the repo root. It builds the Debug engine
(the only configuration where the simulator exists), then launches it with `--simulator`.

**You land on "My real setup, mirrored":** the simulator detects your ACTUAL monitors, recreates them as
virtual twins on the canvas, and **live-mirrors each real screen onto its twin** — so the effects are
driven by what is really on your screen from the first second. From there you drag things around, add
fake monitors, swap content, and save the result as a preset. On a multi-monitor machine the simulator
window deliberately opens on a display **other than the one the source mirrors** (usually a side
monitor), so the source mirror runs immediately; whichever display does host the window has its own
twin's mirror **paused** — see the feedback-loop guard below.

```
run-simulator.cmd                 # build Debug + launch (lands on your mirrored real setup)
run-simulator.cmd six-grid        # preselect a curated template instead (see Presets)
./run-simulator.ps1 -NoBuild      # skip the build, relaunch the last Debug binary
./run-simulator.ps1 -Web          # also rebuild the web UI (vite -> src/Engine/wwwroot)
```

The launcher hard-codes `-c Debug`, so it can never start a build the signed Release installer contains.
A scenario name passed to it is forwarded via `AMBIENTFX_SIMULATOR_SCENARIO` (a curated name, a saved
preset's `.json` path, or any scenario file) — automation keeps a deterministic startup this way; the
real-setup clone is only the default when the variable is unset. The underlying gate is unchanged:
any Debug build started with `--simulator` (or `AMBIENTFX_SIMULATOR=1`) enters the simulator. Your real
`settings.json` is **never touched** — the simulator runs an in-memory, sim-Premium configuration and
never persists engine state; its only files are the presets you explicitly save (see below).

**One session, two windows.** You get the normal control window *and* the composite simulator window;
closing either one exits the whole dev session cleanly. Production tray behavior is unchanged.

## The two modes

Because each live effect viewport is a *windowed* WebView2 (it cannot be clicked through or drawn
over — "airspace"), the simulator has exactly two modes, toggled by the big accent button:

- **✏ Edit layout** (the mode you land in): effects are hidden; monitors are draggable boxes, clicking
  one opens its **card**, peripheral chips are draggable. This is where you build the bench.
- **▶ Preview effects**: the live composite — real WebGL effects, LED dots, audio reactivity. Leaving
  edit mode re-syncs the engine to the arranged layout automatically.

## Navigating the canvas

- **Pan** — drag an empty area (this also dismisses any open card).
- **Zoom** — mouse wheel, centered on the cursor (clamped ~0.2×–6×).
- **Fit** — toolbar button; resets zoom/pan to auto-fit the layout. Loading any preset/template re-fits.

Pan/zoom is presentation-only — the projection geometry is unaffected.

## The toolbar

| Control | What it does |
|---|---|
| **Presets ▾** | Save the current scene under a name; load one of *your* presets; load a curated template (`SIM_MONITORS`, `3-wide`, `L-shape`, `vertical-stack`, `portrait-flanked`, `mixed-dpi`, `gapped`, `six-grid`); **⟳ My real setup (mirrored)**; **▢ Blank slate**. |
| **+ Add monitor ▾** | Adds a monitor of the chosen dimensions (1920×1080 / 2560×1440 / 3840×2160 / portrait) at the right edge and selects it — drag it into place. |
| **✏ Edit layout / ▶ Preview effects** | The mode toggle (always offers the *other* mode). |
| **⚡ Display change** | Fires the simulated `MonitorsChanged` — the real hot-plug / resolution-change / source-lost coordinator path. |
| **FPS** | Global FPS ceiling (the real `setGlobal` command). |
| **Fit** | Reset pan/zoom. |

**Presets capture the whole scene**: monitor layout, each monitor's content and effect, the source
monitor, global effect/FPS, and every peripheral placement. They are plain scenario JSON (schema v2,
below) stored per-user at `%AppData%\AmbientFx\simulator\presets\*.json`, interchangeable with the
curated library and the automation hook. Old v1 scenario files load unchanged.

## Arranging monitors (drag, like Windows)

In edit mode every monitor is a draggable box — exactly like the Windows "Display arrangement" screen.
Edges **snap** to neighboring monitors (adjacency and alignment); negative coordinates and gaps are
valid. Dragging updates the live topology; the engine re-syncs once, when you switch to preview.

## The monitor card

**Click a monitor** (edit mode) and its card appears right next to it — everything about that monitor
in one place:

- **Monitor size & position** — common dimensions or custom W/H, exact X/Y (Enter applies),
  **Primary**, **Rotate** (swaps width/height; orientation is modeled as `width < height` by design).
  These are the monitor's *dimensions on the virtual desktop* — the simulator has no display-resolution
  or DPI concept, only where each monitor's rect sits and how big it is.
- **★ Set as source** — makes this monitor the one the engine captures. Every other monitor becomes a
  target (the simulator's standing rule), and the peripheral chips re-home around it.
- **Screen content** — what this monitor "shows":
  - **Mirror real display** — pick one of your actual displays; real WGC captures it live. This is the
    headline content source (your real screen drives the effect when the source monitor mirrors it).
  - **Picture / video** — an image, an image folder (looping sequence), or a video file (in-box WPF
    `MediaPlayer`, no ffmpeg). Picking the mode opens the browser immediately.
  - **Demo pattern** — animated `gradient`, static `bars`, or `testcard`.
  - **Blank** — opaque black.
- **Effect on this monitor** — the real effect catalog (11 effects, from `web/src/effects/manifest.json`,
  embedded Debug-only), applied via the real `setEffect` command; "(global default)" clears the
  per-monitor override. The simulator runs sim-Premium so every effect is testable.
- **Remove monitor**.

**Single-source reality (faithful, not a bug):** the engine captures exactly **one** source monitor and
every target projects from it. Content on non-source monitors is **visual context** for the composite.

## The mirror feedback-loop guard

Mirroring the physical display that hosts the simulator window would capture the window itself (hall of
mirrors). The simulator now handles this automatically: it watches which display the window sits on and
**pauses exactly those mirrors** (they fall back to the synthetic pattern; effects keep running). The
status line (top-left) and the monitor's backdrop label say so, and the *desired* mirror stays recorded —
**move the window to another display and the mirror resumes by itself.** Presets always save the desired
content, never the paused stand-in.

## RGB peripherals — drag chips onto real anchor zones

The simulator activates a **sim-Premium** entitlement so the gated RGB path runs. The three virtual
devices (keyboard / mouse / light strip) appear as chips of live LED dots whose colors are **recorded
from the real `LedProjection` output** — the simulator never computes colors itself.

- **Drag a chip** (edit mode) and the seven REAL anchor zones light up around the source monitor —
  `left` / `right` / `above` / `below` edge strips, `behind` (center), a `surround` outer band, and an
  `auto` corner slot. The zone you'd commit highlights as you move; **drop** to hot-apply the placement
  through the real `setDevicePlacement` model. Dropping anywhere else = `auto` (the product's default).
- **Click a chip** for its mini-card: anchor dropdown, **Flip**, per-device **Brightness**, **Enabled** —
  exactly the `DevicePlacement` fields the shipped product supports.
- **Distinct placements** (previously behind/surround collapsed to "below"): `behind` centers the dimmed
  chip ON the monitor; `surround` parks the chip at the corner and draws a **live perimeter ring** — one
  dot per LED, positioned exactly where the real surround projection samples that LED's color (same
  angle convention, flip included).

A `SimulatedAudioCaptureService` emits a synthetic 124 bpm "track" on the real cadence, so
audio-reactive effects and audio-modulated peripheral brightness run hardware- and sound-free.

## Scenario schema (v2)

Each preset/template is a `SimulatorScenario` (see `SimulatorScenario.cs`): top-level
`version, name, sourceMonitorId`, plus v2 fields `activeEffectId`, `globalMaxFps`, and
`devicePlacements` (stable device id → `{anchor, flip, brightness, enabled}`); per-monitor
`id, name, x, y, width, height, isPrimary, pattern, maxFps, scale`, optional `content`
(`synthetic` / `media` / `mirror` / `blank`), and the v2 `effect` (per-monitor override). Bounds are
virtual-desktop device pixels; negative x/y are valid. The loader is tolerant — v1 files simply have
the new fields null, and null v2 fields are omitted on save.

## Automation hook (headless render)

Unchanged from Story 10.5: a deterministic, GPU-free composite render of a scenario's synthetic content
at a pinned frame index, with the **real** `MonitorLayout.ComputeRelation` baked in as relation-colored
borders:

```
AmbientFx.exe --simulator-render <scenario-name-or-path> [--out <dir>]
```

Also callable programmatically (`SimulatorRenderHook.ComposeBgra` / `RenderComposite`). Saved presets
work here too (they are ordinary scenario files). A full CI golden-image suite remains out of scope.

## Known fidelity limitations

The simulator approximates — it does **not** replace owner verification on real extra monitors:

- **Synthetic & media frames bypass real WGC.** Only the **mirror** content source exercises real
  screen capture.
- **Viewports approximate OS window placement and per-monitor-V2 DPI.** One window + a uniform scale;
  the projection **geometry** is correct (rect-based on genuine virtual-desktop rects), but exact
  pixel/DPI look is not reproduced. The per-monitor `scale` field is an annotation.
- **WebView2 airspace.** Each viewport is a windowed WebView2 — the reason for the Edit/Preview split.
- **N mirrors = N real WGC sessions.** Cloning a many-monitor rig starts one capture per twin; on
  3+ monitor machines expect some GPU/CPU load in preview mode.
- **The Indirect Display Driver (IDD) path** — real phantom monitors with full capture/placement/DPI
  fidelity — was considered and declined for this epic. It remains the documented future option.

## Fidelity invariant

The simulator **reuses the real pipeline unmodified** — `EngineCoordinator`,
`MonitorLayout.ComputeRelation`, `monitorProjection.ts`, the effect runtime, `LedProjection`, and
`AudioModulation` are not forked, and there is **no bridge-contract change**. The simulator only swaps DI
seams (monitor detection, screen capture, audio capture, the RGB backend, the effect-surface factory,
and an in-memory settings/license overlay) and composites/records their output. All scene actions (source,
targets, effects, FPS, placements) go through the **real coordinator commands** via the injection seam.
Canvas scaling is the only rendering adaptation.
