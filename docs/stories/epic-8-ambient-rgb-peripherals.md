# Epic 8: Ambient RGB Peripheral Output

## Status
Complete (2026-06-12) — all four stories Done. LGPL sign-off: GO. The packaged, signed
2.1.0 build is installed and verified on owner hardware. Tracked carry-overs: the
clean-machine sandbox run (`tools/sandbox-verify/`), the 8.3 non-Corsair hardware check
(when such gear exists), and the production signing certificate (Story 7.4).

## Context
Through Epic 7 the app is a complete on-screen Ambilight: it captures the source monitor, computes
per-edge zone colors (`EdgeColors` — top/bottom/left/right strips), and renders glow on adjacent
**monitors** (Story 7.5 made that mapping position-accurate). Every frame the engine already
produces `FramePayload.Edges` and fans it out to the effect windows
(`EngineCoordinator.OnFrameReady` → `_windowManager.PostToAll`).

This epic adds a **second consumer of that same data**: physical RGB peripherals. The colors are
already computed — the new work is a device output pipeline that maps those edge zones onto the
LEDs of keyboards, mice, light strips, fans, etc., so the ambient light spills off the screen onto
real hardware on the desk. It is the physical-world sibling of Story 7.5's monitor mapping.

## Goal
Owner wants the ambient effect to **extend onto Corsair RGB hardware** (and, by design, other
vendors later) so peripherals glow with the matching edge of the screen — a desk-wide Ambilight,
not just a multi-monitor one.

## Chosen approach (from owner research session, 2026-06-12)
- **Integration layer: [RGB.NET](https://github.com/DarthAffe/RGB.NET)** (`RGB.NET.Devices.Corsair`,
  NuGet, **LGPL-2.1**). .NET-native, wraps the iCUE SDK, exposes per-LED positions, and is
  multi-vendor — so the same pipeline reaches Razer/Logitech/ASUS/etc. with little extra work.
  Chosen over hand-written P/Invoke against the native iCUE SDK v4 C API for speed and breadth.
- **Vendor-neutral from the start**, Corsair shipped first: the device-sink abstraction is generic;
  only the Corsair provider is enabled and verified in Epic 8's first pass.
- **Position-mapped** (not dominant-color): each device's LED `(x,y)` positions are projected onto
  the screen-edge zones, mirroring `MonitorLayout`'s spatial philosophy.
- **Hardware available**: the owner has Corsair devices + iCUE installed, so stories carry real
  on-device verification (not build-blind).

## Hard dependencies & constraints (apply to every story)
- **iCUE must be installed and running** (≥ 4.31; current 5.46). RGB.NET's Corsair provider talks
  to hardware *through* the iCUE service. No iCUE → the feature is cleanly unavailable, never a crash.
- **User must enable third-party control** in iCUE settings, or the session is refused. The UI must
  explain this and surface the not-running / refused / no-devices states (NFR5 — never take the host
  down; mirror the defensive posture of the capture/audio services).
- **Release control on shutdown.** When the app exits or the feature is disabled, hand lighting back
  to iCUE so the user's normal profiles resume. This pairs with Story 7.3's reliable-shutdown work.
- **LGPL-2.1 redistribution.** RGB.NET is dynamically linked via NuGet (relinkable, unmodified) —
  generally compatible with a closed-source app, but **must be reviewed before the 7.4 installer
  ships it.** Document the license and keep the assembly replaceable.
- **Bridge contract is versioned:** any new `ApplicationSettings` field for device config must be
  mirrored in `src/Engine/Models/ApplicationSettings.cs` ↔ `web/src/shared/bridge.ts` and survive
  back-compat (unknown keys ignored, missing keys default — the Story 7.2 pattern).
- **Performance:** do not blindly push every 60 fps frame to hardware. Use RGB.NET's own update
  trigger / rate limiting; peripherals update far slower than the screen.

## Stories
| # | Title | Summary | Depends on |
|---|-------|---------|------------|
| 8.1 | [Position-Mapped RGB Peripheral Output](8.1.story.md) | The full vertical slice: connect to iCUE via RGB.NET, discover devices, project edge zones onto LED positions, push colors at a sane rate, release on shutdown, master enable/brightness + device list in the UI. Automatic mapping (no per-device manual placement yet). | — |
| 8.2 | [Per-Device Placement & Tuning UI](8.2.story.md) | Manual override of where each device sits relative to the screen, per-device brightness/enable, save/restore in presets. | 8.1 |
| 8.3 | [Additional Vendors & Audio-Reactive Peripherals](8.3.story.md) | Light up non-Corsair providers; optional audio-reactive peripheral mode reusing `FramePayload.Audio`. | 8.1 (pairs with 8.2) |
| 8.4 | [Installer & Licensing Hardening](8.4.story.md) | LGPL review sign-off, bundle/relink strategy in the Velopack installer, graceful behavior when iCUE is absent on a clean machine. | 8.1–8.3, 7.4 |

## Suggested sequencing
**8.1 first** (everything depends on the device service + projection). Then **8.2** (placement) and
**8.3** (vendors + audio) in either order — they're independent slices on top of 8.1. **8.4 ships
last** so the installer bundles the final device pipeline and the LGPL sign-off gates release.

## Out of scope / non-goals (this epic, first pass)
- No change to the capture/processing pipeline — `FramePayload`/`EdgeColors` are consumed as-is.
- No per-key effects or macro/key-event listening (lighting output only).
- Non-Corsair providers are designed-for but not enabled/verified in 8.1.

## Change Log
| Date | Version | Description | Author |
|------|---------|-------------|--------|
| 2026-06-12 | 1.0 | Epic created from owner research session; approach (RGB.NET, vendor-neutral, position-mapped, hardware-verified) locked. | Kirk + Claude |
