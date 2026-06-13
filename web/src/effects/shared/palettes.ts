// Named fixed palettes (Story 7.2, AC1/AC4): the alternative color source to
// the live screen colors. ParamDef type 'palette' defaults must be one of
// these ids; the control UI renders the swatches from this registry.
import type { EffectParams } from '../../shared/bridge';
import { clamp } from './params';

/** Packed rgb triple, 0..1. */
export type RGB01 = readonly [number, number, number];

export interface PaletteDef {
  id: string;
  label: string;
  /** Evenly spaced gradient stops, dark-ish to bright-ish, length >= 2. */
  stops: readonly RGB01[];
}

export const PALETTES: readonly PaletteDef[] = [
  {
    id: 'warm',
    label: 'Warm',
    stops: [
      [0.05, 0.02, 0.02],
      [0.35, 0.08, 0.06],
      [0.72, 0.22, 0.08],
      [0.95, 0.45, 0.12],
      [1.0, 0.72, 0.35],
    ],
  },
  {
    id: 'cool',
    label: 'Cool',
    stops: [
      [0.02, 0.03, 0.08],
      [0.05, 0.12, 0.32],
      [0.1, 0.3, 0.6],
      [0.25, 0.55, 0.85],
      [0.6, 0.85, 1.0],
    ],
  },
  {
    id: 'neon',
    label: 'Neon',
    stops: [
      [0.06, 0.0, 0.12],
      [0.45, 0.05, 0.55],
      [0.9, 0.1, 0.65],
      [0.3, 0.55, 0.95],
      [0.2, 0.95, 0.9],
    ],
  },
  {
    id: 'mono',
    label: 'Mono',
    stops: [
      [0.02, 0.025, 0.04],
      [0.18, 0.2, 0.25],
      [0.45, 0.48, 0.55],
      [0.75, 0.78, 0.84],
      [0.97, 0.98, 1.0],
    ],
  },
  {
    id: 'sunset',
    label: 'Sunset',
    stops: [
      [0.1, 0.04, 0.22],
      [0.42, 0.1, 0.4],
      [0.85, 0.25, 0.35],
      [1.0, 0.55, 0.25],
      [1.0, 0.8, 0.5],
    ],
  },
  {
    id: 'ocean',
    label: 'Ocean',
    stops: [
      [0.01, 0.05, 0.1],
      [0.02, 0.2, 0.3],
      [0.05, 0.45, 0.5],
      [0.2, 0.7, 0.65],
      [0.65, 0.95, 0.85],
    ],
  },
  {
    id: 'forest',
    label: 'Forest',
    stops: [
      [0.02, 0.06, 0.03],
      [0.06, 0.22, 0.1],
      [0.15, 0.45, 0.18],
      [0.45, 0.7, 0.3],
      [0.85, 0.9, 0.55],
    ],
  },
  {
    id: 'aurora',
    label: 'Aurora',
    stops: [
      [0.02, 0.06, 0.08],
      [0.05, 0.35, 0.3],
      [0.15, 0.75, 0.5],
      [0.35, 0.6, 0.85],
      [0.6, 0.4, 0.9],
    ],
  },
  {
    id: 'ember',
    label: 'Ember',
    stops: [
      [0.03, 0.01, 0.0],
      [0.3, 0.04, 0.01],
      [0.75, 0.2, 0.02],
      [1.0, 0.55, 0.08],
      [1.0, 0.9, 0.45],
    ],
  },
] as const;

export const PALETTE_IDS: readonly string[] = PALETTES.map((p) => p.id);

const byId = new Map(PALETTES.map((p) => [p.id, p]));

export function isPaletteId(id: string): boolean {
  return byId.has(id);
}

export function getPalette(id: string): PaletteDef {
  return byId.get(id) ?? PALETTES[0];
}

/** Param read with palette-id validation (falls back when the id is unknown). */
export function readPaletteId(params: EffectParams, key: string, fallback: string): string {
  const v = params[key];
  return typeof v === 'string' && byId.has(v) ? v : fallback;
}

/** Linear sample of a palette's gradient at t (0..1). */
export function samplePalette(id: string, t: number): [number, number, number] {
  const stops = getPalette(id).stops;
  const x = clamp(t, 0, 1) * (stops.length - 1);
  const i0 = Math.floor(x);
  const i1 = Math.min(stops.length - 1, i0 + 1);
  const f = x - i0;
  const a = stops[i0];
  const b = stops[i1];
  return [
    a[0] + (b[0] - a[0]) * f,
    a[1] + (b[1] - a[1]) * f,
    a[2] + (b[2] - a[2]) * f,
  ];
}

/**
 * Bake `count` evenly spaced palette samples into a packed rgb Float32Array
 * (the shape every effect's CPU color-target buffers use). Writes into `out`
 * when given (length must be count*3), else allocates.
 */
export function paletteStops(id: string, count: number, out?: Float32Array): Float32Array {
  const dst = out ?? new Float32Array(count * 3);
  for (let i = 0; i < count; i++) {
    const [r, g, b] = samplePalette(id, count > 1 ? i / (count - 1) : 0.5);
    dst[i * 3] = r;
    dst[i * 3 + 1] = g;
    dst[i * 3 + 2] = b;
  }
  return dst;
}

/** CSS linear-gradient string for UI swatches. */
export function paletteCssGradient(id: string): string {
  const stops = getPalette(id).stops
    .map((s, i, arr) => {
      const pct = arr.length > 1 ? (i / (arr.length - 1)) * 100 : 50;
      const r = Math.round(s[0] * 255);
      const g = Math.round(s[1] * 255);
      const b = Math.round(s[2] * 255);
      return `rgb(${r},${g},${b}) ${pct.toFixed(0)}%`;
    })
    .join(', ');
  return `linear-gradient(90deg, ${stops})`;
}
