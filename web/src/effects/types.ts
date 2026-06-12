// The effect-module contract (FR6/NFR8): adding an effect = one self-contained
// folder under src/effects/<id>/ + a registry/manifest entry. No engine changes.
import type { EffectParams, FramePayload, WindowConfigPayload } from '../shared/bridge';

/** Render-time globals every effect must respect. */
export interface GlobalRenderSettings {
  /** 0..1 master effect intensity (motion/reactivity scale). */
  intensity: number;
  /** 0..1 output brightness multiplier. */
  brightness: number;
}

/** Declarative parameter definition — the control UI auto-generates controls from these. */
export interface ParamDef {
  key: string;
  label: string;
  type: 'range' | 'select' | 'toggle';
  /** range only */
  min?: number;
  max?: number;
  step?: number;
  /** select only */
  options?: { value: string; label: string }[];
  default: number | string | boolean;
}

export interface EffectContext {
  canvas: HTMLCanvasElement;
  /** Monitor/layout info; null in gallery previews and browser dev. */
  windowConfig: WindowConfigPayload | null;
  /** True when rendering as a small gallery preview — prefer cheaper quality settings. */
  preview: boolean;
}

export interface EffectInstance {
  /** Latest engine data (called up to MaxFps; store it, don't render here). */
  onFrame(frame: FramePayload): void;
  /** Render one frame. Called from the host's rAF loop (already FPS-capped). */
  render(timeMs: number, dtMs: number): void;
  setParams(params: EffectParams): void;
  setGlobals(globals: GlobalRenderSettings): void;
  /** CSS pixel size; implementations handle devicePixelRatio themselves. */
  resize(width: number, height: number): void;
  dispose(): void;
}

export interface EffectModule {
  id: string;
  name: string;
  description: string;
  params: ParamDef[];
  create(ctx: EffectContext): EffectInstance;
}

/** Helper: defaults extracted from a module's param definitions. */
export function defaultParamsOf(module: EffectModule): EffectParams {
  const out: EffectParams = {};
  for (const def of module.params) out[def.key] = def.default;
  return out;
}
