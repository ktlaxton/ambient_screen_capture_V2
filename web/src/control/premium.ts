// ============================================================================
// Free-vs-Premium policy for the control UI (Epic 9 / Story 9.2). This is the
// MIRROR of src/Engine/Licensing/Entitlements.cs — treat the two as one versioned
// contract. The engine is the source of truth and re-enforces every gate; this
// file only drives UX (lock badges, upsells, disabled controls), so editing the
// web bundle can never unlock a feature — the engine still refuses.
// ============================================================================
import { useControlStore } from './store';

/** Effects available without a license. MUST match Entitlements.FreeEffects (C#). */
export const FREE_EFFECTS = ['edge-glow', 'plasma', 'aurora', 'particles'] as const;

/** Where to send users who want to upgrade (shown in the license panel + upsells). */
export const PURCHASE_URL = 'https://ambientfx.app/buy';

export function isEffectFree(effectId: string): boolean {
  return (FREE_EFFECTS as readonly string[]).includes(effectId);
}

export function effectAllowed(effectId: string, premium: boolean): boolean {
  return premium || isEffectFree(effectId);
}

export function maxTargetMonitors(premium: boolean): number {
  return premium ? Infinity : 1;
}

/** Per-monitor effect overrides (vs one global effect) are premium-only. */
export function perMonitorEffects(premium: boolean): boolean {
  return premium;
}

/** The whole Epic 8 ambient-RGB-peripherals feature is premium-only. */
export function rgbPeripherals(premium: boolean): boolean {
  return premium;
}

/** Reactive hook: true when the current entitlement is Premium. */
export function usePremium(): boolean {
  return useControlStore((s) => s.license.isPremium);
}
