// Edge Glow — the flagship Ambilight effect (FR6, FR7, AC1).
// The window acts as a physical extension of the source screen: colored light
// spills in from the side facing the source and diffuses across the window.
// Single pass, fullscreen triangle, glow faked in-shader (no postprocessing).
import * as THREE from 'three';
import type { EffectParams, FramePayload, RGB } from '../../shared/bridge';
import type { EffectContext, EffectInstance, EffectModule } from '../types';
import { EDGE_GLOW_FS, FULLSCREEN_VS } from './shaders';

const ROWS = 4; // DataTexture rows: 0 top, 1 bottom, 2 left, 3 right
const COLOR_TAU_MS = 120; // snappy but not flickery (engine already smooths)
const AUDIO_TAU_MS = 90;
const MAX_ZONES = 64;

function asNumber(v: number | string | boolean | undefined, fallback: number): number {
  return typeof v === 'number' && Number.isFinite(v) ? v : fallback;
}

function clamp(v: number, lo: number, hi: number): number {
  return Math.min(hi, Math.max(lo, v));
}

/** Fractionally resamples a zone strip (RGB 0-255) onto `w` texels (0..1 floats). */
function resampleRow(zones: RGB[], out: Float32Array, offset: number, w: number): void {
  const len = zones.length;
  for (let x = 0; x < w; x++) {
    const o = offset + x * 3;
    if (len === 0) {
      out[o] = 0;
      out[o + 1] = 0;
      out[o + 2] = 0;
      continue;
    }
    const f = len === 1 ? 0 : (x / (w - 1)) * (len - 1);
    const i0 = Math.min(len - 1, Math.floor(f));
    const i1 = Math.min(len - 1, i0 + 1);
    const t = f - i0;
    const a = zones[i0];
    const b = zones[i1];
    out[o] = (a[0] + (b[0] - a[0]) * t) / 255;
    out[o + 1] = (a[1] + (b[1] - a[1]) * t) / 255;
    out[o + 2] = (a[2] + (b[2] - a[2]) * t) / 255;
  }
}

/** Mean of the low 25% of the bands array (bass energy), length-agnostic. */
function bassOf(bands: number[]): number {
  const len = bands.length;
  if (len === 0) return 0;
  const n = Math.max(1, Math.floor(len * 0.25));
  let sum = 0;
  for (let i = 0; i < n; i++) sum += bands[i];
  return clamp(sum / n, 0, 1);
}

class EdgeGlowInstance implements EffectInstance {
  private renderer: THREE.WebGLRenderer;
  private scene = new THREE.Scene();
  private camera = new THREE.OrthographicCamera(); // dummy; vertex shader ignores it
  private geometry: THREE.BufferGeometry;
  private material: THREE.ShaderMaterial;
  private texture: THREE.DataTexture | null = null;
  private texData: Uint8Array = new Uint8Array(0);

  /** Smoothed (current) and engine-fed (target) zone colors, 0..1, ROWS x zones x rgb. */
  private current = new Float32Array(0);
  private target = new Float32Array(0);
  private zones = 0;

  private audioCurrent = 0;
  private audioTarget = 0;
  private bassCurrent = 0;
  private bassTarget = 0;

  private readonly dprCap: number;

  constructor(ctx: EffectContext) {
    this.dprCap = ctx.preview ? 1 : 1.5;
    this.renderer = new THREE.WebGLRenderer({
      canvas: ctx.canvas,
      antialias: false,
      powerPreference: 'high-performance',
      alpha: false,
      stencil: false,
      depth: false,
    });

    // Layout awareness (FR7): which window side the light enters from, and
    // which source-edge row of the DataTexture feeds it. relation 'none'
    // (or missing windowConfig — previews/browser) => ambient halo mode.
    const relation = ctx.windowConfig?.relation ?? 'none';
    let mode = 0;
    let entry = 0; // 0=left 1=right 2=bottom 3=top
    let row = 0.875;
    const rowV = (r: number) => (r + 0.5) / ROWS;
    switch (relation) {
      case 'right': // window right of source -> light from the LEFT, source RIGHT edge
        entry = 0;
        row = rowV(3);
        break;
      case 'left':
        entry = 1;
        row = rowV(2);
        break;
      case 'above': // window above source -> light from the BOTTOM, source TOP edge
        entry = 2;
        row = rowV(0);
        break;
      case 'below':
        entry = 3;
        row = rowV(1);
        break;
      default:
        mode = 1;
    }

    this.material = new THREE.ShaderMaterial({
      defines: { TAPS: ctx.preview ? 2 : 3 }, // ~4x cheaper edge blur in gallery previews
      uniforms: {
        uTex: { value: null },
        uMode: { value: mode },
        uEntry: { value: entry },
        uRow: { value: row },
        uZones: { value: 1 },
        uReach: { value: 0.55 },
        uSpread: { value: 0.5 },
        uColorBoost: { value: 0.35 },
        uAudioPulse: { value: 0.35 },
        uAudio: { value: 0 },
        uBass: { value: 0 },
        uIntensity: { value: 1 },
        uBrightness: { value: 1 },
        uTime: { value: 0 },
      },
      vertexShader: FULLSCREEN_VS,
      fragmentShader: EDGE_GLOW_FS,
      depthTest: false,
      depthWrite: false,
    });

    // Fullscreen triangle (single 3-vertex draw; no quad seam).
    this.geometry = new THREE.BufferGeometry();
    this.geometry.setAttribute(
      'position',
      new THREE.BufferAttribute(new Float32Array([-1, -1, 0, 3, -1, 0, -1, 3, 0]), 3),
    );
    const mesh = new THREE.Mesh(this.geometry, this.material);
    mesh.frustumCulled = false; // no valid bounding volume in NDC
    this.scene.add(mesh);

    this.ensureField(8);
    this.resize(ctx.canvas.clientWidth || 1, ctx.canvas.clientHeight || 1);
  }

