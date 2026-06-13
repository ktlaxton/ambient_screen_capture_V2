// Shared param/audio helpers (Story 7.2, AC4): every effect used to redefine
// these — new code should import from here instead.
import type { EffectParams } from '../../shared/bridge';

export const clamp = (x: number, lo: number, hi: number): number =>
  x < lo ? lo : x > hi ? hi : x;

export function readNumber(params: EffectParams, key: string, fallback: number): number {
  const v = params[key];
  return typeof v === 'number' && Number.isFinite(v) ? v : fallback;
}

export function readString(params: EffectParams, key: string, fallback: string): string {
  const v = params[key];
  return typeof v === 'string' ? v : fallback;
}

export function readBoolean(params: EffectParams, key: string, fallback: boolean): boolean {
  const v = params[key];
  return typeof v === 'boolean' ? v : fallback;
}

/** Average of bands over the fractional index range [f0, f1] (length varies 8-16). */
export function bandAvg(bands: number[], f0: number, f1: number): number {
  const n = bands.length;
  if (n === 0) return 0;
  const lo = Math.round(f0 * (n - 1));
  const hi = Math.max(lo, Math.round(f1 * (n - 1)));
  let sum = 0;
  for (let i = lo; i <= hi; i++) sum += clamp(bands[i] ?? 0, 0, 1);
  return sum / (hi - lo + 1);
}

/** dt-scaled exponential smoothing factor (dt and tau in the same unit). */
export const easeFactor = (dt: number, tau: number): number => 1 - Math.exp(-dt / tau);

/** '#rrggbb' -> [r,g,b] 0..1; malformed strings return the fallback. */
export function hexToRgb01(
  hex: string,
  fallback: [number, number, number] = [1, 1, 1],
): [number, number, number] {
  const m = /^#([0-9a-fA-F]{6})$/.exec(hex);
  if (!m) return [fallback[0], fallback[1], fallback[2]];
  const v = parseInt(m[1], 16);
  return [((v >> 16) & 255) / 255, ((v >> 8) & 255) / 255, (v & 255) / 255];
}
