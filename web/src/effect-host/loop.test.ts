// loop.test.ts — RenderLoop FPS-cap accumulator math, dt clamping, pause/resume
// behavior. rAF is replaced with a manual queue so ticks are driven explicitly
// with exact timestamps (no fake timers needed; the loop is purely rAF-based).
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { RenderLoop } from './loop';

const TICK_60HZ = 1000 / 60;

let rafCallbacks: Map<number, FrameRequestCallback>;
let nextRafId: number;
let loops: RenderLoop[];

/** Fire all currently-pending rAF callbacks with the given timestamp. */
function tick(now: number): void {
  const pending = [...rafCallbacks.values()];
  rafCallbacks.clear();
  for (const cb of pending) cb(now);
}

/** Drive `count` ticks at 60Hz starting from `startMs`; returns the last timestamp. */
function tick60Hz(count: number, startMs = 0): number {
  let now = startMs;
  for (let i = 0; i < count; i++) {
    now = startMs + i * TICK_60HZ;
    tick(now);
  }
  return now;
}

interface Harness {
  loop: RenderLoop;
  renders: { timeMs: number; dtMs: number }[];
  setActive(active: boolean): void;
}

function makeLoop(): Harness {
  const renders: { timeMs: number; dtMs: number }[] = [];
  let active = true;
  const loop = new RenderLoop({
    render: (timeMs, dtMs) => renders.push({ timeMs, dtMs }),
    isActive: () => active,
  });
  loops.push(loop);
  return { loop, renders, setActive: (a) => (active = a) };
}

function setDocumentHidden(hidden: boolean): void {
  Object.defineProperty(document, 'hidden', { configurable: true, get: () => hidden });
}

beforeEach(() => {
  rafCallbacks = new Map();
  nextRafId = 1;
  loops = [];
  vi.stubGlobal('requestAnimationFrame', (cb: FrameRequestCallback): number => {
    const id = nextRafId++;
    rafCallbacks.set(id, cb);
    return id;
  });
  vi.stubGlobal('cancelAnimationFrame', (id: number): void => {
    rafCallbacks.delete(id);
  });
});

