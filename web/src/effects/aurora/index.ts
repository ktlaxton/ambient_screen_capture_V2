// Aurora (FR6): northern-lights curtains on a fullscreen triangle. The lower
// borders drift via fbm, rays shimmer with treble, bass swells the sway and
// sends a brightness surge traveling along each curtain. Curtain bases take
// their colors from the source monitor's relevant edge (via a zones x 1
// DataTexture, eased CPU-side); the tint param crossfades toward the classic
// teal-green/violet aurora palette. Single pass, no postprocessing.
import * as THREE from 'three';
import type { EffectParams, FramePayload, MonitorRelation, RGB } from '../../shared/bridge';
import type {
  EffectContext,
  EffectInstance,
  EffectModule,
  GlobalRenderSettings,
} from '../types';
import { FRAGMENT_SHADER, VERTEX_SHADER } from './shaders';

const DEFAULTS = { ribbons: 4, sway: 0.5, tint: 0.35, audioDrive: 0.5 } as const;

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

/** The source edge whose colors feed the curtain bases, given monitor layout. */
function pickEdge(relation: MonitorRelation, frame: FramePayload): RGB[] {
  switch (relation) {
    case 'right':
      return frame.edges.right;
    case 'left':
      // Reversed so the colors nearest the source land on the adjoining side.
      return [...frame.edges.left].reverse();
    case 'above':
      return frame.edges.top;
    default:
      // 'below' and 'none': aurora bases sit at the bottom -> bottom edge.
      return frame.edges.bottom;
  }
}

/** Seed palette shown until the first engine frame arrives: dim teal -> violet. */
function seedPalette(out: Float32Array, zones: number): void {
  for (let i = 0; i < zones; i++) {
    const t = zones > 1 ? i / (zones - 1) : 0;
    out[i * 3 + 0] = (0.212 + (0.478 - 0.212) * t) * 0.55;
    out[i * 3 + 1] = (0.91 + (0.31 - 0.91) * t) * 0.55;
    out[i * 3 + 2] = (0.627 + (0.847 - 0.627) * t) * 0.55;
  }
}

interface AuroraUniforms {
  uResolution: { value: THREE.Vector2 };
  uTime: { value: number };
  uDrift: { value: number };
  uRibbons: { value: number };
  uSway: { value: number };
  uTint: { value: number };
  uShimmer: { value: number };
  uSurge: { value: number };
  uSurgePhase: { value: number };
  uBrightness: { value: number };
  uEdge: { value: THREE.DataTexture };
  uDominant: { value: THREE.Vector3 };
  [uniform: string]: THREE.IUniform;
}

class AuroraInstance implements EffectInstance {
  private readonly renderer: THREE.WebGLRenderer;
  private readonly scene = new THREE.Scene();
  private readonly camera = new THREE.OrthographicCamera();
  private readonly geometry: THREE.BufferGeometry;
  private readonly material: THREE.ShaderMaterial;
  private readonly uniforms: AuroraUniforms;
  private readonly dprCap: number;
  private readonly relation: MonitorRelation;

  // Edge palette: eased current + frame targets (rgb packed 0..1) -> DataTexture.
  private zones = 8;
  private edgeCur: Float32Array;
  private edgeTarget: Float32Array;
  private edgeData: Uint8Array;
  private edgeTexture: THREE.DataTexture;
  private edgeDirty = true;

  private readonly dominantCur = new THREE.Vector3(0.2, 0.6, 0.5);
  private readonly dominantTarget = new THREE.Vector3(0.2, 0.6, 0.5);

  // Audio (targets set in onFrame, eased in render).
  private bassTarget = 0;
  private bassCur = 0;
  private trebleTarget = 0;
  private trebleCur = 0;

  // Params (sliders eased so live tweaks don't pop; ribbons is discrete).
  private ribbons: number = DEFAULTS.ribbons;
  private swayTarget: number = DEFAULTS.sway;
  private swayCur: number = DEFAULTS.sway;
  private tintTarget: number = DEFAULTS.tint;
  private tintCur: number = DEFAULTS.tint;
  private audioDrive: number = DEFAULTS.audioDrive;

  // Globals.
  private intensityTarget = 1;
  private intensityCur = 1;
  private brightnessTarget = 1;
  private brightnessCur = 1;

  // Accumulated motion clocks (advance speed is audio/intensity modulated).
  private drift = 0;
  private surgePhase = 0;

