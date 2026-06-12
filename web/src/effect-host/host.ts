// effect-host/host.ts — owns the active EffectInstance for one effect window:
// windowConfig adoption, effect swaps, settings application, frame fan-in, and
// crash isolation around render. No DOM/loop concerns beyond the canvas it is
// given; main.ts wires it to the bridge and the RenderLoop.

import type {
  ApplicationSettings,
  ConfigPayload,
  FramePayload,
  WindowConfigPayload,
} from '../shared/bridge';
import { getEffect } from '../effects/registry';
import { defaultParamsOf } from '../effects/types';
import type { EffectInstance, EffectModule } from '../effects/types';

const MAX_CONSECUTIVE_RENDER_FAILURES = 30;
const DEFAULT_MAX_FPS = 60;
/** Log a throwing onFrame only once per this many occurrences (60fps stream). */
const FRAME_ERROR_LOG_EVERY = 300;

export class EffectHost {
  /** Wired by main.ts: pushes the validated FPS cap into the render loop. */
  onMaxFpsChange: ((fps: number) => void) | null = null;
  /** Wired by main.ts: permanently stops the render loop. */
  onFatalError: (() => void) | null = null;
  /** Wired by main.ts: reports failures to the engine so it can toast (NFR5/AC7). */
  onReportError: ((source: string, message: string) => void) | null = null;

  private monitorId: string | null;
  /** Resolved id (after registry fallback) of the running effect. */
  private effectId: string | null = null;
  private module: EffectModule | null = null;
  private instance: EffectInstance | null = null;
  private windowConfig: WindowConfigPayload | null = null;
  private settings: ApplicationSettings | null = null;
  private consecutiveRenderFailures = 0;
  private frameErrorCount = 0;

  constructor(
    private canvas: HTMLCanvasElement,
    /** Element whose CSS box defines the effect surface size (#root). */
    private readonly sizeSource: HTMLElement,
    initialMonitorId: string | null,
  ) {
    this.monitorId = initialMonitorId;
  }

  hasInstance(): boolean {
    return this.instance !== null;
  }

  /** Adopt a window assignment if it is for us (or we are still unassigned). */
  handleWindowConfig(cfg: WindowConfigPayload): void {
    if (this.monitorId !== null && cfg.monitorId !== this.monitorId) return;
    this.monitorId = cfg.monitorId;
    const prev = this.windowConfig;
    this.windowConfig = cfg;
    const resolvedId = getEffect(cfg.effectId).id; // registry falls back to the default effect
    // Effects receive windowConfig only at create(); a layout/source change with the SAME
    // effect must therefore recreate the instance or it keeps spilling from a stale side (FR7).
    const layoutChanged =
      prev !== null &&
      (prev.relation !== cfg.relation ||
        prev.source?.id !== cfg.source?.id ||
        prev.monitor?.x !== cfg.monitor?.x ||
        prev.monitor?.y !== cfg.monitor?.y ||
        prev.monitor?.width !== cfg.monitor?.width ||
        prev.monitor?.height !== cfg.monitor?.height);
    if (this.instance === null || resolvedId !== this.effectId || layoutChanged) {
      this.swapTo(cfg.effectId);
    }
  }

  handleConfig(payload: ConfigPayload): void {
    this.settings = payload.settings;
    const maxFps = payload.settings.maxFps;
    this.onMaxFpsChange?.(Number.isFinite(maxFps) && maxFps >= 1 ? maxFps : DEFAULT_MAX_FPS);

    // Defensive consistency: if the engine recorded a per-monitor override for
    // us that never arrived as a windowConfig re-push, follow the settings.
    if (this.monitorId !== null && this.instance !== null) {
      const override = payload.settings.effectByMonitorId[this.monitorId];
      if (override !== undefined && getEffect(override).id !== this.effectId) {
        this.swapTo(override); // swapTo re-applies globals/params from settings
        return;
      }
    }
    this.applySettingsToInstance();
  }

  handleFrame(frame: FramePayload): void {
    if (this.instance === null) return;
    try {
      this.instance.onFrame(frame);
    } catch (err) {
      if (this.frameErrorCount % FRAME_ERROR_LOG_EVERY === 0) {
        console.error('[effect-host] effect onFrame threw', err);
      }
      this.frameErrorCount += 1;
    }
  }

