# Epic 7: Expanded Graphics, Reliable Shutdown & Distributable Installer

## Status
Implemented — all four stories Ready for Review (2026-06-11). Remaining external items:
owner's code-signing certificate (7.4 AC5) and manual hardware/clean-VM verification passes
(7.1 AC7, 7.3 AC3, 7.4 AC2-6) — see each story's QA notes.

## Context
The AmbientFx rebuild (the WebView2 + React + three.js engine in `src/Engine/` and `web/`) is
complete through Phase 6 — 5 effects ship today (`edge-glow`, `plasma`, `audio-bars`,
`particles`, `aurora`), with presets, autostart, hotkeys, FPS cap, and settings persistence all
working. The two things deliberately left out were **packaging** and a **larger graphics library**.

This epic closes those gaps plus a lifecycle papercut the owner hit in real use. It is the
post-rebuild epic — Epics 1–3 in this folder describe the abandoned MVP (WPF/`AmbientEffectsEngine/`)
and do not apply; the rebuild itself was tracked as Phases 1–6 in `REBUILD_PRD_AND_ARCHITECTURE.md`.

## Goal
Owner wants: (1) **many more graphic options** — both new whole effects and richer controls on
existing ones; (2) the app **packaged as a real, installable, runnable executable**; and (3) the
ability to **fully shut it down from the taskbar** without a ghost process lingering.

## Stories
| # | Title | Summary | Depends on |
|---|-------|---------|------------|
| 7.1 | Effect Library Expansion | Add a batch of new self-contained effects (nebula, fire, rain, waveform, kaleidoscope, ripple), bringing the gallery to ~11. | — (can start now; pairs well after 7.2 infra) |
| 7.2 | Richer Effect Controls & Palettes | Extend the ParamDef control system with color/palette/blend-mode controls; retrofit the existing 5 effects; ship more default presets. | — |
| 7.3 | Reliable Shutdown & Taskbar Quit | Make closing from the taskbar actually quit (today it hides to tray); guarantee full process termination with no ghost process. | — |
| 7.4 | Distributable Signed Installer + Auto-Update | One-command build (web → wwwroot → publish), self-contained installer via Velopack, code signing, and auto-update. | 7.3 (ship correct lifecycle) |
| 7.5 | Position-Aware Edge Glow | Edge glow maps to the real Windows monitor arrangement — offsets, size differences, diagonals and gaps — with live re-layout on display changes. | — (added 2026-06-12, post-1.1) |

## Suggested sequencing
7.2 and 7.1 are the graphics pair — do **7.2 first** so new effects in 7.1 can reuse the shared
palette/blend-mode controls. 7.3 is independent and small; do it any time before 7.4. **7.4 ships
last** so the installer bundles the corrected shutdown behavior and the full effect library.

## Out of scope / explicit non-goals
- No new capture/audio pipeline work — the engine data contract (`FramePayload`) is unchanged.
- No MSIX Store submission this pass (Velopack sideload installer is the chosen path).
- No telemetry/analytics.

## Cross-cutting guardrails (apply to every story)
- **Effect-module contract (NFR8):** adding an effect = one folder under `web/src/effects/<id>/`
  plus a `registry.ts` import and a `manifest.json` entry. The vitest sync test
  `web/src/effects/registry.test.ts` must stay green (it asserts registry/manifest match
  field-for-field and enforces param hygiene).
- **Bridge contract is versioned:** any change to `web/src/shared/bridge.ts` must be mirrored in
  the matching C# types under `src/Engine/Bridge/` and `src/Engine/Models/ApplicationSettings.cs`.
- **Tests:** keep the existing xUnit + Vitest suites green; add coverage for new logic.

## Change Log
| Date | Version | Description | Author |
|------|---------|-------------|--------|
| 2026-06-11 | 1.0 | Epic drafted from owner change request for Mythos handoff | Kirk + Claude |
| 2026-06-11 | 1.1 | All four stories implemented (7.2 → 7.1 → 7.3 → 7.4); suites green (238 xUnit / 231 Vitest); installer pipeline smoke-verified | Claude (Fable 5) |
| 2026-06-12 | 1.2 | Story 7.5 (Position-Aware Edge Glow) added from owner request | Kirk + Claude |
