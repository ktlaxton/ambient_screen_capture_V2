// Plasma Flow (FR6): slow-breathing, domain-warped fbm plasma clouds rendered
// on a fullscreen triangle, colored by a palette LUT built from the screen's
// edge colors and stirred by the music (bass -> warp/flow boost, treble ->
// fine shimmer). Single pass, ALU-only: OCTAVES fbm + 1 palette fetch.
import * as THREE from 'three';
import type { EffectParams, FramePayload } from '../../shared/bridge';
import type {
  EffectContext,
  EffectInstance,
  EffectModule,
  GlobalRenderSettings,
} from '../types';
import {
  LUT_SIZE,
  STOP_COUNT,
  bakeLut,
  computeStops,
  createPaletteTexture,
  seedStops,
} from './palette';
import { FRAGMENT_SHADER, VERTEX_SHADER } from './shaders';

const DEFAULTS = { scale: 1.4, flowSpeed: 0.35, warp: 0.6, audioDrive: 0.5 } as const;

const clamp = (x: number, lo: number, hi: number): number => (x < lo ? lo : x > hi ? hi : x);

function readNumber(params: EffectParams, key: string, fallback: number): number {
  const v = params[key];
  return typeof v === 'number' && Number.isFinite(v) ? v : fallback;
}

/** Average of bands over the fractional index range [f0, f1] (length varies 8-16). */
function bandAvg(bands: number[], f0: number, f1: number): number {
  const n = bands.length;
  if (n === 0) return 0;
  const lo = Math.round(f0 * (n - 1));
  const hi = Math.max(lo, Math.round(f1 * (n - 1)));
  let sum = 0;
  for (let i = lo; i <= hi; i++) sum += clamp(bands[i] ?? 0, 0, 1);
  return sum / (hi - lo + 1);
}

/** dt-scaled exponential smoothing factor (dt and tau in seconds). */
const easeFactor = (dt: number, tau: number): number => 1 - Math.exp(-dt / tau);

interface PlasmaUniforms {
  uResolution: { value: THREE.Vector2 };
  uTime: { value: number };
  uFlow: { value: number };
  uScale: { value: number };
  uWarp: { value: number };
  uShimmer: { value: number };
  uBrightness: { value: number };
  uPalette: { value: THREE.DataTexture };
  [uniform: string]: THREE.IUniform;
}

class PlasmaInstance implements EffectInstance {
  private readonly renderer: THREE.WebGLRenderer;
  private readonly scene = new THREE.Scene();
  private readonly camera = new THREE.OrthographicCamera();
  private readonly geometry: THREE.BufferGeometry;
  private readonly material: THREE.ShaderMaterial;
  private readonly palette: THREE.DataTexture;
  private readonly lutData: Uint8Array;
  private readonly uniforms: PlasmaUniforms;
  private readonly dprCap: number;
  private readonly mirror: boolean;

  // Palette stops: eased current + frame targets (rgb packed, 0..1).
  private readonly stopsCur = new Float32Array(STOP_COUNT * 3);
  private readonly stopsTarget = new Float32Array(STOP_COUNT * 3);
  private lutDirty = true;

  // Audio (targets set in onFrame, eased in render).
  private bassTarget = 0;
  private bassCur = 0;
  private trebleTarget = 0;
  private trebleCur = 0;

  // Params (slider values eased so live tweaks don't pop).
  private scaleTarget: number = DEFAULTS.scale;
  private scaleCur: number = DEFAULTS.scale;
  private warpTarget: number = DEFAULTS.warp;
  private warpCur: number = DEFAULTS.warp;
  private flowSpeed: number = DEFAULTS.flowSpeed;
  private audioDrive: number = DEFAULTS.audioDrive;

  // Globals.
  private intensityTarget = 1;
  private intensityCur = 1;
  private brightnessTarget = 1;
  private brightnessCur = 1;

  private flowTime = 0;
  private cssWidth: number;
  private cssHeight: number;

  constructor(ctx: EffectContext) {
    this.dprCap = ctx.preview ? 1 : 1.5;
    this.mirror = ctx.windowConfig?.relation === 'left';

    seedStops(this.stopsCur);
    this.stopsTarget.set(this.stopsCur);
    this.lutData = new Uint8Array(LUT_SIZE * 4);
    bakeLut(this.stopsCur, this.lutData);
    this.palette = createPaletteTexture(this.lutData);

    this.renderer = new THREE.WebGLRenderer({
      canvas: ctx.canvas,
      antialias: false,
      powerPreference: 'high-performance',
      alpha: false,
      stencil: false,
      depth: false,
    });

    this.uniforms = {
      uResolution: { value: new THREE.Vector2(1, 1) },
      uTime: { value: 0 },
      uFlow: { value: 0 },
      uScale: { value: this.scaleCur },
      uWarp: { value: this.warpCur },
      uShimmer: { value: 0 },
      uBrightness: { value: this.brightnessCur },
      uPalette: { value: this.palette },
    };

    this.material = new THREE.ShaderMaterial({
      uniforms: this.uniforms,
      vertexShader: VERTEX_SHADER,
      fragmentShader: FRAGMENT_SHADER,
      defines: { OCTAVES: ctx.preview ? 3 : 5 },
      depthTest: false,
      depthWrite: false,
    });

    // Fullscreen triangle: 3 NDC vertices, overshoots and gets clipped.
    this.geometry = new THREE.BufferGeometry();
    this.geometry.setAttribute(
      'position',
      new THREE.BufferAttribute(new Float32Array([-1, -1, 0, 3, -1, 0, -1, 3, 0]), 3),
    );
    const mesh = new THREE.Mesh(this.geometry, this.material);
    mesh.frustumCulled = false; // no valid bounds in world space
    this.scene.add(mesh);

    this.cssWidth = ctx.canvas.clientWidth || ctx.canvas.width || 640;
    this.cssHeight = ctx.canvas.clientHeight || ctx.canvas.height || 360;
    this.resize(this.cssWidth, this.cssHeight);
  }