  /** Called by the RenderLoop. Never throws; stops the loop after 30 straight failures. */
  renderSafe(timeMs: number, dtMs: number): void {
    if (this.instance === null) return;
    try {
      this.instance.render(timeMs, dtMs);
      this.consecutiveRenderFailures = 0;
    } catch (err) {
      this.consecutiveRenderFailures += 1;
      console.error(
        `[effect-host] render failed (${this.consecutiveRenderFailures}/${MAX_CONSECUTIVE_RENDER_FAILURES})`,
        err,
      );
      if (this.consecutiveRenderFailures >= MAX_CONSECUTIVE_RENDER_FAILURES) {
        console.error(
          '[effect-host] too many consecutive render failures — stopping the render loop',
        );
        this.onReportError?.('effect-render', `Effect "${this.effectId ?? 'unknown'}" stopped: ${String(err)}`);
        this.onFatalError?.();
      }
    }
  }

  /** CSS pixel size; effects handle devicePixelRatio themselves. */
  resize(width: number, height: number): void {
    if (this.instance === null || width <= 0 || height <= 0) return;
    try {
      this.instance.resize(width, height);
    } catch (err) {
      console.error('[effect-host] effect resize threw', err);
    }
  }

  dispose(): void {
    this.disposeInstance();
    this.module = null;
    this.effectId = null;
  }

  private swapTo(effectId: string): void {
    this.disposeInstance();
    // Each effect's dispose() calls renderer.forceContextLoss(), which permanently kills
    // the WebGL context bound to this canvas element — reusing it for the next effect makes
    // getContext() return the lost context and three crashes probing gl.getShaderPrecisionFormat()
    // ("Cannot read properties of null (reading 'precision')"). A fresh canvas per swap avoids it.
    this.replaceCanvas();
    const module = getEffect(effectId);
    let instance: EffectInstance;
    try {
      instance = module.create({
        canvas: this.canvas,
        windowConfig: this.windowConfig,
        preview: false,
      });
    } catch (err) {
      console.error(`[effect-host] failed to create effect "${module.id}"`, err);
      this.onReportError?.('effect-create', `Effect "${module.id}" failed to start: ${String(err)}`);
      this.module = null;
      this.effectId = null;
      return;
    }
    this.module = module;
    this.effectId = module.id;
    this.instance = instance;
    this.consecutiveRenderFailures = 0;
    this.frameErrorCount = 0;
    this.applySettingsToInstance();
    this.resize(this.sizeSource.clientWidth, this.sizeSource.clientHeight);
  }

  /** Replace the (possibly context-lost) canvas with a pristine one in the same DOM slot.
   * CSS `#root canvas` sizes it; the ResizeObserver watches #root, not the canvas, so swapping
   * the element is transparent to the rest of the host. */
  private replaceCanvas(): void {
    const fresh = document.createElement('canvas');
    if (this.canvas.parentNode !== null) {
      this.canvas.replaceWith(fresh);
    } else {
      this.sizeSource.appendChild(fresh);
    }
    this.canvas = fresh;
  }

  /** Swap must fully release the old instance even if its dispose throws. */
  private disposeInstance(): void {
    const old = this.instance;
    this.instance = null;
    if (old === null) return;
    try {
      old.dispose();
    } catch (err) {
      console.error('[effect-host] dispose of the previous effect threw', err);
    }
  }

  private applySettingsToInstance(): void {
    if (this.instance === null || this.module === null || this.effectId === null) return;
    const s = this.settings;
    try {
      // No settings yet (windowConfig can land before config): safe defaults.
      this.instance.setGlobals(
        s
          ? { intensity: s.globalIntensity, brightness: s.brightness }
          : { intensity: 1, brightness: 1 },
      );
      const merged = defaultParamsOf(this.module);
      const overrides = s?.effectParamsById[this.effectId];
      if (overrides) Object.assign(merged, overrides);
      this.instance.setParams(merged);
    } catch (err) {
      console.error('[effect-host] applying settings to the effect threw', err);
    }
  }
}
