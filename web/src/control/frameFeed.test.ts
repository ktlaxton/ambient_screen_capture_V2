// frameFeed.test.ts — the 60Hz frame stream kept out of React state.
// The bridge module is mocked with a hand-built hub; vi.resetModules() gives
// each test a fresh frameFeed (its `started`/`latestFrame` state is module-level).
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { FramePayload } from '../shared/bridge';

type Handler = (payload: unknown) => void;

const mocks = vi.hoisted(() => {
  const handlers = new Map<string, Set<Handler>>();
  const bridge = {
    isHosted: true,
    send: vi.fn(),
    on: vi.fn((type: string, handler: Handler): (() => void) => {
      let set = handlers.get(type);
      if (!set) {
        set = new Set();
        handlers.set(type, set);
      }
      set.add(handler);
      return () => set.delete(handler);
    }),
    dispose: vi.fn(),
  };
  const emit = (type: string, payload: unknown): void => {
    for (const handler of [...(handlers.get(type) ?? [])]) handler(payload);
  };
  return { handlers, bridge, emit };
});

vi.mock('../shared/bridge', () => ({
  getBridge: () => mocks.bridge,
}));

function makeFrame(t = 1): FramePayload {
  return {
    t,
    edges: {
      top: [[10, 20, 30]],
      bottom: [[40, 50, 60]],
      left: [[70, 80, 90]],
      right: [[100, 110, 120]],
    },
    dominant: [200, 100, 50],
    audio: { intensity: 0.5, bands: [0.1, 0.2, 0.3] },
  };
}

let feed: typeof import('./frameFeed');

beforeEach(async () => {
  vi.resetModules();
  mocks.handlers.clear();
  mocks.bridge.send.mockClear();
  mocks.bridge.on.mockClear();
  feed = await import('./frameFeed');
});

afterEach(() => {
  vi.restoreAllMocks();
});

describe('startFrameFeed', () => {
  it('subscribes to the bridge frame stream exactly once (idempotent)', () => {
    feed.startFrameFeed();
    feed.startFrameFeed();
    expect(mocks.bridge.on).toHaveBeenCalledTimes(1);
    expect(mocks.bridge.on).toHaveBeenCalledWith('frame', expect.any(Function));
    expect(mocks.handlers.get('frame')?.size).toBe(1);
  });
});

describe('latest frame', () => {
  it('returns null before any frame arrives', () => {
    feed.startFrameFeed();
    expect(feed.getLatestFrame()).toBeNull();
  });

  it('returns the last published frame', () => {
    feed.startFrameFeed();
    const first = makeFrame(1);
    const second = makeFrame(2);
    mocks.emit('frame', first);
    expect(feed.getLatestFrame()).toBe(first);
    mocks.emit('frame', second);
    expect(feed.getLatestFrame()).toBe(second);
  });
});

describe('subscribeFrames', () => {
  it('delivers published frames to subscribers', () => {
    feed.startFrameFeed();
    const listener = vi.fn();
    feed.subscribeFrames(listener);
    const frame = makeFrame(7);
    mocks.emit('frame', frame);
    expect(listener).toHaveBeenCalledTimes(1);
    expect(listener).toHaveBeenCalledWith(frame);
  });

  it('unsubscribe stops further delivery', () => {
    feed.startFrameFeed();
    const listener = vi.fn();
    const unsubscribe = feed.subscribeFrames(listener);
    mocks.emit('frame', makeFrame(1));
    unsubscribe();
    mocks.emit('frame', makeFrame(2));
    expect(listener).toHaveBeenCalledTimes(1);
  });

  it('a throwing subscriber does not block other subscribers or the latest-frame cache', () => {
    const errorSpy = vi.spyOn(console, 'error').mockImplementation(() => {});
    feed.startFrameFeed();
    const bad = vi.fn(() => {
      throw new Error('listener boom');
    });
    const good = vi.fn();
    feed.subscribeFrames(bad);
    feed.subscribeFrames(good);
    const frame = makeFrame(3);
    mocks.emit('frame', frame);
    expect(bad).toHaveBeenCalledTimes(1);
    expect(good).toHaveBeenCalledTimes(1);
    expect(good).toHaveBeenCalledWith(frame);
    expect(feed.getLatestFrame()).toBe(frame);
    expect(errorSpy).toHaveBeenCalled();
  });

  it('supports multiple isolated subscribers', () => {
    feed.startFrameFeed();
    const a = vi.fn();
    const b = vi.fn();
    const offA = feed.subscribeFrames(a);
    feed.subscribeFrames(b);
    mocks.emit('frame', makeFrame(1));
    offA();
    mocks.emit('frame', makeFrame(2));
    expect(a).toHaveBeenCalledTimes(1);
    expect(b).toHaveBeenCalledTimes(2);
  });
});

describe('color helpers', () => {
  it('rgbToCss formats with default and explicit alpha', () => {
    expect(feed.rgbToCss([255, 0, 128])).toBe('rgba(255, 0, 128, 1)');
    expect(feed.rgbToCss([1, 2, 3], 0.5)).toBe('rgba(1, 2, 3, 0.5)');
  });

  it('rgbToHex pads, rounds, and clamps channels', () => {
    expect(feed.rgbToHex([0, 0, 0])).toBe('#000000');
    expect(feed.rgbToHex([255, 0, 128])).toBe('#ff0080');
    expect(feed.rgbToHex([15.6, 300, -5])).toBe('#10ff00');
  });
});
