// Particle Field (FR6): thousands of soft light motes drifting through the
// screen's color field, surging with the music. THREE.Points with a custom
// ShaderMaterial; motion is fully stateless in the vertex shader (no per-frame
// attribute uploads). Edge colors arrive via a zonesPerEdge x 4 DataTexture,
// audio via a 16-slot uniform array — everything is dt-eased on the CPU so the
// visuals stay silky even when the engine stream stutters or stops.
import * as THREE from 'three';
import type { EffectParams, FramePayload, RGB } from '../../shared/bridge';
import type {
  EffectContext,
  EffectInstance,
  EffectModule,
  GlobalRenderSettings,
} from '../types';
import { BG_FRAGMENT, BG_VERTEX, POINTS_FRAGMENT, POINTS_VERTEX } from './shaders';

const DEFAULTS = { density: 1, baseSize: 1, flow: 0.4, audioPunch: 0.6 } as const;

const FULL_COUNT = 12000;
const PREVIEW_COUNT = 2500;
const BAND_SLOTS = 16;
const FOV_DEG = 55;
const CAM_Z = 2.2;
const HALF_DEPTH = 0.85;
const BOUNDS_MARGIN = 1.18; // covers camera sway + the ±3° roll
// All swirl frequencies are multiples of 0.01, so flow time can wrap at 200*PI
// without any visible discontinuity (keeps sin() args small on long sessions).
const FLOW_WRAP = 200 * Math.PI;

const clamp = (x: number, lo: number, hi: number): number => (x < lo ? lo : x > hi ? hi : x);

const easeFactor = (dt: number, tau: number): number => 1 - Math.exp(-dt / tau);

function readNumber(params: EffectParams, key: string, fallback: number): number {
  const v = params[key];
  return typeof v === 'number' && Number.isFinite(v) ? v : fallback;
}

/** Resample a variable-length 0..1 band array into a fixed 16-slot array by fraction. */
function resampleBands(src: number[], dst: Float32Array): void {
  const n = src.length;
  if (n === 0) {
    dst.fill(0);
    return;
  }
  for (let i = 0; i < dst.length; i++) {
    const f = (i / (dst.length - 1)) * (n - 1);
    const lo = Math.floor(f);
    const hi = Math.min(n - 1, lo + 1);
    const w = f - lo;
    dst[i] = clamp((src[lo] ?? 0) * (1 - w) + (src[hi] ?? 0) * w, 0, 1);
  }
}

/** Resample one edge row (variable 4-16 zones) into `zones` rgb triples (0..1). */
function fillRowTarget(row: RGB[], target: Float32Array, rowIndex: number, zones: number): void {
  const n = row.length;
  for (let i = 0; i < zones; i++) {
    const o = (rowIndex * zones + i) * 3;
    if (n === 0) {
      target[o] = target[o + 1] = target[o + 2] = 0;
      continue;
    }
    const f = zones === 1 ? 0 : (i / (zones - 1)) * (n - 1);
    const lo = Math.floor(f);
    const hi = Math.min(n - 1, lo + 1);
    const w = f - lo;
    const a = row[lo] ?? [0, 0, 0];
    const b = row[hi] ?? a;
    target[o] = clamp((a[0] * (1 - w) + b[0] * w) / 255, 0, 1);
    target[o + 1] = clamp((a[1] * (1 - w) + b[1] * w) / 255, 0, 1);
    target[o + 2] = clamp((a[2] * (1 - w) + b[2] * w) / 255, 0, 1);
  }
}

function hslToRgb(h: number, s: number, l: number): [number, number, number] {
  const k = (n: number) => (n + h * 12) % 12;
  const a = s * Math.min(l, 1 - l);
  const f = (n: number) => l - a * Math.max(-1, Math.min(k(n) - 3, Math.min(9 - k(n), 1)));
  return [f(0), f(8), f(4)];
}

/** Pre-frame palette: a calm indigo/violet field so the effect is pretty in a bare browser. */
function seedDefaultPalette(target: Float32Array, zones: number): void {
  const rowHues = [235, 265, 210, 285]; // top, bottom, left, right
  for (let row = 0; row < 4; row++) {
    for (let i = 0; i < zones; i++) {
      const t = zones === 1 ? 0 : i / (zones - 1);
      const [r, g, b] = hslToRgb(((rowHues[row] + t * 30) % 360) / 360, 0.55, 0.3);
      const o = (row * zones + i) * 3;
      target[o] = r;
      target[o + 1] = g;
      target[o + 2] = b;
    }
  }
}

