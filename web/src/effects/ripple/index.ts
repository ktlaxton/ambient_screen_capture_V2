// Ripple (Story 7.1): water ripples pulsed by audio peaks. A CPU-side onset
// detector (bass rising sharply over its slow average) spawns rings into a
// fixed slot pool; each ring expands and decays in render(). Colors map
// through the shared LUT (screen colors or fixed palette).
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
  stopsFromPalette,
} from '../shared/screenLut';
import { readPaletteId } from '../shared/palettes';
import { bandAvg, clamp, easeFactor, readBoolean, readNumber } from '../shared/params';
import { FRAGMENT_SHADER, VERTEX_SHADER } from './shaders';

const DEFAULTS = {
  sensitivity: 0.6,
  speed: 0.5,
  ringWidth: 0.45,
  decay: 0.5,
  screenColors: true,
  palette: 'ocean',
} as const;

const MAX_RIPPLES = 8;
const PREVIEW_RIPPLES = 5;
const MIN_SPAWN_GAP_S = 0.12; // refractory period so one beat = one ring

interface Ripple {
  radius: number;
  amplitude: number;
  active: boolean;
}

class RippleInstance implements EffectInstance {
  private readonly renderer: THREE.WebGLRenderer;
  private readonly scene = new THREE.Scene();
  private readonly camera = new THREE.OrthographicCamera();
  private readonly geometry: THREE.BufferGeometry;
  private readonly material: THREE.ShaderMaterial;
  private readonly palette: THREE.DataTexture;
  private readonly lutData: Uint8Array;
  private readonly dprCap: number;
  private readonly mirror: boolean;
  private readonly slots: number;

  private readonly stopsCur = new Float32Array(STOP_COUNT * 3);
  private readonly stopsTarget = new Float32Array(STOP_COUNT * 3);
  private lutDirty = true;

  private readonly ripples: Ripple[];
  private readonly rippleVecs: THREE.Vector4[];
  private nextSlot = 0;
  private sinceSpawn = 1;

  // Onset detection state (bass vs its slow-moving average).
  private bassSlow = 0;
  private prevBass = 0;
  private pendingSpawn = 0;

  private swellTarget = 0;
  private swellCur = 0;

  private sensitivity: number = DEFAULTS.sensitivity;
  private speed: number = DEFAULTS.speed;
  private ringWidthTarget: number = DEFAULTS.ringWidth;
  private ringWidthCur: number = DEFAULTS.ringWidth;
  private decay: number = DEFAULTS.decay;
  private screenColors: boolean = DEFAULTS.screenColors;
  private paletteId: string = DEFAULTS.palette;

  private intensityTarget = 1;
  private intensityCur = 1;
  private brightnessTarget = 1;
  private brightnessCur = 1;

  private aspect = 16 / 9;
  private cssWidth: number;
  private cssHeight: number;

  constructor(ctx: EffectContext) {
    this.dprCap = ctx.preview ? 1 : 1.5;
    this.mirror = ctx.windowConfig?.relation === 'left';
    this.slots = ctx.preview ? PREVIEW_RIPPLES : MAX_RIPPLES;

    this.ripples = Array.from({ length: this.slots }, () => ({
      radius: 0,
      amplitude: 0,
      active: false,
    }));
    this.rippleVecs = Array.from({ length: this.slots }, () => new THREE.Vector4(0, 0, 0, 0));

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

    this.material = new THREE.ShaderMaterial({
      uniforms: {
        uResolution: { value: new THREE.Vector2(1, 1) },
        uTime: { value: 0 },
        uRipples: { value: this.rippleVecs },
        uRingWidth: { value: this.ringWidthCur },
        uSwell: { value: 0 },
        uBrightness: { value: this.brightnessCur },
        uPalette: { value: this.palette },
      },
      vertexShader: VERTEX_SHADER,
      fragmentShader: FRAGMENT_SHADER,
      defines: { RIPPLES: this.slots },
      depthTest: false,
      depthWrite: false,
    });

    this.geometry = new THREE.BufferGeometry();
    this.geometry.setAttribute(
      'position',
      new THREE.BufferAttribute(new Float32Array([-1, -1, 0, 3, -1, 0, -1, 3, 0]), 3),
    );
    const mesh = new THREE.Mesh(this.geometry, this.material);
    mesh.frustumCulled = false;
    this.scene.add(mesh);

    this.cssWidth = ctx.canvas.clientWidth || ctx.canvas.width || 640;
    this.cssHeight = ctx.canvas.clientHeight || ctx.canvas.height || 360;
    this.resize(this.cssWidth, this.cssHeight);
  }

  onFrame(frame: FramePayload): void {
    if (this.screenColors) {
      computeStops(frame.edges, frame.dominant, this.mirror, this.stopsTarget);
    }
    const bands = frame.audio.bands;
    const bass = clamp(bandAvg(bands, 0, 0.3), 0, 1);
    this.swellTarget = clamp(frame.audio.intensity, 0, 1);

    // Onset: bass jumping over its slow average by more than the (inverted)
    // sensitivity threshold queues a spawn; render() does the actual spawning.
    const jump = bass - Math.max(this.bassSlow, this.prevBass);
    const threshold = 0.3 - 0.25 * this.sensitivity;
    if (jump > threshold) {
      this.pendingSpawn = Math.max(this.pendingSpawn, clamp(jump * 2.5, 0.35, 1));
    }
    this.prevBass = bass;
  }