  private cssWidth: number;
  private cssHeight: number;

  constructor(ctx: EffectContext) {
    this.dprCap = ctx.preview ? 1 : 1.5;
    this.relation = ctx.windowConfig?.relation ?? 'none';

    this.edgeCur = new Float32Array(this.zones * 3);
    this.edgeTarget = new Float32Array(this.zones * 3);
    seedPalette(this.edgeCur, this.zones);
    this.edgeTarget.set(this.edgeCur);
    this.edgeData = new Uint8Array(this.zones * 4);
    this.edgeTexture = this.createEdgeTexture();

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
      uDrift: { value: 0 },
      uRibbons: { value: this.ribbons },
      uSway: { value: this.swayCur },
      uTint: { value: this.tintCur },
      uShimmer: { value: 0 },
      uSurge: { value: 0 },
      uSurgePhase: { value: 0 },
      uBrightness: { value: this.brightnessCur },
      uEdge: { value: this.edgeTexture },
      uDominant: { value: this.dominantCur },
    };

    this.material = new THREE.ShaderMaterial({
      uniforms: this.uniforms,
      vertexShader: VERTEX_SHADER,
      fragmentShader: FRAGMENT_SHADER,
      defines: ctx.preview ? { PATH_OCTAVES: 2, DETAIL: 0 } : { PATH_OCTAVES: 4, DETAIL: 1 },
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

  private createEdgeTexture(): THREE.DataTexture {
    const tex = new THREE.DataTexture(
      this.edgeData,
      this.zones,
      1,
      THREE.RGBAFormat,
      THREE.UnsignedByteType,
    );
    tex.magFilter = THREE.LinearFilter;
    tex.minFilter = THREE.LinearFilter;
    tex.wrapS = THREE.ClampToEdgeWrapping;
    tex.wrapT = THREE.ClampToEdgeWrapping;
    tex.needsUpdate = true;
    return tex;
  }

  /** Zone count changed (engine setting): rebuild buffers + texture, snapping. */
  private rebuildEdge(zones: number): void {
    this.zones = zones;
    this.edgeCur = new Float32Array(zones * 3);
    this.edgeTarget = new Float32Array(zones * 3);
    this.edgeData = new Uint8Array(zones * 4);
    this.edgeTexture.dispose();
    this.edgeTexture = this.createEdgeTexture();
    this.uniforms.uEdge.value = this.edgeTexture;
  }

  onFrame(frame: FramePayload): void {
    const edge = pickEdge(this.relation, frame);
    if (edge.length > 0 && edge.length !== this.zones) {
      this.rebuildEdge(edge.length);
      for (let i = 0; i < edge.length; i++) {
        this.edgeCur[i * 3 + 0] = edge[i][0] / 255;
        this.edgeCur[i * 3 + 1] = edge[i][1] / 255;
        this.edgeCur[i * 3 + 2] = edge[i][2] / 255;
      }
      this.edgeDirty = true;
    }
    for (let i = 0; i < Math.min(edge.length, this.zones); i++) {
      this.edgeTarget[i * 3 + 0] = edge[i][0] / 255;
      this.edgeTarget[i * 3 + 1] = edge[i][1] / 255;
      this.edgeTarget[i * 3 + 2] = edge[i][2] / 255;
    }
    this.dominantTarget.set(
      frame.dominant[0] / 255,
      frame.dominant[1] / 255,
      frame.dominant[2] / 255,
    );

    const bands = frame.audio.bands;
    this.bassTarget = clamp(bandAvg(bands, 0, 0.25) * (0.5 + 0.5 * frame.audio.intensity), 0, 1);
    this.trebleTarget = clamp(bandAvg(bands, 0.75, 1), 0, 1);
  }

  render(timeMs: number, dtMs: number): void {
    const dt = clamp(dtMs, 0, 100) / 1000;

    // Audio easing: fast attack so beats land, slower release so surges bloom.
    this.bassCur += (this.bassTarget - this.bassCur) *
      easeFactor(dt, this.bassTarget > this.bassCur ? 0.06 : 0.3);
    this.trebleCur += (this.trebleTarget - this.trebleCur) * easeFactor(dt, 0.12);

    // Param + global easing.
    const kParam = easeFactor(dt, 0.15);
    this.swayCur += (this.swayTarget - this.swayCur) * kParam;
    this.tintCur += (this.tintTarget - this.tintCur) * kParam;
    this.intensityCur += (this.intensityTarget - this.intensityCur) * kParam;
    this.brightnessCur += (this.brightnessTarget - this.brightnessCur) * kParam;

    // Edge palette + dominant easing; re-upload the texture only when it moved.
    const kEdge = easeFactor(dt, 0.35);
    let maxDelta = 0;
    for (let i = 0; i < this.edgeCur.length; i++) {
      const d = (this.edgeTarget[i] - this.edgeCur[i]) * kEdge;
      this.edgeCur[i] += d;
      const ad = Math.abs(d);
      if (ad > maxDelta) maxDelta = ad;
    }
    if (maxDelta > 0.0012 || this.edgeDirty) {
      for (let i = 0; i < this.zones; i++) {
        this.edgeData[i * 4 + 0] = Math.round(clamp(this.edgeCur[i * 3 + 0], 0, 1) * 255);
        this.edgeData[i * 4 + 1] = Math.round(clamp(this.edgeCur[i * 3 + 1], 0, 1) * 255);
        this.edgeData[i * 4 + 2] = Math.round(clamp(this.edgeCur[i * 3 + 2], 0, 1) * 255);
        this.edgeData[i * 4 + 3] = 255;
      }
      this.edgeTexture.needsUpdate = true;
      this.edgeDirty = false;
    }
    this.dominantCur.lerp(this.dominantTarget, kEdge);

    // Motion clocks: intensity scales everything; bass swells the drift.
    const drive = this.audioDrive;
    const motion = 0.15 + 0.85 * this.intensityCur;
    this.drift += dt * (0.1 + 0.45 * this.swayCur) * motion * (1 + 0.9 * this.bassCur * drive);
    this.surgePhase += dt * (0.03 + 0.55 * this.bassCur * drive * this.intensityCur);

    this.uniforms.uTime.value = timeMs / 1000;
    this.uniforms.uDrift.value = this.drift;
    this.uniforms.uRibbons.value = this.ribbons;
    this.uniforms.uSway.value = clamp(
      this.swayCur * (0.35 + 0.65 * this.intensityCur) * (1 + 1.1 * this.bassCur * drive),
      0,
      1.6,
    );
    this.uniforms.uTint.value = this.tintCur;
    this.uniforms.uShimmer.value = clamp(this.trebleCur * 1.8 * drive * this.intensityCur, 0, 1);
    this.uniforms.uSurge.value = clamp(
      this.bassCur * drive * (0.3 + 0.7 * this.intensityCur) * 1.4,
      0,
      1,
    );
    this.uniforms.uSurgePhase.value = this.surgePhase;
    this.uniforms.uBrightness.value = this.brightnessCur;

    if (this.cssWidth > 0 && this.cssHeight > 0) {
      this.renderer.render(this.scene, this.camera);
    }
  }

  setParams(params: EffectParams): void {
    this.ribbons = Math.round(clamp(readNumber(params, 'ribbons', DEFAULTS.ribbons), 2, 6));
    this.swayTarget = clamp(readNumber(params, 'sway', DEFAULTS.sway), 0, 1);
    this.tintTarget = clamp(readNumber(params, 'tint', DEFAULTS.tint), 0, 1);
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
    this.edgeTexture.dispose();
    this.renderer.dispose();
    this.renderer.forceContextLoss(); // release the WebGL context now (gallery churns contexts; Chromium caps ~16)
  }
}

const aurora: EffectModule = {
  id: 'aurora',
  name: 'Aurora',
  description:
    'Northern-lights ribbons that ripple with bass and take their colors from the screen edges.',
  params: [
    { key: 'ribbons', label: 'Ribbons', type: 'range', min: 2, max: 6, step: 1, default: DEFAULTS.ribbons },
    { key: 'sway', label: 'Sway', type: 'range', min: 0, max: 1, step: 0.01, default: DEFAULTS.sway },
    { key: 'tint', label: 'Aurora tint', type: 'range', min: 0, max: 1, step: 0.01, default: DEFAULTS.tint },
    { key: 'audioDrive', label: 'Audio drive', type: 'range', min: 0, max: 1, step: 0.01, default: DEFAULTS.audioDrive },
  ],
  create: (ctx: EffectContext): EffectInstance => new AuroraInstance(ctx),
};

export default aurora;
