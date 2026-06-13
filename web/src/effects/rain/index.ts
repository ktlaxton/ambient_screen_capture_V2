// Rain (Story 7.1): parallax layers of falling streaks tinted by the screen's
// colors via the shared LUT (or a fixed palette). Bass gusts boost fall speed
// and brightness; density/length/speed are tunable.
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
  density: 0.55,
  fallSpeed: 0.5,
  streakLength: 0.5,
  audioDrive: 0.5,
  screenColors: true,
  palette: 'cool',
} as const;

class RainInstance implements EffectInstance {
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

  private densityTarget: number = DEFAULTS.density;
  private densityCur: number = DEFAULTS.density;
  private fallSpeed: number = DEFAULTS.fallSpeed;
  private lengthTarget: number = DEFAULTS.streakLength;
  private lengthCur: number = DEFAULTS.streakLength;
  private audioDrive: number = DEFAULTS.audioDrive;
  private screenColors: boolean = DEFAULTS.screenColors;
  private paletteId: string = DEFAULTS.palette;

  private intensityTarget = 1;
  private intensityCur = 1;
  private brightnessTarget = 1;
  private brightnessCur = 1;

  private fallTime = 0;
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

    this.material = new THREE.ShaderMaterial({
      uniforms: {
        uResolution: { value: new THREE.Vector2(1, 1) },
        uTime: { value: 0 },
        uFall: { value: 0 },
        uDensity: { value: this.densityCur },
        uLength: { value: this.lengthCur },
        uGust: { value: 0 },
        uBrightness: { value: this.brightnessCur },
        uPalette: { value: this.palette },
      },
      vertexShader: VERTEX_SHADER,
      fragmentShader: FRAGMENT_SHADER,
      defines: { LAYERS: ctx.preview ? 1 : 2 },
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
    this.bassTarget = clamp(bandAvg(bands, 0, 0.3) * (0.5 + 0.5 * frame.audio.intensity), 0, 1);
  }

  render(timeMs: number, dtMs: number): void {
    const dt = clamp(dtMs, 0, 100) / 1000;

    this.bassCur += (this.bassTarget - this.bassCur) *
      easeFactor(dt, this.bassTarget > this.bassCur ? 0.07 : 0.4);

    const kParam = easeFactor(dt, 0.15);
    this.densityCur += (this.densityTarget - this.densityCur) * kParam;
    this.lengthCur += (this.lengthTarget - this.lengthCur) * kParam;
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

    const gust = clamp(this.bassCur * this.audioDrive * this.intensityCur, 0, 1);
    const motion = 0.15 + 0.85 * this.intensityCur;
    this.fallTime += dt * (0.25 + 1.1 * this.fallSpeed) * motion * (1 + 0.8 * gust);

    const u = this.material.uniforms;
    u.uTime.value = timeMs / 1000;
    u.uFall.value = this.fallTime;
    u.uDensity.value = this.densityCur * (0.4 + 0.6 * this.intensityCur);
    u.uLength.value = this.lengthCur;
    u.uGust.value = gust;
    u.uBrightness.value = this.brightnessCur;

    if (this.cssWidth > 0 && this.cssHeight > 0) {
      this.renderer.render(this.scene, this.camera);
    }
  }

  setParams(params: EffectParams): void {
    this.densityTarget = clamp(readNumber(params, 'density', DEFAULTS.density), 0.05, 1);
    this.fallSpeed = clamp(readNumber(params, 'fallSpeed', DEFAULTS.fallSpeed), 0, 1);
    this.lengthTarget = clamp(readNumber(params, 'streakLength', DEFAULTS.streakLength), 0, 1);
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

const rain: EffectModule = {
  id: 'rain',
  name: 'Rain',
  description: 'Falling light streaks tinted by your screen, gusting harder with the bass.',
  params: [
    { key: 'density', label: 'Density', type: 'range', min: 0.05, max: 1, step: 0.01, default: DEFAULTS.density },
    { key: 'fallSpeed', label: 'Fall speed', type: 'range', min: 0, max: 1, step: 0.01, default: DEFAULTS.fallSpeed },
    { key: 'streakLength', label: 'Streak length', type: 'range', min: 0, max: 1, step: 0.01, default: DEFAULTS.streakLength },
    { key: 'audioDrive', label: 'Audio drive', type: 'range', min: 0, max: 1, step: 0.01, default: DEFAULTS.audioDrive },
    { key: 'screenColors', label: 'Screen colors', type: 'toggle', default: DEFAULTS.screenColors },
    { key: 'palette', label: 'Palette', type: 'palette', default: DEFAULTS.palette },
  ],
  create: (ctx: EffectContext): EffectInstance => new RainInstance(ctx),
};

export default rain;