/** Static attributes: home position + seed + (size factor, band affinity, palette coord, hue jitter). */
function buildGeometry(count: number): THREE.BufferGeometry {
  const geo = new THREE.BufferGeometry();
  const pos = new Float32Array(count * 3);
  const seed = new Float32Array(count * 4);
  const props = new Float32Array(count * 4);
  for (let i = 0; i < count; i++) {
    pos[i * 3] = Math.random() * 2 - 1;
    pos[i * 3 + 1] = Math.random() * 2 - 1;
    pos[i * 3 + 2] = Math.random() * 2 - 1;
    for (let c = 0; c < 4; c++) seed[i * 4 + c] = Math.random();
    props[i * 4] = 0.45 + 1.15 * Math.pow(Math.random(), 1.6); // many small, few large
    props[i * 4 + 1] = Math.random(); // band affinity
    props[i * 4 + 2] = Math.random(); // palette coord (dominant-blend bias)
    props[i * 4 + 3] = Math.random(); // hue jitter
  }
  geo.setAttribute('position', new THREE.BufferAttribute(pos, 3));
  geo.setAttribute('aSeed', new THREE.BufferAttribute(seed, 4));
  geo.setAttribute('aProps', new THREE.BufferAttribute(props, 4));
  geo.boundingSphere = new THREE.Sphere(new THREE.Vector3(), 1e5);
  return geo;
}

interface PointsUniforms {
  uFlowTime: { value: number };
  uDriftTime: { value: number };
  uFlowAmp: { value: number };
  uKick: { value: number };
  uBounds: { value: THREE.Vector3 };
  uFlip: { value: THREE.Vector2 };
  uPointScale: { value: number };
  uSizeBase: { value: number };
  uMaxPoint: { value: number };
  uPulse: { value: number };
  uLift: { value: number };
  uEdges: { value: THREE.DataTexture };
  uDominant: { value: THREE.Color };
  uBands: { value: Float32Array };
  uBrightness: { value: number };
  [uniform: string]: THREE.IUniform;
}

interface BgUniforms {
  uDominant: { value: THREE.Color };
  uBrightness: { value: number };
  uAspect: { value: THREE.Vector2 };
  [uniform: string]: THREE.IUniform;
}

class ParticlesInstance implements EffectInstance {
  private readonly renderer: THREE.WebGLRenderer;
  private readonly scene = new THREE.Scene();
  private readonly camera: THREE.PerspectiveCamera;
  private readonly points: THREE.Points;
  private readonly pointsMaterial: THREE.ShaderMaterial;
  private readonly bgGeometry: THREE.BufferGeometry;
  private readonly bgMaterial: THREE.ShaderMaterial;
  private readonly uniforms: PointsUniforms;
  private readonly bgUniforms: BgUniforms;
  private readonly previewMode: boolean;
  private readonly dprCap: number;
  /** Shared between points + background uniforms (mutated in place). */
  private readonly dominantColor = new THREE.Color(0.25, 0.28, 0.45);

  // Edge palette texture state (rebuilt when zone count changes).
  private zones = 0;
  private edgeTex!: THREE.DataTexture;
  private edgeData!: Uint8Array;
  private edgesCur!: Float32Array;
  private edgesTarget!: Float32Array;
  private edgesDirty = true;

  // Audio state: targets from onFrame, eased in render.
  private readonly bandsCur = new Float32Array(BAND_SLOTS);
  private readonly bandsTarget = new Float32Array(BAND_SLOTS);
  private readonly dominantTarget = new Float32Array([0.25, 0.28, 0.45]);
  private audioIntensityTarget = 0;
  private audioIntensityCur = 0;
  private bassCur = 0;
  private prevFrameBass = 0;
  private kickEnv = 0;
  private kickCur = 0;

  // Params (eased where a pop would show).
  private density: number = DEFAULTS.density;
  private baseSizeTarget: number = DEFAULTS.baseSize;
  private baseSizeCur: number = DEFAULTS.baseSize;
  private flowTarget: number = DEFAULTS.flow;
  private flowCur: number = DEFAULTS.flow;
  private audioPunch: number = DEFAULTS.audioPunch;

  // Globals.
  private intensityTarget = 1;
  private intensityCur = 1;
  private brightnessTarget = 1;
  private brightnessCur = 1;

  private flowTime = Math.random() * FLOW_WRAP;
  private driftTime = 0;
  private particleCount: number;
  private cssWidth: number;
  private cssHeight: number;

