// Waveform (Story 7.1): an audio oscilloscope line. The engine's frequency
// bands are resampled into a smooth 64-point curve (alternating bands flip
// sign so it reads as a waveform, not a spectrum), eased per-point with fast
// attack / slow release, and drawn as a glowing line. Line color follows the
// screen's dominant color or a fixed picker color.
import * as THREE from 'three';
import type { EffectParams, FramePayload } from '../../shared/bridge';
import type {
  EffectContext,
  EffectInstance,
  EffectModule,
  GlobalRenderSettings,
} from '../types';
import { clamp, easeFactor, hexToRgb01, readBoolean, readNumber, readString } from '../shared/params';
import { FRAGMENT_SHADER, VERTEX_SHADER } from './shaders';

const DEFAULTS = {
  glow: 0.6,
  thickness: 0.45,
  amplitude: 0.7,
  mirror: true,
  screenColors: true,
  lineColor: '#22d3ee',
} as const;

const POINTS = 64;

/** Cosine-interpolated resample of bands onto the fixed point grid, signed
 *  alternately per band so the curve oscillates around the center line. */
function buildCurveTargets(bands: number[], out: Float32Array): void {
  const n = bands.length;
  if (n === 0) {
    out.fill(0.5);
    return;
  }
  for (let i = 0; i < POINTS; i++) {
    const f = (i / (POINTS - 1)) * (n - 1);
    const i0 = Math.floor(f);
    const i1 = Math.min(n - 1, i0 + 1);
    const w = 0.5 - 0.5 * Math.cos((f - i0) * Math.PI); // cosine ease between bands
    const a = clamp(bands[i0] ?? 0, 0, 1) * (i0 % 2 === 0 ? 1 : -1);
    const b = clamp(bands[i1] ?? 0, 0, 1) * (i1 % 2 === 0 ? 1 : -1);
    const v = a * (1 - w) + b * w;
    // Hann window tapers the ends so the line meets the baseline at the edges.
    const win = 0.5 - 0.5 * Math.cos((i / (POINTS - 1)) * Math.PI * 2);
    out[i] = 0.5 + 0.5 * v * win;
  }
}

class WaveformInstance implements EffectInstance {
  private readonly renderer: THREE.WebGLRenderer;
  private readonly scene = new THREE.Scene();
  private readonly camera = new THREE.OrthographicCamera();
  private readonly geometry: THREE.BufferGeometry;
  private readonly material: THREE.ShaderMaterial;
  private readonly curveTex: THREE.DataTexture;
  private readonly curveData = new Float32Array(POINTS);
  private readonly curveCur = new Float32Array(POINTS).fill(0.5);
  private readonly curveTarget = new Float32Array(POINTS).fill(0.5);
  private readonly dprCap: number;

  private readonly colorCur = new THREE.Color(...hexToRgb01(DEFAULTS.lineColor));
  private readonly colorTarget = new THREE.Color(...hexToRgb01(DEFAULTS.lineColor));

  private audioIntensityTarget = 0;
  private audioIntensityCur = 0;

  private glowTarget: number = DEFAULTS.glow;
  private glowCur: number = DEFAULTS.glow;
  private thicknessTarget: number = DEFAULTS.thickness;
  private thicknessCur: number = DEFAULTS.thickness;
  private amplitude: number = DEFAULTS.amplitude;
  private mirror: boolean = DEFAULTS.mirror;
  private screenColors: boolean = DEFAULTS.screenColors;
  private fixedColor = hexToRgb01(DEFAULTS.lineColor);

  private intensityTarget = 1;
  private intensityCur = 1;
  private brightnessTarget = 1;
  private brightnessCur = 1;

  private cssWidth: number;
  private cssHeight: number;

