// premium.test.ts — the free-vs-premium policy mirror (Epic 9 / Story 9.2). These MUST
// stay in lockstep with src/Engine/Licensing/Entitlements.cs (the engine re-enforces).
import { describe, expect, it } from 'vitest';
import {
  FREE_EFFECTS,
  effectAllowed,
  isEffectFree,
  maxTargetMonitors,
  perMonitorEffects,
  rgbPeripherals,
} from './premium';

describe('free effects', () => {
  it('matches the engine free-effect list exactly', () => {
    // Mirror of Entitlements.FreeEffects — keep these in sync.
    expect([...FREE_EFFECTS]).toEqual(['edge-glow', 'plasma', 'aurora', 'particles']);
  });

  it('isEffectFree only accepts the four free effects', () => {
    for (const id of FREE_EFFECTS) expect(isEffectFree(id)).toBe(true);
    for (const id of ['nebula', 'fire', 'rain', 'waveform', 'kaleidoscope', 'ripple', 'audio-bars']) {
      expect(isEffectFree(id)).toBe(false);
    }
  });
});

describe('effectAllowed', () => {
  it('premium allows everything', () => {
    expect(effectAllowed('kaleidoscope', true)).toBe(true);
    expect(effectAllowed('edge-glow', true)).toBe(true);
  });
  it('free allows only free effects', () => {
    expect(effectAllowed('plasma', false)).toBe(true);
    expect(effectAllowed('nebula', false)).toBe(false);
  });
});

describe('other gates', () => {
  it('caps free target monitors at one, premium unlimited', () => {
    expect(maxTargetMonitors(false)).toBe(1);
    expect(maxTargetMonitors(true)).toBe(Infinity);
  });
  it('per-monitor effects and RGB peripherals are premium-only', () => {
    expect(perMonitorEffects(false)).toBe(false);
    expect(perMonitorEffects(true)).toBe(true);
    expect(rgbPeripherals(false)).toBe(false);
    expect(rgbPeripherals(true)).toBe(true);
  });
});
