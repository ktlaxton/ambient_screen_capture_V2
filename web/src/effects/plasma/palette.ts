// Palette plumbing for Plasma Flow: gradient stops sampled from the frame's
// perimeter edge colors (near edge -> left stops, top/bottom -> middle stops,
// far edge -> right stops, dominant anchored at the midpoint), baked into a
// 256x1 RGBA LUT. Stops are eased on the CPU between frames so the gradient
// morphs smoothly instead of popping.
import * as THREE from 'three';
import type { EdgeColors, RGB } from '../../shared/bridge';

export const STOP_COUNT = 7;
/** Fixed LUT positions of the 7 stops; dominant anchors the midpoint (0.5). */
export const STOP_POSITIONS: readonly number[] = [0, 0.18, 0.36, 0.5, 0.64, 0.82, 1];
export const LUT_SIZE = 256;

/** #05070d — the deep-shadow tint (normalized 0..1). */
export const SHADOW_RGB: readonly [number, number, number] = [5 / 255, 7 / 255, 13 / 255];

const clamp01 = (x: number): number => (x < 0 ? 0 : x > 1 ? 1 : x);

/** Sample an edge array (RGB 0-255) at fraction f with linear interpolation; 0..1 out. */
function sampleEdge(edge: RGB[], f: number): [number, number, number] {
  const n = edge.length;
  if (n === 0) return [0, 0, 0];
  const x = clamp01(f) * (n - 1);
  const i0 = Math.floor(x);
  const i1 = Math.min(n - 1, i0 + 1);
  const t = x - i0;
  const a = edge[i0];
  const b = edge[i1];
  return [
    (a[0] + (b[0] - a[0]) * t) / 255,
    (a[1] + (b[1] - a[1]) * t) / 255,
    (a[2] + (b[2] - a[2]) * t) / 255,
  ];
}

function writeStop(out: Float32Array, index: number, r: number, g: number, b: number): void {
  out[index * 3] = clamp01(r);
  out[index * 3 + 1] = clamp01(g);
  out[index * 3 + 2] = clamp01(b);
}

/**
 * Build the 7 target stop colors (0..1, packed rgb) from one frame.
 * `mirror` flips which screen edge feeds which end of the gradient (used when
 * the effect monitor sits to the LEFT of the source, for spatial continuity).
 */
export function computeStops(
  edges: EdgeColors,
  dominant: RGB,
  mirror: boolean,
  out: Float32Array,
): void {
  const near = mirror ? edges.right : edges.left;
  const far = mirror ? edges.left : edges.right;

  // Stop 0: near edge pulled deep toward the shadow tint — anchors the LUT's dark end.
  const c0 = sampleEdge(near, 0.3);
  writeStop(
    out,
    0,
    SHADOW_RGB[0] + (c0[0] - SHADOW_RGB[0]) * 0.35,
    SHADOW_RGB[1] + (c0[1] - SHADOW_RGB[1]) * 0.35,
    SHADOW_RGB[2] + (c0[2] - SHADOW_RGB[2]) * 0.35,
  );

  const c1 = sampleEdge(near, 0.7);
  writeStop(out, 1, c1[0], c1[1], c1[2]);

  const c2 = sampleEdge(edges.top, 0.33);
  writeStop(out, 2, c2[0], c2[1], c2[2]);

  // Stop 3: dominant color anchors the midpoint.
  writeStop(out, 3, dominant[0] / 255, dominant[1] / 255, dominant[2] / 255);

  const c4 = sampleEdge(edges.bottom, 0.67);
  writeStop(out, 4, c4[0], c4[1], c4[2]);

  const c5 = sampleEdge(far, 0.3);
  writeStop(out, 5, c5[0], c5[1], c5[2]);

  // Stop 6: far edge slightly lifted — gives the LUT's bright end some bloom headroom.
  const c6 = sampleEdge(far, 0.75);
  writeStop(out, 6, c6[0] * 1.18, c6[1] * 1.18, c6[2] * 1.18);
}

/** Moody indigo/violet seed palette shown before the first engine frame arrives. */
export function seedStops(out: Float32Array): void {
  const seed: ReadonlyArray<readonly [number, number, number]> = [
    [0.02, 0.027, 0.051],
    [0.07, 0.06, 0.18],
    [0.13, 0.1, 0.32],
    [0.24, 0.14, 0.45],
    [0.36, 0.18, 0.5],
    [0.5, 0.26, 0.52],
    [0.72, 0.42, 0.58],
  ];
  for (let i = 0; i < STOP_COUNT; i++) {
    const s = seed[i];
    writeStop(out, i, s[0], s[1], s[2]);
  }
}

/** Bake the eased stop colors into the 256x1 RGBA8 LUT (smoothstep between stops). */
export function bakeLut(stops: Float32Array, out: Uint8Array): void {
  let seg = 0;
  for (let x = 0; x < LUT_SIZE; x++) {
    const f = x / (LUT_SIZE - 1);
    while (seg < STOP_COUNT - 2 && f > STOP_POSITIONS[seg + 1]) seg++;
    const p0 = STOP_POSITIONS[seg];
    const p1 = STOP_POSITIONS[seg + 1];
    let t = (f - p0) / Math.max(1e-6, p1 - p0);
    t = clamp01(t);
    t = t * t * (3 - 2 * t);
    const a = seg * 3;
    const b = (seg + 1) * 3;
    const o = x * 4;
    out[o] = Math.round(clamp01(stops[a] + (stops[b] - stops[a]) * t) * 255);
    out[o + 1] = Math.round(clamp01(stops[a + 1] + (stops[b + 1] - stops[a + 1]) * t) * 255);
    out[o + 2] = Math.round(clamp01(stops[a + 2] + (stops[b + 2] - stops[a + 2]) * t) * 255);
    out[o + 3] = 255;
  }
}

/** Create the 256x1 RGBA palette texture wrapping `data` (LinearFilter + clamp). */
export function createPaletteTexture(data: Uint8Array): THREE.DataTexture {
  const tex = new THREE.DataTexture(data, LUT_SIZE, 1, THREE.RGBAFormat, THREE.UnsignedByteType);
  tex.magFilter = THREE.LinearFilter;
  tex.minFilter = THREE.LinearFilter;
  tex.wrapS = THREE.ClampToEdgeWrapping;
  tex.wrapT = THREE.ClampToEdgeWrapping;
  tex.generateMipmaps = false;
  tex.needsUpdate = true;
  return tex;
}