  constructor(ctx: EffectContext) {
    this.previewMode = ctx.preview;
    this.dprCap = ctx.preview ? 1 : 1.5;

    this.renderer = new THREE.WebGLRenderer({
      canvas: ctx.canvas,
      antialias: false,
      powerPreference: 'high-performance',
      alpha: false,
      stencil: false,
      depth: false,
    });
    this.renderer.setClearColor(0x05060a, 1);

    this.camera = new THREE.PerspectiveCamera(FOV_DEG, 1, 0.1, 10);
    this.camera.position.set(0, 0, CAM_Z);

    this.ensureEdgeTexture(8);

    const relation = ctx.windowConfig?.relation ?? 'none';
    const flip = new THREE.Vector2(
      relation === 'left' || relation === 'right' ? 1 : 0,
      relation === 'above' || relation === 'below' ? 1 : 0,
    );

    this.uniforms = {
      uFlowTime: { value: this.flowTime },
      uDriftTime: { value: 0 },
      uFlowAmp: { value: 0.08 },
      uKick: { value: 0 },
      uBounds: { value: new THREE.Vector3(2, 2, HALF_DEPTH) },
      uFlip: { value: flip },
      uPointScale: { value: 500 },
      uSizeBase: { value: 0.045 },
      uMaxPoint: { value: ctx.preview ? 64 : 110 },
      uPulse: { value: 0 },
      uLift: { value: 0 },
      uEdges: { value: this.edgeTex },
      uDominant: { value: this.dominantColor },
      uBands: { value: this.bandsCur },
      uBrightness: { value: 1 },
    };

    this.pointsMaterial = new THREE.ShaderMaterial({
      uniforms: this.uniforms,
      vertexShader: POINTS_VERTEX,
      fragmentShader: POINTS_FRAGMENT,
      transparent: true,
      blending: THREE.AdditiveBlending,
      depthTest: false,
      depthWrite: false,
    });

    this.particleCount = this.desiredCount();
    this.points = new THREE.Points(buildGeometry(this.particleCount), this.pointsMaterial);
    this.points.frustumCulled = false;
    this.scene.add(this.points);

    // Near-black radial vignette backdrop (opaque, drawn before the points).
    this.bgUniforms = {
      uDominant: { value: this.dominantColor },
      uBrightness: { value: 1 },
      uAspect: { value: new THREE.Vector2(1, 1) },
    };
    this.bgMaterial = new THREE.ShaderMaterial({
      uniforms: this.bgUniforms,
      vertexShader: BG_VERTEX,
      fragmentShader: BG_FRAGMENT,
      depthTest: false,
      depthWrite: false,
    });
    this.bgGeometry = new THREE.BufferGeometry();
    this.bgGeometry.setAttribute(
      'position',
      new THREE.BufferAttribute(new Float32Array([-1, -1, 0, 3, -1, 0, -1, 3, 0]), 3),
    );
    const bgMesh = new THREE.Mesh(this.bgGeometry, this.bgMaterial);
    bgMesh.frustumCulled = false;
    bgMesh.renderOrder = -1;
    this.scene.add(bgMesh);

    this.cssWidth = ctx.canvas.clientWidth || ctx.canvas.width || 640;
    this.cssHeight = ctx.canvas.clientHeight || ctx.canvas.height || 360;
    this.resize(this.cssWidth, this.cssHeight);
  }

  private desiredCount(): number {
    const base = this.previewMode ? PREVIEW_COUNT : FULL_COUNT;
    return Math.max(400, Math.round(base * this.density));
  }

  private ensureEdgeTexture(zones: number): void {
    const z = clamp(Math.round(zones) || 8, 2, 32);
    if (z === this.zones) return;
    this.zones = z;
    this.edgesCur = new Float32Array(z * 4 * 3);
    this.edgesTarget = new Float32Array(z * 4 * 3);
    seedDefaultPalette(this.edgesCur, z);
    this.edgesTarget.set(this.edgesCur);
    this.edgeData = new Uint8Array(z * 4 * 4);
    this.bakeEdges();
    const tex = new THREE.DataTexture(this.edgeData, z, 4, THREE.RGBAFormat, THREE.UnsignedByteType);
    tex.minFilter = THREE.LinearFilter;
    tex.magFilter = THREE.LinearFilter;
    tex.wrapS = THREE.ClampToEdgeWrapping;
    tex.wrapT = THREE.ClampToEdgeWrapping;
    tex.needsUpdate = true;
    if (this.edgeTex !== undefined) this.edgeTex.dispose();
    this.edgeTex = tex;
    if (this.uniforms !== undefined) this.uniforms.uEdges.value = tex;
    this.edgesDirty = true;
  }