  /** (Re)allocates the zones x 4 RGBA color-field texture when zone count changes. */
  private ensureField(zones: number): void {
    const w = clamp(Math.floor(zones), 1, MAX_ZONES);
    if (w === this.zones) return;
    this.zones = w;
    this.current = new Float32Array(w * ROWS * 3);
    this.target = new Float32Array(w * ROWS * 3);
    this.texData = new Uint8Array(w * ROWS * 4);
    for (let i = 3; i < this.texData.length; i += 4) this.texData[i] = 255;

    this.texture?.dispose();
    const tex = new THREE.DataTexture(this.texData, w, ROWS, THREE.RGBAFormat, THREE.UnsignedByteType);
    tex.minFilter = THREE.LinearFilter; // free smooth blending between zones
    tex.magFilter = THREE.LinearFilter;
    tex.wrapS = THREE.ClampToEdgeWrapping;
    tex.wrapT = THREE.ClampToEdgeWrapping;
    tex.needsUpdate = true;
    this.texture = tex;
    this.material.uniforms.uTex.value = tex;
    this.material.uniforms.uZones.value = w;
  }

  onFrame(frame: FramePayload): void {
    const { top, bottom, left, right } = frame.edges;
    const w = Math.max(top.length, bottom.length, left.length, right.length, 1);
    this.ensureField(w);
    const stride = this.zones * 3;
    resampleRow(top, this.target, 0 * stride, this.zones);
    resampleRow(bottom, this.target, 1 * stride, this.zones);
    resampleRow(left, this.target, 2 * stride, this.zones);
    resampleRow(right, this.target, 3 * stride, this.zones);

    this.audioTarget = clamp(frame.audio.intensity, 0, 1);
    this.bassTarget = bassOf(frame.audio.bands);
  }

  render(timeMs: number, dtMs: number): void {
    const dt = clamp(dtMs, 0, 250);

    // dt-scaled exponential easing toward the latest engine frame keeps the
    // visuals silky even when the stream stutters or stops (then we hold the
    // last colors and the in-shader drift keeps the field alive from uTime).
    const kc = 1 - Math.exp(-dt / COLOR_TAU_MS);
    const cur = this.current;
    const tgt = this.target;
    const data = this.texData;
    for (let i = 0, p = 0; i < cur.length; i += 3, p += 4) {
      cur[i] += (tgt[i] - cur[i]) * kc;
      cur[i + 1] += (tgt[i + 1] - cur[i + 1]) * kc;
      cur[i + 2] += (tgt[i + 2] - cur[i + 2]) * kc;
      data[p] = (cur[i] * 255 + 0.5) | 0;
      data[p + 1] = (cur[i + 1] * 255 + 0.5) | 0;
      data[p + 2] = (cur[i + 2] * 255 + 0.5) | 0;
    }
    if (this.texture) this.texture.needsUpdate = true;

    const ka = 1 - Math.exp(-dt / AUDIO_TAU_MS);
    this.audioCurrent += (this.audioTarget - this.audioCurrent) * ka;
    this.bassCurrent += (this.bassTarget - this.bassCurrent) * ka;

    const u = this.material.uniforms;
    u.uAudio.value = this.audioCurrent;
    u.uBass.value = this.bassCurrent;
    u.uTime.value = timeMs / 1000;

    this.renderer.render(this.scene, this.camera);
  }

  setParams(params: EffectParams): void {
    const u = this.material.uniforms;
    u.uReach.value = clamp(asNumber(params.reach, 0.55), 0.2, 1);
    u.uSpread.value = clamp(asNumber(params.spread, 0.5), 0.1, 1);
    u.uAudioPulse.value = clamp(asNumber(params.audioPulse, 0.35), 0, 1);
    u.uColorBoost.value = clamp(asNumber(params.colorBoost, 0.35), 0, 1);
  }

  setGlobals(globals: { intensity: number; brightness: number }): void {
    this.material.uniforms.uIntensity.value = clamp(globals.intensity, 0, 1);
    this.material.uniforms.uBrightness.value = clamp(globals.brightness, 0, 1);
  }

  resize(width: number, height: number): void {
    const dpr = Math.min(typeof devicePixelRatio === 'number' ? devicePixelRatio : 1, this.dprCap);
    this.renderer.setPixelRatio(dpr);
    this.renderer.setSize(Math.max(1, width), Math.max(1, height), false);
  }

  dispose(): void {
    this.texture?.dispose();
    this.texture = null;
    this.geometry.dispose();
    this.material.dispose();
    this.renderer.dispose();
    this.renderer.forceContextLoss(); // release the WebGL context now (gallery churns contexts; Chromium caps ~16)
  }
}

const edgeGlow: EffectModule = {
  id: 'edge-glow',
  name: 'Edge Glow',
  description:
    "Ambilight-style glow that mirrors the source monitor's edge colors onto adjacent displays.",
  params: [
    { key: 'reach', label: 'Reach', type: 'range', min: 0.2, max: 1, step: 0.01, default: 0.55 },
    { key: 'spread', label: 'Spread', type: 'range', min: 0.1, max: 1, step: 0.01, default: 0.5 },
    { key: 'audioPulse', label: 'Audio pulse', type: 'range', min: 0, max: 1, step: 0.01, default: 0.35 },
    { key: 'colorBoost', label: 'Color boost', type: 'range', min: 0, max: 1, step: 0.01, default: 0.35 },
  ],
  create(ctx: EffectContext): EffectInstance {
    return new EdgeGlowInstance(ctx);
  },
};

export default edgeGlow;
