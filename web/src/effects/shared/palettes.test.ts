// Palette registry hygiene + sampling helpers (Story 7.2).
import { describe, expect, it } from 'vitest';
import {
  PALETTES,
  PALETTE_IDS,
  getPalette,
  isPaletteId,
  paletteCssGradient,
  paletteStops,
  readPaletteId,
  samplePalette,
} from './palettes';
import { hexToRgb01 } from './params';
import { STOP_COUNT, stopsFromPalette } from './screenLut';

describe('palette registry', () => {
  it('has unique ids and at least 2 stops per palette, all components in 0..1', () => {
    expect(new Set(PALETTE_IDS).size).toBe(PALETTES.length);
    for (const p of PALETTES) {
      expect(p.stops.length, p.id).toBeGreaterThanOrEqual(2);
      for (const [r, g, b] of p.stops) {
        for (const c of [r, g, b]) {
          expect(c, p.id).toBeGreaterThanOrEqual(0);
          expect(c, p.id).toBeLessThanOrEqual(1);
        }
      }
    }
  });

  it('isPaletteId / getPalette agree, with a stable fallback for unknown ids', () => {
    for (const p of PALETTES) {
      expect(isPaletteId(p.id)).toBe(true);
      expect(getPalette(p.id)).toBe(p);
    }
    expect(isPaletteId('nope')).toBe(false);
    expect(getPalette('nope')).toBe(PALETTES[0]);
  });

  it('readPaletteId validates against the registry', () => {
    expect(readPaletteId({ palette: 'warm' }, 'palette', 'cool')).toBe('warm');
    expect(readPaletteId({ palette: 'bogus' }, 'palette', 'cool')).toBe('cool');
    expect(readPaletteId({}, 'palette', 'cool')).toBe('cool');
    expect(readPaletteId({ palette: 42 }, 'palette', 'cool')).toBe('cool');
  });

  it('samplePalette interpolates endpoints exactly and clamps t', () => {
    const p = PALETTES[0];
    expect(samplePalette(p.id, 0)).toEqual([...p.stops[0]]);
    expect(samplePalette(p.id, 1)).toEqual([...p.stops[p.stops.length - 1]]);
    expect(samplePalette(p.id, -5)).toEqual([...p.stops[0]]);
    expect(samplePalette(p.id, 5)).toEqual([...p.stops[p.stops.length - 1]]);
  });

  it('paletteStops fills count*3 floats (allocating or in place)', () => {
    const a = paletteStops('cool', 8);
    expect(a.length).toBe(24);
    const buf = new Float32Array(24);
    const b = paletteStops('cool', 8, buf);
    expect(b).toBe(buf);
    expect([...b]).toEqual([...a]);
  });

  it('stopsFromPalette fills the shared 7-stop LUT buffer with anchored ends', () => {
    const out = new Float32Array(STOP_COUNT * 3);
    stopsFromPalette('warm', out);
    const [r0] = samplePalette('warm', 0);
    // Stop 0 is pulled toward the shadow tint, so it's darker than the raw sample.
    expect(out[0]).toBeLessThan(Math.max(r0, 0.05));
    for (const v of out) expect(Number.isFinite(v)).toBe(true);
  });

  it('paletteCssGradient produces a linear-gradient string for every palette', () => {
    for (const p of PALETTES) {
      const css = paletteCssGradient(p.id);
      expect(css).toMatch(/^linear-gradient\(90deg, rgb\(/);
      expect(css.split('rgb(').length - 1).toBe(p.stops.length);
    }
  });
});

describe('hexToRgb01', () => {
  it('parses #rrggbb and falls back on malformed input', () => {
    expect(hexToRgb01('#ff0000')).toEqual([1, 0, 0]);
    expect(hexToRgb01('#000000')).toEqual([0, 0, 0]);
    const [r, g, b] = hexToRgb01('#4f7cff');
    expect(r).toBeCloseTo(0x4f / 255);
    expect(g).toBeCloseTo(0x7c / 255);
    expect(b).toBeCloseTo(0xff / 255);
    expect(hexToRgb01('red', [0.5, 0.5, 0.5])).toEqual([0.5, 0.5, 0.5]);
    expect(hexToRgb01('#fff', [0.5, 0.5, 0.5])).toEqual([0.5, 0.5, 0.5]);
  });
});