  constructor(ctx: EffectContext) {
    this.dprCap = ctx.preview ? 1 : 1.5;

    this.curveData.fill(0.5);
    this.curveTex = new THREE.DataTexture(
      this.curveData,
      POINTS,
      1,
      THREE.RedFormat,
      THREE.FloatType,
    );
    this.curveTex.magFilter = THREE.LinearFilter; // hardware-smooth curve between points
    this.curveTex.minFilter = THREE.LinearFilter;
    this.curveTex.wrapS = THREE.ClampToEdgeWrapping;
    this.curveTex.wrapT = THREE.ClampToEdgeWrapping;
    this.curveTex.needsUpdate = true;

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
        uCurve: { value: this.curveTex },
        uColor: { value: this.colorCur },
        uThickness: { value: this.thicknessCur },
        uGlow: { value: this.glowCur },
        uAmp: { value: 0 },
        uMirror: { value: this.mirror ? 1 : 0 },
        uBrightness: { value: this.brightnessCur },
      },
      vertexShader: VERTEX_SHADER,
      fragmentShader: FRAGMENT_SHADER,
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
    buildCurveTargets(frame.audio.bands, this.curveTarget);
    this.audioIntensityTarget = clamp(frame.audio.intensity, 0, 1);
    if (this.screenColors) {
      this.colorTarget.setRGB(
        Math.max(frame.dominant[0] / 255, 0.08),
        Math.max(frame.dominant[1] / 255, 0.08),
        Math.max(frame.dominant[2] / 255, 0.1),
      );
    }
  }

  render(timeMs: number, dtMs: number): void {
    const dt = clamp(dtMs, 0, 100) / 1000;

    // Per-point easing: fast attack so transients land, slower release.
    let moved = false;
    for (let i = 0; i < POINTS; i++) {
      const target = this.curveTarget[i];
      const cur = this.curveCur[i];
      const k = easeFactor(dt, Math.abs(target - 0.5) > Math.abs(cur - 0.5) ? 0.04 : 0.14);
      const next = cur + (target - cur) * k;
      if (Math.abs(next - cur) > 1e-5) moved = true;
      this.curveCur[i] = next;
      this.curveData[i] = next;
    }
    if (moved) this.curveTex.needsUpdate = true;

    this.audioIntensityCur += (this.audioIntensityTarget - this.audioIntensityCur) *
      easeFactor(dt, this.audioIntensityTarget > this.audioIntensityCur ? 0.06 : 0.3);

    const kParam = easeFactor(dt, 0.15);
    this.glowCur += (this.glowTarget - this.glowCur) * kParam;
    this.thicknessCur += (this.thicknessTarget - this.thicknessCur) * kParam;
    this.intensityCur += (this.intensityTarget - this.intensityCur) * kParam;
    this.brightnessCur += (this.brightnessTarget - this.brightnessCur) * kParam;
    this.colorCur.lerp(this.colorTarget, easeFactor(dt, 0.25));

    const u = this.material.uniforms;
    u.uTime.value = timeMs / 1000;
    u.uThickness.value = this.thicknessCur;
    u.uGlow.value = this.glowCur;
    // Amplitude breathes with overall loudness, scaled by the user + intensity.
    u.uAmp.value = clamp(
      this.amplitude * (0.35 + 0.65 * this.audioIntensityCur) * (0.25 + 0.75 * this.intensityCur),
      0,
      1,
    );
    u.uMirror.value = this.mirror ? 1 : 0;
    u.uBrightness.value = this.brightnessCur;

    if (this.cssWidth > 0 && this.cssHeight > 0) {
      this.renderer.render(this.scene, this.camera);
    }
  }

  setParams(params: EffectParams): void {
    this.glowTarget = clamp(readNumber(params, 'glow', DEFAULTS.glow), 0, 1);
    this.thicknessTarget = clamp(readNumber(params, 'thickness', DEFAULTS.thickness), 0, 1);
    this.amplitude = clamp(readNumber(params, 'amplitude', DEFAULTS.amplitude), 0.1, 1);
    this.mirror = readBoolean(params, 'mirror', DEFAULTS.mirror);

    this.fixedColor = hexToRgb01(
      readString(params, 'lineColor', DEFAULTS.lineColor),
      hexToRgb01(DEFAULTS.lineColor),
    );
    this.screenColors = readBoolean(params, 'screenColors', DEFAULTS.screenColors);
    if (!this.screenColors) {
      this.colorTarget.setRGB(this.fixedColor[0], this.fixedColor[1], this.fixedColor[2]);
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
    this.curveTex.dispose();
    this.renderer.dispose();
    this.renderer.forceContextLoss(); // release the WebGL context now (gallery churns contexts; Chromium caps ~16)
  }
}

const waveform: EffectModule = {
  id: 'waveform',
  name: 'Waveform',
  description: 'A glowing oscilloscope line tracing the music, tinted by your screen or a fixed color.',
  params: [
    { key: 'amplitude', label: 'Amplitude', type: 'range', min: 0.1, max: 1, step: 0.01, default: DEFAULTS.amplitude },
    { key: 'thickness', label: 'Thickness', type: 'range', min: 0, max: 1, step: 0.01, default: DEFAULTS.thickness },
    { key: 'glow', label: 'Glow', type: 'range', min: 0, max: 1, step: 0.01, default: DEFAULTS.glow },
    { key: 'mirror', label: 'Mirror', type: 'toggle', default: DEFAULTS.mirror },
    { key: 'screenColors', label: 'Screen colors', type: 'toggle', default: DEFAULTS.screenColors },
    { key: 'lineColor', label: 'Line color', type: 'color', default: DEFAULTS.lineColor },
  ],
  create: (ctx: EffectContext): EffectInstance => new WaveformInstance(ctx),
};

export default waveform;