  private bakeEdges(): void {
    const n = this.zones * 4;
    for (let i = 0; i < n; i++) {
      this.edgeData[i * 4] = Math.round(this.edgesCur[i * 3] * 255);
      this.edgeData[i * 4 + 1] = Math.round(this.edgesCur[i * 3 + 1] * 255);
      this.edgeData[i * 4 + 2] = Math.round(this.edgesCur[i * 3 + 2] * 255);
      this.edgeData[i * 4 + 3] = 255;
    }
  }

  onFrame(frame: FramePayload): void {
    this.ensureEdgeTexture(frame.edges.top.length || 8);
    fillRowTarget(frame.edges.top, this.edgesTarget, 0, this.zones);
    fillRowTarget(frame.edges.bottom, this.edgesTarget, 1, this.zones);
    fillRowTarget(frame.edges.left, this.edgesTarget, 2, this.zones);
    fillRowTarget(frame.edges.right, this.edgesTarget, 3, this.zones);
    this.dominantTarget[0] = clamp(frame.dominant[0] / 255, 0, 1);
    this.dominantTarget[1] = clamp(frame.dominant[1] / 255, 0, 1);
    this.dominantTarget[2] = clamp(frame.dominant[2] / 255, 0, 1);

    resampleBands(frame.audio.bands, this.bandsTarget);
    this.audioIntensityTarget = clamp(frame.audio.intensity, 0, 1);

    // Onset detection on the raw low-end: a sharp bass rise charges the kick envelope.
    const bass =
      (this.bandsTarget[0] + this.bandsTarget[1] + this.bandsTarget[2] + this.bandsTarget[3]) / 4;
    const jump = bass - this.prevFrameBass;
    if (jump > 0.08) this.kickEnv = Math.min(1, this.kickEnv + jump * 2.5);
    this.prevFrameBass = bass;
  }

  render(timeMs: number, dtMs: number): void {
    const dt = clamp(dtMs, 0, 100) / 1000;
    const t = timeMs / 1000;

    // --- easing: audio (fast attack / slow release), palette, params, globals ---
    for (let i = 0; i < BAND_SLOTS; i++) {
      const target = this.bandsTarget[i];
      const k = easeFactor(dt, target > this.bandsCur[i] ? 0.05 : 0.2);
      this.bandsCur[i] += (target - this.bandsCur[i]) * k;
    }
    const bassTarget =
      (this.bandsCur[0] + this.bandsCur[1] + this.bandsCur[2] + this.bandsCur[3]) / 4;
    this.bassCur += (bassTarget - this.bassCur) * easeFactor(dt, 0.1);
    this.audioIntensityCur +=
      (this.audioIntensityTarget - this.audioIntensityCur) * easeFactor(dt, 0.25);

    this.kickEnv *= Math.exp(-dt / 0.35);
    this.kickCur +=
      (this.kickEnv - this.kickCur) * easeFactor(dt, this.kickCur < this.kickEnv ? 0.045 : 0.2);

    const kEdges = easeFactor(dt, 0.2);
    let maxDelta = 0;
    for (let i = 0; i < this.edgesCur.length; i++) {
      const d = (this.edgesTarget[i] - this.edgesCur[i]) * kEdges;
      this.edgesCur[i] += d;
      const ad = Math.abs(d);
      if (ad > maxDelta) maxDelta = ad;
    }
    if (maxDelta > 0.0008 || this.edgesDirty) {
      this.bakeEdges();
      this.edgeTex.needsUpdate = true;
      this.edgesDirty = false;
    }
    const kDom = easeFactor(dt, 0.3);
    this.dominantColor.r += (this.dominantTarget[0] - this.dominantColor.r) * kDom;
    this.dominantColor.g += (this.dominantTarget[1] - this.dominantColor.g) * kDom;
    this.dominantColor.b += (this.dominantTarget[2] - this.dominantColor.b) * kDom;

    const kParam = easeFactor(dt, 0.15);
    this.baseSizeCur += (this.baseSizeTarget - this.baseSizeCur) * kParam;
    this.flowCur += (this.flowTarget - this.flowCur) * kParam;
    this.intensityCur += (this.intensityTarget - this.intensityCur) * kParam;
    this.brightnessCur += (this.brightnessTarget - this.brightnessCur) * kParam;

    // --- motion integration: base drift always alive, bass/intensity add surge ---
    const punch = this.audioPunch * this.intensityCur;
    const motionScale = 0.25 + 0.75 * this.intensityCur;
    const bassBoost = 1 + (0.9 * this.bassCur + 0.5 * this.kickCur) * punch;
    this.flowTime = (this.flowTime + dt * (0.18 + 1.4 * this.flowCur) * motionScale * bassBoost) % FLOW_WRAP;
    this.driftTime += dt * 0.035 * motionScale * (1 + 0.5 * this.bassCur * punch);

    const halfY = this.uniforms.uBounds.value.y;
    this.uniforms.uFlowTime.value = this.flowTime;
    this.uniforms.uDriftTime.value = this.driftTime;
    this.uniforms.uFlowAmp.value =
      (0.05 + 0.16 * this.flowCur) * (0.35 + 0.65 * this.intensityCur) *
      (1 + 0.5 * this.bassCur * punch) * (halfY * 0.5);
    this.uniforms.uKick.value = this.kickCur * this.audioPunch * (0.3 + 0.7 * this.intensityCur);
    this.uniforms.uPulse.value = punch;
    this.uniforms.uLift.value = punch * (0.6 + 1.2 * this.audioIntensityCur);
    this.uniforms.uSizeBase.value = 0.045 * this.baseSizeCur;
    this.uniforms.uBrightness.value = this.brightnessCur;
    this.bgUniforms.uBrightness.value = this.brightnessCur;

    // Barely-perceptible camera drift + roll (~0.0065 rad/s peak) for parallax life.
    this.camera.position.x = 0.07 * Math.sin(t * 0.05);
    this.camera.position.y = 0.05 * Math.sin(t * 0.073 + 1.7);
    this.camera.rotation.z = 0.05 * Math.sin(t * 0.13);

    if (this.cssWidth > 0 && this.cssHeight > 0) {
      this.renderer.render(this.scene, this.camera);
    }
  }

