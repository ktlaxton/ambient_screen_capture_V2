// Spectrum Bars (FR6, AC2): a glassy, glowing audio spectrum rendered in ONE
// fullscreen SDF pass — rounded-top capsule bars, in-shader distance-falloff
// glow (no postprocessing), mirrored reflection under the baseline, peak-hold
// caps, and a radial mode that wraps the spectrum around a pulsing circle.
// Bar values/peaks ride in a barCount x 1 RG float DataTexture (CPU keeps
// attack/release + peak gravity, see spectrum.ts); the color gradient across
// bars comes from the source's TOP edge zone colors (fallback: dominant ramp).
import * as THREE from 'three';
import type { EffectParams, FramePayload, RGB } from '../../shared/bridge';
import type {
  EffectContext,
  EffectInstance,
  EffectModule,
  GlobalRenderSettings,
} from '../types';
import { SpectrumProcessor } from './spectrum';
import { FRAGMENT_SHADER, VERTEX_SHADER } from './shaders';

const DEFAULTS = { barCount: 32, glow: 0.5, reflection: 0.4, style: 'bars' } as const;
const FALLBACK_STOPS = 8;

const clamp = (x: number, lo: number, hi: number): number => (x < lo ? lo : x > hi ? hi : x);

/** dt-scaled exponential smoothing factor (dt and tau in seconds). */
const easeFactor = (dt: number, tau: number): number => 1 - Math.exp(-dt / tau);

function readNumber(params: EffectParams, key: string, fallback: number): number {
  const v = params[key];
  return typeof v === 'number' && Number.isFinite(v) ? v : fallback;
}

