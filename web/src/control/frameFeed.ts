// ============================================================================
// frameFeed — the 60Hz engine frame stream, kept OUT of React state.
// Components that visualize live data read getLatestFrame() inside their own
// rAF loops, or subscribe directly (effect previews feed instances this way).
// ============================================================================
import { getBridge } from '../shared/bridge';
import { makeSimFrame } from '../shared/simulator';
import type { FramePayload, RGB } from '../shared/bridge';

type FrameListener = (frame: FramePayload) => void;

/** No engine frame for this long -> synthesize a demo signal so gallery previews and
 * the signal panel stay alive while effects are disabled (incl. all of onboarding, FR8). */
const LIVE_TIMEOUT_MS = 600;
const SYNTH_INTERVAL_MS = 33; // ~30fps is plenty for previews

let latestFrame: FramePayload | null = null;
const listeners = new Set<FrameListener>();
let started = false;
let live = false;
let liveTimer: ReturnType<typeof setTimeout> | null = null;
let synthTimer: ReturnType<typeof setInterval> | null = null;
let synthEpoch = 0;

function dispatch(frame: FramePayload): void {
  latestFrame = frame;
  for (const listener of [...listeners]) {
    try {
      listener(frame);
    } catch (err) {
      console.error('[frameFeed] listener threw', err);
    }
  }
}

function startSynth(): void {
  if (synthTimer !== null) return;
  synthEpoch = performance.now();
  synthTimer = setInterval(
    () => dispatch(makeSimFrame(performance.now() - synthEpoch, 8, 12)),
    SYNTH_INTERVAL_MS,
  );
}

function stopSynth(): void {
  if (synthTimer !== null) {
    clearInterval(synthTimer);
    synthTimer = null;
  }
}

function armLiveTimeout(): void {
  if (liveTimer !== null) clearTimeout(liveTimer);
  liveTimer = setTimeout(() => {
    live = false;
    startSynth();
  }, LIVE_TIMEOUT_MS);
}

/** Subscribe the feed to the bridge exactly once (idempotent). */
export function startFrameFeed(): void {
  if (started) return;
  started = true;
  armLiveTimeout();
  getBridge().on('frame', (frame) => {
    live = true;
    stopSynth();
    armLiveTimeout();
    dispatch(frame);
  });
}

/** False while running on the synthesized demo signal (effects off / engine not streaming). */
export function isLiveSignal(): boolean {
  return live;
}

/** The most recent frame, or null before the first one arrives. */
export function getLatestFrame(): FramePayload | null {
  return latestFrame;
}

/** Per-frame subscription (~MaxFps). Keep handlers tiny. Returns unsubscribe. */
export function subscribeFrames(listener: FrameListener): () => void {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}

// --------------------------------------------------------------- helpers --

export function rgbToCss(rgb: RGB, alpha = 1): string {
  return `rgba(${rgb[0]}, ${rgb[1]}, ${rgb[2]}, ${alpha})`;
}

export function rgbToHex(rgb: RGB): string {
  const h = (n: number) => Math.max(0, Math.min(255, Math.round(n))).toString(16).padStart(2, '0');
  return `#${h(rgb[0])}${h(rgb[1])}${h(rgb[2])}`;
}
