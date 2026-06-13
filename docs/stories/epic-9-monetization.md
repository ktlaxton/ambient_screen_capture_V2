# Epic 9: Monetization (Free + Premium)

## Status
Implemented 2026-06-13 — 9.1, 9.2, 9.3 engineering complete, adversarially reviewed, and green
(367 xUnit + 281 Vitest). 9.4 is the human go-live checklist (owner actions: real signing cert,
storefront account, prices, purchase URL). No ads — paid unlock only (see decision below).
A multi-agent review (21 candidate findings → 6 confirmed → all fixed) hardened the expiry clock
(rollback floor), capped key length, and brought the simulator's gates in line with the engine.

## Context
Through Epic 8 AmbientFx is a complete desk-wide Ambilight: multi-monitor on-screen glow plus
RGB peripheral output, packaged as a signed Velopack installer with auto-update (Epics 7–8).
This epic turns it into a product: a **free tier** that's genuinely useful (single-monitor
ambilight + core effects) and a **Premium unlock** (every monitor, the full effect library,
per-monitor effects, and the whole RGB-peripheral feature).

## Decision: paid unlock, NOT ad-supported
The original ask floated "free (ad supported) + premium (paid)". We deliberately ship
**feature-limited free + one-time paid unlock, with no ads**:
- **No viable desktop ad supply.** AdSense forbids embedded-WebView/desktop placement;
  AdMob is mobile-only. What's left pays poorly and looks cheap.
- **Trust.** AmbientFx captures the screen continuously. Bundling a third-party ad SDK into a
  screen-capture app is a privacy story not worth defending.
- **Compliance for nothing.** Ads would add GDPR/consent burden for negligible desktop revenue.
The free tier is the marketing; the desk-wide RGB takeover is the demo that converts.

## Architecture
- **Offline, signed license keys — no server, no accounts.** Keys are minted by the owner's
  private ECDSA P-256 key and verified in-app against an embedded public key
  (`LicenseValidator`). Format `AFX1.<base64url(payload)>.<base64url(sig)>`. A buyer pastes a
  key; activation is pure local crypto. No phone-home, works offline, nothing to operate.
- **Engine is the source of truth.** Every gate is enforced in `EngineCoordinator`
  (`Entitlements`); the web UI mirror (`premium.ts`) only drives UX (lock badges, upsells).
  Editing the web bundle cannot unlock anything — the engine re-checks and refuses.
- **One binary, key-unlocked.** Everyone runs the same installer/update feed; a key flips the
  entitlement. The license key persists in `ApplicationSettings.LicenseKey` and is re-validated
  on every launch.
- **Merchant of record for fulfillment.** Paddle or Lemon Squeezy handles checkout + VAT/sales
  tax and calls a webhook that mints a key via `tools/license/new-license.ps1`
  (see `docs/MONETIZATION.md`).

## Tier policy (`Entitlements.cs` ↔ `premium.ts`, kept in lockstep)
| Capability | Free | Premium |
|---|---|---|
| Target monitors (on-screen glow) | 1 | unlimited |
| Effects | edge-glow, plasma, aurora, particles | full library |
| Per-monitor effect overrides | — | ✓ |
| RGB peripherals (Epic 8, all vendors + audio-reactive) | — | ✓ |
| Source selection, global controls, presets, hotkeys, updates | ✓ | ✓ |

## Stories
| # | Title | Summary | Status |
|---|-------|---------|--------|
| 9.1 | [Offline License Key System](9.1.story.md) | Signed-key mint/verify, `ILicenseService`, persistence, startup validation. | Done |
| 9.2 | [Feature Gating & Premium UI](9.2.story.md) | Engine enforcement at every gate + UI lock badges/upsells, license panel, activation. | Done |
| 9.3 | [Fulfillment & Storefront](9.3.story.md) | `new-license.ps1` minting tool + storefront/webhook integration docs. | Done (tooling); owner wires the store. |
| 9.4 | [Release / Go-Live Checklist](9.4.story.md) | The switch-flip checklist: cert, store, prices, purchase URL, final verification. | Owner checklist |

## Out of scope (this epic)
- Trials/grace periods, subscription enforcement beyond a key expiry date, per-seat activation
  limits, and any license server — all deliberately avoided for the offline model. Revisit only
  if piracy/refund-abuse data justifies the operational cost.

## Change Log
| Date | Version | Description | Author |
|------|---------|-------------|--------|
| 2026-06-13 | 1.0 | Epic created and 9.1–9.3 implemented; paid-unlock (no ads) decision recorded. | Kirk + Claude |
