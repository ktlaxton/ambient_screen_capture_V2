// Fire (Story 7.1): bass-reactive rising flames + ember sparks. Heat maps
// through the shared LUT — the 'ember' palette by default, or the screen's
// edge colors for ghost-flame looks. Bass drives flame height, heat surge and
// rise speed; everything honors global intensity/brightness.
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
  stopsFromPalette,
} from '../shared/screenLut';
import { readPaletteId } from '../shared/palettes';
import { bandAvg, clamp, easeFactor, readBoolean, readNumber } from '../shared/params';
import { FRAGMENT_SHADER, VERTEX_SHADER } from './shaders';

const DEFAULTS = {
  height: 0.65,
  turbulence: 0.55,
  embers: 0.6,
  audioDrive: 0.6,
  screenColors: false, // fire reads best on its ember ramp; screen mode is opt-in
  palette: 'ember',
} as const;

class FireInstance implements EffectInstance {
  private readonly renderer: THREE.WebGLRenderer;
  private readonly scene = new THREE.Scene();
  private readonly camera = new THREE.OrthographicCamera();
  private readonly geometry: THREE.BufferGeometry;
  private readonly material: THREE.ShaderMaterial;
  private readonly palette: THREE.DataTexture;
  private readonly lutData: Uint8Array;
  private readonly dprCap: number;
  private readonly mirror: boolean;

  private readonly stopsCur = new Float32Array(STOP_COUNT * 3);
  private readonly stopsTarget = new Float32Array(STOP_COUNT * 3);
  private lutDirty = true;

  private bassTarget = 0;
  private bassCur = 0;

  private heightTarget: number = DEFAULTS.height;
  private heightCur: number = DEFAULTS.height;
  private turbulenceTarget: number = DEFAULTS.turbulence;
  private turbulenceCur: number = DEFAULTS.turbulence;
  private embers: number = DEFAULTS.embers;
  private audioDrive: number = DEFAULTS.audioDrive;
  private screenColors: boolean = DEFAULTS.screenColors;
  private paletteId: string = DEFAULTS.palette;

  private intensityTarget = 1;
  private intensityCur = 1;
  private brightnessTarget = 1;
  private brightnessCur = 1;

  private riseTime = 0;
  private cssWidth: number;
  private cssHeight: number;

  constructor(ctx: EffectContext) {
    this.dprCap = ctx.preview ? 1 : 1.5;
    this.mirror = ctx.windowConfig?.relation === 'left';

    stopsFromPalette(DEFAULTS.palette, this.stopsTarget);
    this.stopsCur.set(this.stopsTarget);
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
        uRise: { value: 0 },
        uHeight: { value: this.heightCur },
        uTurbulence: { value: this.turbulenceCur },
        uHeat: { value: 0 },
        uEmbers: { value: this.embers },
        uBrightness: { value: this.brightnessCur },
        uPalette: { value: this.palette },
      },
      vertexShader: VERTEX_SHADER,
      fragmentShader: FRAGMENT_SHADER,
      defines: { OCTAVES: ctx.preview ? 3 : 5 },
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
    this.bassTarget = clamp(bandAvg(bands, 0, 0.25) * (0.5 + 0.5 * frame.audio.intensity), 0, 1);
  }

  render(timeMs: number, dtMs: number): void {
    const dt = clamp(dtMs, 0, 100) / 1000;

    // Fast attack so kicks flare instantly; slow release so flames settle.
    this.bassCur += (this.bassTarget - this.bassCur) *
      easeFactor(dt, this.bassTarget > this.bassCur ? 0.05 : 0.3);

    const kParam = easeFactor(dt, 0.15);
    this.heightCur += (this.heightTarget - this.heightCur) * kParam;
    this.turbulenceCur += (this.turbulenceTarget - this.turbulenceCur) * kParam;
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

    const heat = clamp(this.bassCur * this.audioDrive * this.intensityCur, 0, 1);
    const motion = 0.15 + 0.85 * this.intensityCur;
    this.riseTime += dt * (0.45 + 0.55 * this.heightCur) * motion * (1 + 0.7 * heat);

    const u = this.material.uniforms;
    u.uTime.value = timeMs / 1000;
    u.uRise.value = this.riseTime;
    u.uHeight.value = this.heightCur * (0.35 + 0.65 * this.intensityCur);
    u.uTurbulence.value = this.turbulenceCur;
    u.uHeat.value = heat;
    u.uEmbers.value = this.embers;
    u.uBrightness.value = this.brightnessCur;

    if (this.cssWidth > 0 && this.cssHeight > 0) {
      this.renderer.render(this.scene, this.camera);
    }
  }

  setParams(params: EffectParams): void {
    this.heightTarget = clamp(readNumber(params, 'height', DEFAULTS.height), 0.15, 1);
    this.turbulenceTarget = clamp(readNumber(params, 'turbulence', DEFAULTS.turbulence), 0, 1);
    this.embers = clamp(readNumber(params, 'embers', DEFAULTS.embers), 0, 1);
    this.audioDrive = clamp(readNumber(params, 'audioDrive', DEFAULTS.audioDrive), 0, 1);

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

const fire: EffectModule = {
  id: 'fire',
  name: 'Fire',
  description: 'Rising flames and embers that flare with the bass — ember-ramp or screen-colored.',
  params: [
    { key: 'height', label: 'Flame height', type: 'range', min: 0.15, max: 1, step: 0.01, default: DEFAULTS.height },
    { key: 'turbulence', label: 'Turbulence', type: 'range', min: 0, max: 1, step: 0.01, default: DEFAULTS.turbulence },
    { key: 'embers', label: 'Embers', type: 'range', min: 0, max: 1, step: 0.01, default: DEFAULTS.embers },
    { key: 'audioDrive', label: 'Audio drive', type: 'range', min: 0, max: 1, step: 0.01, default: DEFAULTS.audioDrive },
    { key: 'screenColors', label: 'Screen colors', type: 'toggle', default: DEFAULTS.screenColors },
    { key: 'palette', label: 'Palette', type: 'palette', default: DEFAULTS.palette },
  ],
  create: (ctx: EffectContext): EffectInstance => new FireInstance(ctx),
};

export default fire;