  setParams(params: EffectParams): void {
    this.density = clamp(readNumber(params, 'density', DEFAULTS.density), 0.25, 2);
    this.baseSizeTarget = clamp(readNumber(params, 'baseSize', DEFAULTS.baseSize), 0.5, 2);
    this.flowTarget = clamp(readNumber(params, 'flow', DEFAULTS.flow), 0, 1);
    this.audioPunch = clamp(readNumber(params, 'audioPunch', DEFAULTS.audioPunch), 0, 1);

    const count = this.desiredCount();
    if (count !== this.particleCount) {
      const old = this.points.geometry;
      this.points.geometry = buildGeometry(count);
      old.dispose();
      this.particleCount = count;
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

    const aspect = width / height;
    this.camera.aspect = aspect;
    this.camera.updateProjectionMatrix();

    // Wrap box sized to the frustum at the FAR particle plane (+ margin) so the
    // mod-space never shows empty borders, even with the camera sway/roll.
    const halfFovRad = (FOV_DEG * Math.PI) / 360;
    const halfH = Math.tan(halfFovRad) * (CAM_Z + HALF_DEPTH) * BOUNDS_MARGIN;
    this.uniforms.uBounds.value.set(halfH * aspect, halfH, HALF_DEPTH);
    this.uniforms.uPointScale.value = (height * dpr * 0.5) / Math.tan(halfFovRad);
    this.uniforms.uMaxPoint.value = (this.previewMode ? 64 : 110) * dpr;
    this.bgUniforms.uAspect.value.set(aspect, 1);
  }

  dispose(): void {
    this.points.geometry.dispose();
    this.pointsMaterial.dispose();
    this.bgGeometry.dispose();
    this.bgMaterial.dispose();
    this.edgeTex.dispose();
    this.renderer.dispose();
    this.renderer.forceContextLoss(); // release the WebGL context now (gallery churns contexts; Chromium caps ~16)
  }
}

const particles: EffectModule = {
  id: 'particles',
  name: 'Particle Field',
  description:
    'Thousands of GPU particles drifting through screen colors and pulsing with the beat.',
  params: [
    { key: 'density', label: 'Density', type: 'range', min: 0.25, max: 2, step: 0.05, default: DEFAULTS.density },
    { key: 'baseSize', label: 'Particle size', type: 'range', min: 0.5, max: 2, step: 0.05, default: DEFAULTS.baseSize },
    { key: 'flow', label: 'Flow', type: 'range', min: 0, max: 1, step: 0.01, default: DEFAULTS.flow },
    { key: 'audioPunch', label: 'Audio punch', type: 'range', min: 0, max: 1, step: 0.01, default: DEFAULTS.audioPunch },
  ],
  create: (ctx: EffectContext): EffectInstance => new ParticlesInstance(ctx),
};

export default particles;