  private spawn(strength: number): void {
    const slot = this.ripples[this.nextSlot];
    const vec = this.rippleVecs[this.nextSlot];
    this.nextSlot = (this.nextSlot + 1) % this.slots;
    slot.radius = 0.02;
    slot.amplitude = strength;
    slot.active = true;
    // Random drop point, biased toward the middle of the window.
    const x = (Math.random() - 0.5) * this.aspect * 0.8;
    const y = (Math.random() - 0.5) * 0.8;
    vec.set(x, y, slot.radius, slot.amplitude);
  }

  render(timeMs: number, dtMs: number): void {
    const dt = clamp(dtMs, 0, 100) / 1000;

    this.bassSlow += (this.prevBass - this.bassSlow) * easeFactor(dt, 0.6);
    this.swellCur += (this.swellTarget - this.swellCur) * easeFactor(dt, 0.35);

    const kParam = easeFactor(dt, 0.15);
    this.ringWidthCur += (this.ringWidthTarget - this.ringWidthCur) * kParam;
    this.intensityCur += (this.intensityTarget - this.intensityCur) * kParam;
    this.brightnessCur += (this.brightnessTarget - this.brightnessCur) * kParam;

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

    // Spawn queued onsets (refractory-gated, scaled by global intensity).
    this.sinceSpawn += dt;
    if (this.pendingSpawn > 0 && this.sinceSpawn >= MIN_SPAWN_GAP_S) {
      this.spawn(this.pendingSpawn * (0.3 + 0.7 * this.intensityCur));
      this.pendingSpawn = 0;
      this.sinceSpawn = 0;
    }

    // Advance rings: expand at speed, amplitude decays exponentially.
    const expand = (0.12 + 0.55 * this.speed) * (0.25 + 0.75 * this.intensityCur);
    const decayRate = 0.5 + 2.2 * this.decay;
    for (let i = 0; i < this.slots; i++) {
      const r = this.ripples[i];
      if (!r.active) continue;
      r.radius += dt * expand;
      r.amplitude *= Math.exp(-dt * decayRate);
      if (r.amplitude < 0.01 || r.radius > this.aspect * 1.6) {
        r.active = false;
        r.amplitude = 0;
      }
      this.rippleVecs[i].z = r.radius;
      this.rippleVecs[i].w = r.amplitude;
    }

    const u = this.material.uniforms;
    u.uTime.value = timeMs / 1000;
    u.uRingWidth.value = this.ringWidthCur;
    u.uSwell.value = clamp(this.swellCur * this.intensityCur, 0, 1);
    u.uBrightness.value = this.brightnessCur;

    if (this.cssWidth > 0 && this.cssHeight > 0) {
      this.renderer.render(this.scene, this.camera);
    }
  }

  setParams(params: EffectParams): void {
    this.sensitivity = clamp(readNumber(params, 'sensitivity', DEFAULTS.sensitivity), 0, 1);
    this.speed = clamp(readNumber(params, 'speed', DEFAULTS.speed), 0, 1);
    this.ringWidthTarget = clamp(readNumber(params, 'ringWidth', DEFAULTS.ringWidth), 0, 1);
    this.decay = clamp(readNumber(params, 'decay', DEFAULTS.decay), 0, 1);

    const screen = readBoolean(params, 'screenColors', DEFAULTS.screenColors);
    const palette = readPaletteId(params, 'palette', DEFAULTS.palette);
    if (screen !== this.screenColors || palette !== this.paletteId) {
      this.screenColors = screen;
      this.paletteId = palette;
      if (!screen) stopsFromPalette(palette, this.stopsTarget);
    }
  }

  setGlobals(globals: GlobalRenderSettings): void {
    this.intensityTarget = clamp(globals.intensity, 0, 1);
    this.brightnessTarget = clamp(globals.brightness, 0, 1);
  }

  resize(width: number, height: number): void {
    this.cssWidth = width;
    this.cssHeight = height;
    if (width <= 0 || height <= 0) return;
    this.aspect = width / height;
    const dpr = Math.min(
      typeof window !== 'undefined' ? window.devicePixelRatio || 1 : 1,
      this.dprCap,
    );
    this.renderer.setPixelRatio(dpr);
    this.renderer.setSize(width, height, false);
    (this.material.uniforms.uResolution.value as THREE.Vector2).set(width * dpr, height * dpr);
  }

  dispose(): void {
    this.geometry.dispose();
    this.material.dispose();
    this.palette.dispose();
    this.renderer.dispose();
    this.renderer.forceContextLoss(); // release the WebGL context now (gallery churns contexts; Chromium caps ~16)
  }
}

const ripple: EffectModule = {
  id: 'ripple',
  name: 'Ripple',
  description: 'Dark water where every beat drops an expanding ring of screen-colored light.',
  params: [
    { key: 'sensitivity', label: 'Beat sensitivity', type: 'range', min: 0, max: 1, step: 0.01, default: DEFAULTS.sensitivity },
    { key: 'speed', label: 'Ripple speed', type: 'range', min: 0, max: 1, step: 0.01, default: DEFAULTS.speed },
    { key: 'ringWidth', label: 'Ring width', type: 'range', min: 0, max: 1, step: 0.01, default: DEFAULTS.ringWidth },
    { key: 'decay', label: 'Decay', type: 'range', min: 0, max: 1, step: 0.01, default: DEFAULTS.decay },
    { key: 'screenColors', label: 'Screen colors', type: 'toggle', default: DEFAULTS.screenColors },
    { key: 'palette', label: 'Palette', type: 'palette', default: DEFAULTS.palette },
  ],
  create: (ctx: EffectContext): EffectInstance => new RippleInstance(ctx),
};

export default ripple;