afterEach(() => {
  for (const loop of loops) loop.dispose(); // remove visibilitychange listeners
  delete (document as { hidden?: boolean }).hidden; // restore prototype getter
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('scheduling basics', () => {
  it('start schedules a rAF and the first tick only establishes the baseline', () => {
    const h = makeLoop();
    h.loop.start();
    expect(rafCallbacks.size).toBe(1);
    tick(0);
    expect(h.renders).toHaveLength(0); // baseline tick, no render
    expect(rafCallbacks.size).toBe(1); // rescheduled
  });

  it('renders on the second 60Hz tick at the default 60fps cap', () => {
    const h = makeLoop();
    h.loop.start();
    tick(0);
    tick(TICK_60HZ);
    expect(h.renders).toHaveLength(1);
    expect(h.renders[0].timeMs).toBeCloseTo(TICK_60HZ, 5);
    expect(h.renders[0].dtMs).toBeLessThanOrEqual(TICK_60HZ + 0.001);
  });

  it('start is a no-op while the document is hidden', () => {
    setDocumentHidden(true);
    const h = makeLoop();
    h.loop.start();
    expect(rafCallbacks.size).toBe(0);
  });
});

describe('FPS cap accumulator', () => {
  it('a 30fps cap on a 60Hz tick stream renders exactly half the ticks', () => {
    const h = makeLoop();
    h.loop.setMaxFps(30);
    h.loop.start();
    tick60Hz(121); // 1 baseline tick + 120 ticks = 2 seconds at 60Hz
    expect(h.loop.framesRendered).toBe(60);
  });

  it('a 60fps cap on a 60Hz tick stream renders every tick after the baseline', () => {
    const h = makeLoop();
    h.loop.setMaxFps(60);
    h.loop.start();
    tick60Hz(121);
    expect(h.loop.framesRendered).toBe(120);
  });

  it('a cap above the tick rate is limited by the tick rate (rAF-bound)', () => {
    const h = makeLoop();
    h.loop.setMaxFps(240);
    h.loop.start();
    tick60Hz(61);
    expect(h.loop.framesRendered).toBe(60);
  });
});

describe('setMaxFps validation', () => {
  it('keeps a valid value', () => {
    const h = makeLoop();
    h.loop.setMaxFps(30);
    expect(h.loop.getMaxFps()).toBe(30);
  });

  it('falls back to 60 for NaN, Infinity, and values below 1', () => {
    const h = makeLoop();
    for (const bad of [NaN, Infinity, 0, 0.5, -10]) {
      h.loop.setMaxFps(bad);
      expect(h.loop.getMaxFps()).toBe(60);
    }
  });

  it('clamps to the 240fps ceiling', () => {
    const h = makeLoop();
    h.loop.setMaxFps(1000);
    expect(h.loop.getMaxFps()).toBe(240);
  });
});

describe('dt clamping', () => {
  it('clamps dt to 100ms after a long hitch between renders', () => {
    const h = makeLoop();
    h.loop.start();
    tick(0);
    tick(TICK_60HZ); // first render
    tick(5000); // 5s hitch
    expect(h.renders).toHaveLength(2);
    expect(h.renders[1].dtMs).toBe(100);
  });

  it('caps the very first render dt at the frame interval', () => {
    const h = makeLoop();
    h.loop.setMaxFps(30);
    h.loop.start();
    tick(0);
    tick(500); // huge gap straight after the baseline
    expect(h.renders).toHaveLength(1);
    expect(h.renders[0].dtMs).toBeCloseTo(1000 / 30, 5);
  });
});

describe('pause and resume', () => {
  it('idles without rendering while isActive() is false and resets its timers', () => {
    const h = makeLoop();
    h.setActive(false);
    h.loop.start();
    tick60Hz(10);
    expect(h.loop.framesRendered).toBe(0);
    expect(rafCallbacks.size).toBe(1); // still ticking, just idle
  });

  it('resumes from idle without a dt spike', () => {
    const h = makeLoop();
    h.loop.start();
    tick(0);
    tick(TICK_60HZ); // render 1
    h.setActive(false);
    tick(2 * TICK_60HZ); // idle tick: timers reset
    h.setActive(true);
    tick(60_000); // long pause; this tick only re-establishes the baseline
    expect(h.renders).toHaveLength(1);
    tick(60_000 + TICK_60HZ);
    expect(h.renders).toHaveLength(2);
    expect(h.renders[1].dtMs).toBeLessThanOrEqual(TICK_60HZ + 0.001); // no spike
  });

  it('pauses on document hidden and resumes on visible without a dt spike', () => {
    const h = makeLoop();
    h.loop.start();
    tick(0);
    tick(TICK_60HZ); // render 1
    setDocumentHidden(true);
    document.dispatchEvent(new Event('visibilitychange'));
    expect(rafCallbacks.size).toBe(0); // rAF canceled while hidden

    setDocumentHidden(false);
    document.dispatchEvent(new Event('visibilitychange'));
    expect(rafCallbacks.size).toBe(1); // rescheduled

    tick(30_000); // baseline re-established, no render
    expect(h.renders).toHaveLength(1);
    tick(30_000 + TICK_60HZ);
    expect(h.renders).toHaveLength(2);
    expect(h.renders[1].dtMs).toBeLessThanOrEqual(TICK_60HZ + 0.001);
  });
});

describe('stop and dispose', () => {
  it('stop is permanent: start and visibilitychange cannot revive the loop', () => {
    const h = makeLoop();
    h.loop.start();
    tick(0);
    h.loop.stop();
    expect(rafCallbacks.size).toBe(0);
    h.loop.start();
    expect(rafCallbacks.size).toBe(0);
    document.dispatchEvent(new Event('visibilitychange')); // visible -> schedule()
    expect(rafCallbacks.size).toBe(0);
  });

  it('dispose stops the loop and unhooks visibilitychange', () => {
    const removeSpy = vi.spyOn(document, 'removeEventListener');
    const h = makeLoop();
    h.loop.start();
    h.loop.dispose();
    expect(rafCallbacks.size).toBe(0);
    expect(removeSpy).toHaveBeenCalledWith('visibilitychange', expect.any(Function));
  });
});