function readString(params: EffectParams, key: string, fallback: string): string {
  const v = params[key];
  return typeof v === 'string' ? v : fallback;
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

function makeBarsTexture(data: Float32Array, count: number): THREE.DataTexture {
  const tex = new THREE.DataTexture(data, count, 1, THREE.RGFormat, THREE.FloatType);
  tex.minFilter = THREE.NearestFilter; // sampled at exact texel centers
  tex.magFilter = THREE.NearestFilter;
  tex.wrapS = THREE.ClampToEdgeWrapping;
  tex.wrapT = THREE.ClampToEdgeWrapping;
  tex.needsUpdate = true;
  return tex;
}

function makeGradTexture(data: Uint8Array, count: number): THREE.DataTexture {
  const tex = new THREE.DataTexture(data, count, 1, THREE.RGBAFormat, THREE.UnsignedByteType);
  tex.minFilter = THREE.LinearFilter; // hardware-lerped gradient across zone colors
  tex.magFilter = THREE.LinearFilter;
  tex.wrapS = THREE.ClampToEdgeWrapping;
  tex.wrapT = THREE.ClampToEdgeWrapping;
  tex.needsUpdate = true;
  return tex;
}

interface BarsUniforms {
  uBars: { value: THREE.DataTexture };
  uGrad: { value: THREE.DataTexture };
  uBarCount: { value: number };
  uTime: { value: number };
  uRes: { value: THREE.Vector2 };
  uAspect: { value: number };
  uIntensity: { value: number };
  uBrightness: { value: number };
  uGlow: { value: number };
  uReflect: { value: number };
  uStyle: { value: number };
  uKick: { value: number };
  uPulse: { value: number };
  uDominant: { value: THREE.Color };
  [uniform: string]: THREE.IUniform;
}

class AudioBarsInstance implements EffectInstance {
  private readonly renderer: THREE.WebGLRenderer;
  private readonly scene = new THREE.Scene();
  private readonly camera = new THREE.OrthographicCamera();
  private readonly geometry: THREE.BufferGeometry;
  private readonly material: THREE.ShaderMaterial;
  private readonly uniforms: BarsUniforms;
  private readonly dprCap: number;
  private readonly mirror: boolean;

  private readonly spectrum: SpectrumProcessor;
  private barsTex: THREE.DataTexture;

  // Gradient LUT (eased CPU-side so palette changes glide instead of popping).
  private gradTex: THREE.DataTexture;
  private gradData: Uint8Array;
  private gradCur: Float32Array;
  private gradTarget: Float32Array;
  private gradLen: number;
  private gradSeeded = false;
  private readonly scratchA = new THREE.Color();
  private readonly scratchB = new THREE.Color();

  // Audio envelopes: targets set in onFrame, eased toward in render.
  private pulseTarget = 0;
  private pulseCur = 0;
  private bassLatest = 0;
  private bassSlow = 0;
  private kickTarget = 0;
  private kickCur = 0;

  // Dominant color (eased).
  private readonly domTarget = new THREE.Color(0.18, 0.2, 0.26);

  // Params.
  private barCount: number = DEFAULTS.barCount;
  private glowTarget: number = DEFAULTS.glow;
  private glowCur: number = DEFAULTS.glow;
  private reflectTarget: number = DEFAULTS.reflection;
  private reflectCur: number = DEFAULTS.reflection;
  private styleRadial = false;

  // Globals.
  private intensityTarget = 1;
  private intensityCur = 1;
  private brightnessTarget = 1;
  private brightnessCur = 1;

  private cssWidth: number;
  private cssHeight: number;

  constructor(ctx: EffectContext) {
    this.dprCap = ctx.preview ? 1 : 1.5;
    this.mirror = ctx.windowConfig?.relation === 'left';

    this.spectrum = new SpectrumProcessor(this.barCount);
    this.barsTex = makeBarsTexture(this.spectrum.data, this.barCount);

    this.gradLen = FALLBACK_STOPS;
    this.gradCur = new Float32Array(this.gradLen * 3);
    this.gradTarget = new Float32Array(this.gradLen * 3);
    this.gradData = new Uint8Array(this.gradLen * 4);
    this.seedFallbackGradient();
    this.gradCur.set(this.gradTarget);
    this.bakeGradient();
    this.gradTex = makeGradTexture(this.gradData, this.gradLen);

    this.renderer = new THREE.WebGLRenderer({
      canvas: ctx.canvas,
      antialias: false,
      powerPreference: 'high-performance',
      alpha: false,
      stencil: false,
      depth: false,
    });

    this.uniforms = {
      uBars: { value: this.barsTex },
      uGrad: { value: this.gradTex },
      uBarCount: { value: this.barCount },
      uTime: { value: 0 },
      uRes: { value: new THREE.Vector2(1, 1) },
      uAspect: { value: 16 / 9 },
      uIntensity: { value: this.intensityCur },
      uBrightness: { value: this.brightnessCur },
      uGlow: { value: this.glowCur },
      uReflect: { value: this.reflectCur },
      uStyle: { value: 0 },
      uKick: { value: 0 },
      uPulse: { value: 0 },
      uDominant: { value: new THREE.Color(0.18, 0.2, 0.26) },
    };

    this.material = new THREE.ShaderMaterial({
      uniforms: this.uniforms,
      vertexShader: VERTEX_SHADER,
      fragmentShader: FRAGMENT_SHADER,
      // Glow bleed reach: 2 neighbor bars each side in preview, 3 fullscreen.
      defines: { NEIGHBORS: ctx.preview ? 2 : 3 },
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
    this.spectrum.setTargets(frame.audio.bands);
    this.pulseTarget = clamp(frame.audio.intensity, 0, 1);

    // Kick = bass rising sharply above its slow-moving average (attack detector).
    const bass = bandAvg(frame.audio.bands, 0, 0.2);
    const attack = bass - this.bassSlow;
    if (attack > 0.05) this.kickTarget = Math.min(1, this.kickTarget + attack * 2.2);
    this.bassLatest = bass;

    this.setGradientTargets(frame.edges.top, frame.dominant);
    const [dr, dg, db] = frame.dominant;
    this.domTarget.setRGB(dr / 255, dg / 255, db / 255);
  }

  render(timeMs: number, dtMs: number): void {
    const dt = clamp(dtMs, 0, 100) / 1000;

    // Spectrum envelopes + peak caps; re-upload only while anything moves.
    if (this.spectrum.update(dt)) this.barsTex.needsUpdate = true;

    // Audio easing: fast attack, slower release.
    this.pulseCur +=
      (this.pulseTarget - this.pulseCur) *
      easeFactor(dt, this.pulseTarget > this.pulseCur ? 0.08 : 0.35);
    this.bassSlow += (this.bassLatest - this.bassSlow) * easeFactor(dt, 0.5);
    this.kickTarget *= Math.exp(-dt / 0.1); // the thump itself is a short impulse
    this.kickCur +=
      (this.kickTarget - this.kickCur) *
      easeFactor(dt, this.kickTarget > this.kickCur ? 0.012 : 0.09);

    // Param + global easing so live tweaks glide.
    const kParam = easeFactor(dt, 0.15);
    this.glowCur += (this.glowTarget - this.glowCur) * kParam;
    this.reflectCur += (this.reflectTarget - this.reflectCur) * kParam;
    this.intensityCur += (this.intensityTarget - this.intensityCur) * kParam;
    this.brightnessCur += (this.brightnessTarget - this.brightnessCur) * kParam;

    // Gradient + dominant color glide (rebake the LUT only while moving).
    const kColor = easeFactor(dt, 0.25);
    let maxDelta = 0;
    for (let i = 0; i < this.gradCur.length; i++) {
      const d = (this.gradTarget[i] - this.gradCur[i]) * kColor;
      this.gradCur[i] += d;
      const ad = Math.abs(d);
      if (ad > maxDelta) maxDelta = ad;
    }
    if (maxDelta > 0.0008) {
      this.bakeGradient();
      this.gradTex.needsUpdate = true;
    }
    this.uniforms.uDominant.value.lerp(this.domTarget, kColor);

    this.uniforms.uTime.value = (timeMs % 3_600_000) / 1000; // wrapped for fp32 precision
    this.uniforms.uIntensity.value = this.intensityCur;
    this.uniforms.uBrightness.value = this.brightnessCur;
    this.uniforms.uGlow.value = this.glowCur;
    this.uniforms.uReflect.value = this.reflectCur;
    this.uniforms.uStyle.value = this.styleRadial ? 1 : 0;
    this.uniforms.uKick.value = this.kickCur;
    this.uniforms.uPulse.value = this.pulseCur;

    if (this.cssWidth > 0 && this.cssHeight > 0) {
      this.renderer.render(this.scene, this.camera);
    }
  }

  setParams(params: EffectParams): void {
    const count = clamp(Math.round(readNumber(params, 'barCount', DEFAULTS.barCount)), 16, 64);
    if (count !== this.barCount) {
      // Structural: rebuild the bars texture for the new width.
      this.barCount = count;
      this.spectrum.setBarCount(count);
      this.barsTex.dispose();
      this.barsTex = makeBarsTexture(this.spectrum.data, count);
      this.uniforms.uBars.value = this.barsTex;
      this.uniforms.uBarCount.value = count;
    }
    this.glowTarget = clamp(readNumber(params, 'glow', DEFAULTS.glow), 0, 1);
    this.reflectTarget = clamp(readNumber(params, 'reflection', DEFAULTS.reflection), 0, 1);
    this.styleRadial = readString(params, 'style', DEFAULTS.style) === 'radial';
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
    this.uniforms.uRes.value.set(width * dpr, height * dpr);
    this.uniforms.uAspect.value = width / height;
  }

  dispose(): void {
    this.geometry.dispose();
    this.material.dispose();
    this.barsTex.dispose();
    this.gradTex.dispose();
    this.renderer.dispose();
    this.renderer.forceContextLoss(); // release the WebGL context now (gallery churns contexts; Chromium caps ~16)
  }

  /** Per-frame gradient targets from the TOP edge zones (fallback: dominant ramp). */
  private setGradientTargets(top: RGB[], dominant: RGB): void {
    if (top.length === 0) {
      this.seedFallbackGradient(dominant);
      if (!this.gradSeeded) {
        this.gradCur.set(this.gradTarget);
        this.gradSeeded = true;
      }
      return;
    }
    if (top.length !== this.gradLen) this.rebuildGradient(top.length);
    const n = top.length;
    for (let i = 0; i < n; i++) {
      // Mirror left<->right when this monitor sits to the LEFT of the source (FR7).
      const zone = top[this.mirror ? n - 1 - i : i] ?? dominant;
      this.gradTarget[i * 3] = (zone[0] ?? 0) / 255;
      this.gradTarget[i * 3 + 1] = (zone[1] ?? 0) / 255;
      this.gradTarget[i * 3 + 2] = (zone[2] ?? 0) / 255;
    }
    if (!this.gradSeeded) {
      this.gradCur.set(this.gradTarget);
      this.gradSeeded = true;
    }
  }

  /** dominant -> hue-shifted-accent ramp used before the first frame / with no edge data. */
  private seedFallbackGradient(dominant?: RGB): void {
    if (this.gradLen !== FALLBACK_STOPS) this.rebuildGradient(FALLBACK_STOPS);
    if (dominant) {
      this.scratchA.setRGB(dominant[0] / 255, dominant[1] / 255, dominant[2] / 255);
    } else {
      this.scratchA.setRGB(0.16, 0.35, 0.65); // pre-frame default: calm blue
    }
    this.scratchB.copy(this.scratchA).offsetHSL(0.13, 0.06, 0.1);
    for (let i = 0; i < this.gradLen; i++) {
      const f = i / (this.gradLen - 1);
      this.gradTarget[i * 3] = this.scratchA.r + (this.scratchB.r - this.scratchA.r) * f;
      this.gradTarget[i * 3 + 1] = this.scratchA.g + (this.scratchB.g - this.scratchA.g) * f;
      this.gradTarget[i * 3 + 2] = this.scratchA.b + (this.scratchB.b - this.scratchA.b) * f;
    }
  }

  /** Structural: zone count changed — reallocate arrays + texture at the new width. */
  private rebuildGradient(len: number): void {
    this.gradLen = len;
    this.gradCur = new Float32Array(len * 3);
    this.gradTarget = new Float32Array(len * 3);
    this.gradData = new Uint8Array(len * 4);
    this.gradSeeded = false; // snap to the next targets instead of easing from black
    this.gradTex?.dispose();
    this.gradTex = makeGradTexture(this.gradData, len);
    if (this.uniforms) this.uniforms.uGrad.value = this.gradTex;
  }

  private bakeGradient(): void {
    for (let i = 0; i < this.gradLen; i++) {
      this.gradData[i * 4] = Math.round(clamp(this.gradCur[i * 3], 0, 1) * 255);
      this.gradData[i * 4 + 1] = Math.round(clamp(this.gradCur[i * 3 + 1], 0, 1) * 255);
      this.gradData[i * 4 + 2] = Math.round(clamp(this.gradCur[i * 3 + 2], 0, 1) * 255);
      this.gradData[i * 4 + 3] = 255;
    }
  }
}

const audioBars: EffectModule = {
  id: 'audio-bars',
  name: 'Spectrum Bars',
  description: "A glowing frequency spectrum with reflections, colored by what's on screen.",
  params: [
    { key: 'barCount', label: 'Bars', type: 'range', min: 16, max: 64, step: 4, default: DEFAULTS.barCount },
    { key: 'glow', label: 'Glow', type: 'range', min: 0, max: 1, step: 0.01, default: DEFAULTS.glow },
    { key: 'reflection', label: 'Reflection', type: 'range', min: 0, max: 1, step: 0.01, default: DEFAULTS.reflection },
    {
      key: 'style',
      label: 'Style',
      type: 'select',
      options: [
        { value: 'bars', label: 'Bars' },
        { value: 'radial', label: 'Radial' },
      ],
      default: DEFAULTS.style,
    },
  ],
  create: (ctx: EffectContext): EffectInstance => new AudioBarsInstance(ctx),
};

export default audioBars;