  onFrame(frame: FramePayload): void {
    computeStops(frame.edges, frame.dominant, this.mirror, this.stopsTarget);
    const bands = frame.audio.bands;
    this.bassTarget = clamp(bandAvg(bands, 0, 0.25) * (0.5 + 0.5 * frame.audio.intensity), 0, 1);
    this.trebleTarget = clamp(bandAvg(bands, 0.75, 1), 0, 1);
  }

  render(timeMs: number, dtMs: number): void {
    const dt = clamp(dtMs, 0, 100) / 1000;

    // Audio easing: fast attack so beats land, slower release so they bloom out.
    this.bassCur += (this.bassTarget - this.bassCur) *
      easeFactor(dt, this.bassTarget > this.bassCur ? 0.06 : 0.28);
    this.trebleCur += (this.trebleTarget - this.trebleCur) * easeFactor(dt, 0.12);

    // Param + global easing.
    const kParam = easeFactor(dt, 0.15);
    this.scaleCur += (this.scaleTarget - this.scaleCur) * kParam;
    this.warpCur += (this.warpTarget - this.warpCur) * kParam;
    this.intensityCur += (this.intensityTarget - this.intensityCur) * kParam;
    this.brightnessCur += (this.brightnessTarget - this.brightnessCur) * kParam;

    // Palette stop easing (slow morph between frame palettes); rebake LUT when it moved.
    const kStops = easeFactor(dt, 0.5);
    let maxDelta = 0;
    for (let i = 0; i < this.stopsCur.length; i++) {
      const d = (this.stopsTarget[i] - this.stopsCur[i]) * kStops;
      this.stopsCur[i] += d;
      const ad = Math.abs(d);
      if (ad > maxDelta) maxDelta = ad;
    }
    if (maxDelta > 0.0008 || this.lutDirty) {
      bakeLut(this.stopsCur, this.lutData);
      this.palette.needsUpdate = true;
      this.lutDirty = false;
    }

    // Bass raises warp amplitude + flow speed up to +60%, scaled by globals.intensity.
    const boost = 1 + 0.6 * this.bassCur * this.audioDrive * this.intensityCur;
    const motion = 0.15 + 0.85 * this.intensityCur;
    this.flowTime += dt * (0.06 + 1.7 * this.flowSpeed) * motion * boost;

    this.uniforms.uTime.value = timeMs / 1000;
    this.uniforms.uFlow.value = this.flowTime;
    this.uniforms.uScale.value = this.scaleCur;
    this.uniforms.uWarp.value = this.warpCur * boost;
    this.uniforms.uShimmer.value = clamp(
      this.trebleCur * 2 * this.audioDrive * this.intensityCur,
      0,
      1,
    );
    this.uniforms.uBrightness.value = this.brightnessCur;

    if (this.cssWidth > 0 && this.cssHeight > 0) {
      this.renderer.render(this.scene, this.camera);
    }
  }

  setParams(params: EffectParams): void {
    this.scaleTarget = clamp(readNumber(params, 'scale', DEFAULTS.scale), 0.5, 3);
    this.warpTarget = clamp(readNumber(params, 'warp', DEFAULTS.warp), 0, 1);
    this.flowSpeed = clamp(readNumber(params, 'flowSpeed', DEFAULTS.flowSpeed), 0, 1);
    this.audioDrive = clamp(readNumber(params, 'audioDrive', DEFAULTS.audioDrive), 0, 1);
  }

  setGlobals(globals: GlobalRenderSettings): void {
    this.intensityTarget = clamp(globals.intensity, 0, 1);
    this.brightnessTarget = clamp(globals.brightness, 0, 1);
  }

  resize(width: number, height: number): void {
    this.cssWidth = width;
    this.cssHeight = height;
    if (width <= 0 || height <= 0) return;
    const dpr = Math.min(
      typeof window !== 'undefined' ? window.devicePixelRatio || 1 : 1,
      this.dprCap,
    );
    this.renderer.setPixelRatio(dpr);
    this.renderer.setSize(width, height, false);
    this.uniforms.uResolution.value.set(width * dpr, height * dpr);
  }

  dispose(): void {
    this.geometry.dispose();
    this.material.dispose();
    this.palette.dispose();
    this.renderer.dispose();
    this.renderer.forceContextLoss(); // release the WebGL context now (gallery churns contexts; Chromium caps ~16)
  }
}

const plasma: EffectModule = {
  id: 'plasma',
  name: 'Plasma Flow',
  description: 'Slow-breathing plasma clouds tinted by the screen palette and stirred by the music.',
  params: [
    { key: 'scale', label: 'Scale', type: 'range', min: 0.5, max: 3, step: 0.05, default: DEFAULTS.scale },
    { key: 'flowSpeed', label: 'Flow speed', type: 'range', min: 0, max: 1, step: 0.01, default: DEFAULTS.flowSpeed },
    { key: 'warp', label: 'Warp', type: 'range', min: 0, max: 1, step: 0.01, default: DEFAULTS.warp },
    { key: 'audioDrive', label: 'Audio drive', type: 'range', min: 0, max: 1, step: 0.01, default: DEFAULTS.audioDrive },
  ],
  create: (ctx: EffectContext): EffectInstance => new PlasmaInstance(ctx),
};

export default plasma;
