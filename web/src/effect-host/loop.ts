// effect-host/loop.ts — the render loop for a fullscreen effect window.
//
// requestAnimationFrame with an accumulator-based FPS cap: rAF already ties
// ticks to the display refresh rate, so the effective cap is
// min(maxFps, display rate). The loop pauses cleanly on document.hidden and
// while inactive (no live effect instance), and resumes without a dt spike by
// re-establishing its time baseline on the first tick back.

export interface RenderLoopOptions {
  /** Render one frame. Must never throw (the host wraps effect errors). */
  render(timeMs: number, dtMs: number): void;
  /** When false the loop idles: no renders, timers reset (no dt spike later). */
  isActive(): boolean;
}

/** dt handed to effects is clamped to this, so a hitch never explodes a sim. */
const MAX_DT_MS = 100;
const DEFAULT_MAX_FPS = 60;
const MAX_FPS_CEILING = 240;

export class RenderLoop {
  /** Total frames actually rendered — read by the FPS overlay. */
  framesRendered = 0;

  private rafId: number | null = null;
  private maxFps = DEFAULT_MAX_FPS;
  private accMs = 0;
  private lastTickMs: number | null = null;
  private lastRenderMs: number | null = null;
  private stopped = false;

  private readonly onVisibility = (): void => {
    if (document.hidden) this.cancel();
    else this.schedule();
  };

  constructor(private readonly options: RenderLoopOptions) {
    document.addEventListener('visibilitychange', this.onVisibility);
  }

  getMaxFps(): number {
    return this.maxFps;
  }

  setMaxFps(fps: number): void {
    this.maxFps =
      Number.isFinite(fps) && fps >= 1 ? Math.min(fps, MAX_FPS_CEILING) : DEFAULT_MAX_FPS;
  }

  start(): void {
    if (this.stopped || document.hidden) return;
    this.schedule();
  }

  /** Permanent stop (fatal render failures). visibilitychange will not revive it. */
  stop(): void {
    this.stopped = true;
    this.cancel();
  }

  dispose(): void {
    this.stop();
    document.removeEventListener('visibilitychange', this.onVisibility);
  }

  private schedule(): void {
    if (this.stopped || this.rafId !== null) return;
    this.resetTimers();
    this.rafId = requestAnimationFrame(this.tick);
  }

  private cancel(): void {
    if (this.rafId !== null) {
      cancelAnimationFrame(this.rafId);
      this.rafId = null;
    }
    this.resetTimers();
  }

  private resetTimers(): void {
    this.lastTickMs = null;
    this.lastRenderMs = null;
    this.accMs = 0;
  }

  private readonly tick = (now: number): void => {
    this.rafId = requestAnimationFrame(this.tick);

    if (!this.options.isActive()) {
      this.resetTimers();
      return;
    }
    if (this.lastTickMs === null) {
      // First tick after start/resume/idle: establish the baseline, render next
      // tick. This is what prevents dt spikes after any pause.
      this.lastTickMs = now;
      return;
    }
    this.accMs += now - this.lastTickMs;
    this.lastTickMs = now;

    const intervalMs = 1000 / this.maxFps;
    // 1ms tolerance: rAF timestamps jitter slightly below the nominal interval;
    // without it a 60fps cap on a 60Hz display drops frames intermittently.
    if (this.accMs < intervalMs - 1) return;
    this.accMs = this.accMs >= intervalMs ? this.accMs % intervalMs : 0;

    const dtMs =
      this.lastRenderMs === null
        ? Math.min(intervalMs, MAX_DT_MS)
        : Math.min(Math.max(now - this.lastRenderMs, 0), MAX_DT_MS);
    this.lastRenderMs = now;

    this.options.render(now, dtMs);
    this.framesRendered += 1;
  };
}
