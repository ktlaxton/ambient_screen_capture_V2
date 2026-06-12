// effect-host/fps-overlay.ts — tiny debug overlay, enabled via ?fps=1.
// Shows measured fps vs the configured cap, refreshed twice per second.

export interface FpsSource {
  /** Monotonic count of frames actually rendered. */
  framesRendered: number;
  getMaxFps(): number;
}

const UPDATE_INTERVAL_MS = 500;

export class FpsOverlay {
  private readonly el: HTMLDivElement;
  private readonly timer: number;
  private lastFrames: number;
  private lastTimeMs: number;

  constructor(parent: HTMLElement, private readonly source: FpsSource) {
    this.el = document.createElement('div');
    const style = this.el.style;
    style.position = 'fixed';
    style.top = '8px';
    style.left = '8px';
    style.zIndex = '10';
    style.padding = '2px 8px';
    style.font = '12px/18px monospace';
    style.color = '#9f9';
    style.background = 'rgba(0, 0, 0, 0.6)';
    style.borderRadius = '4px';
    style.pointerEvents = 'none';
    this.el.textContent = '-- / -- fps';
    parent.appendChild(this.el);

    this.lastFrames = source.framesRendered;
    this.lastTimeMs = performance.now();
    this.timer = window.setInterval(() => this.update(), UPDATE_INTERVAL_MS);
  }

  dispose(): void {
    window.clearInterval(this.timer);
    this.el.remove();
  }

  private update(): void {
    const now = performance.now();
    const frames = this.source.framesRendered;
    const elapsed = now - this.lastTimeMs;
    const fps = elapsed > 0 ? ((frames - this.lastFrames) * 1000) / elapsed : 0;
    this.lastFrames = frames;
    this.lastTimeMs = now;
    this.el.textContent = `${fps.toFixed(1)} / ${this.source.getMaxFps()} fps`;
  }
}
