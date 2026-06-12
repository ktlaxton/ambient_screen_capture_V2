// Tests for the bridge contract: MessageHub dispatch semantics, the WebView2
// bridge (against a fake window.chrome.webview host), and the singleton
// lifecycle (createBridge/getBridge/resetBridgeForTest).
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { MessageHub, createBridge, getBridge, resetBridgeForTest } from './bridge';
import type { FramePayload, StatusPayload } from './bridge';
import type { WebView2Bridge, WebView2MessageEvent } from './webview2';

type Listener = (event: WebView2MessageEvent) => void;

function makeFakeHost() {
  const listeners: Listener[] = [];
  const postMessage = vi.fn<(message: unknown) => void>();
  const addEventListener = vi.fn<(type: 'message', listener: Listener) => void>(
    (_type, listener) => {
      listeners.push(listener);
    },
  );
  const removeEventListener = vi.fn<(type: 'message', listener: Listener) => void>(
    (_type, listener) => {
      const i = listeners.indexOf(listener);
      if (i !== -1) listeners.splice(i, 1);
    },
  );
  const host: WebView2Bridge = { postMessage, addEventListener, removeEventListener };
  return { host, listeners, postMessage, addEventListener, removeEventListener };
}

function testFrame(): FramePayload {
  return {
    t: 1,
    edges: { top: [[1, 2, 3]], bottom: [[4, 5, 6]], left: [[7, 8, 9]], right: [[10, 11, 12]] },
    dominant: [128, 64, 32],
    audio: { intensity: 0.5, bands: [0.1, 0.2, 0.3] },
  };
}

describe('MessageHub', () => {
  it('dispatches emitted payloads to subscribed handlers', () => {
    const hub = new MessageHub();
    const received: StatusPayload[] = [];
    hub.on('status', (p) => received.push(p));

    const payload: StatusPayload = { level: 'info', message: 'hello' };
    hub.emit('status', payload);

    expect(received).toHaveLength(1);
    expect(received[0]).toBe(payload);
  });

  it('emit with no handlers is a no-op', () => {
    const hub = new MessageHub();
    expect(() => hub.emit('status', { level: 'warn', message: 'x' })).not.toThrow();
  });

  it('unsubscribe stops delivery', () => {
    const hub = new MessageHub();
    const handler = vi.fn();
    const off = hub.on('status', handler);

    hub.emit('status', { level: 'info', message: 'one' });
    expect(handler).toHaveBeenCalledTimes(1);

    off();
    hub.emit('status', { level: 'info', message: 'two' });
    expect(handler).toHaveBeenCalledTimes(1);
  });

  it('a throwing handler does not prevent later handlers (error isolation)', () => {
    const errSpy = vi.spyOn(console, 'error').mockImplementation(() => {});
    const hub = new MessageHub();
    const calls: string[] = [];
    hub.on('status', () => {
      calls.push('first');
      throw new Error('boom');
    });
    hub.on('status', (p) => calls.push(`second:${p.message}`));

    expect(() => hub.emit('status', { level: 'error', message: 'm' })).not.toThrow();
    expect(calls).toEqual(['first', 'second:m']);
    expect(errSpy).toHaveBeenCalled();
    errSpy.mockRestore();
  });

  it('clear() removes all handlers across types', () => {
    const hub = new MessageHub();
    const statusHandler = vi.fn();
    const frameHandler = vi.fn();
    hub.on('status', statusHandler);
    hub.on('frame', frameHandler);

    hub.clear();
    hub.emit('status', { level: 'info', message: 'x' });
    hub.emit('frame', testFrame());

    expect(statusHandler).not.toHaveBeenCalled();
    expect(frameHandler).not.toHaveBeenCalled();
  });
});

describe('WebView2 bridge', () => {
  let fake: ReturnType<typeof makeFakeHost>;

  beforeEach(() => {
    fake = makeFakeHost();
    window.chrome = { webview: fake.host };
  });

  afterEach(() => {
    resetBridgeForTest();
    delete window.chrome;
    vi.restoreAllMocks();
    vi.useRealTimers();
  });

  it('createBridge() returns the hosted bridge with isHosted true', () => {
    const bridge = createBridge();
    expect(bridge.isHosted).toBe(true);
    expect(fake.addEventListener).toHaveBeenCalledTimes(1);
    expect(fake.addEventListener).toHaveBeenCalledWith('message', expect.any(Function));
    bridge.dispose();
  });

  it('send() posts exactly {type, payload}', () => {
    const bridge = createBridge();
    bridge.send('setEnabled', { enabled: true });

    expect(fake.postMessage).toHaveBeenCalledTimes(1);
    const sent = fake.postMessage.mock.calls[0][0];
    expect(sent).toStrictEqual({ type: 'setEnabled', payload: { enabled: true } });
    expect(Object.keys(sent as object).sort()).toEqual(['payload', 'type']);
    bridge.dispose();
  });

  it('incoming {type:"frame", payload} reaches on("frame") handlers', () => {
    const bridge = createBridge();
    const received: FramePayload[] = [];
    bridge.on('frame', (f) => received.push(f));

    const frame = testFrame();
    fake.listeners[0]({ data: { type: 'frame', payload: frame } });

    expect(received).toHaveLength(1);
    expect(received[0]).toBe(frame);
    bridge.dispose();
  });

  it('ignores malformed events without throwing', () => {
    const bridge = createBridge();
    const frameHandler = vi.fn();
    const statusHandler = vi.fn();
    bridge.on('frame', frameHandler);
    bridge.on('status', statusHandler);

    for (const data of [null, {}, { type: 42 }]) {
      expect(() => fake.listeners[0]({ data })).not.toThrow();
    }

    expect(frameHandler).not.toHaveBeenCalled();
    expect(statusHandler).not.toHaveBeenCalled();
    bridge.dispose();
  });

  it('dispose() removes the host listener', () => {
    const bridge = createBridge();
    const added = fake.addEventListener.mock.calls[0][1];

    bridge.dispose();

    expect(fake.removeEventListener).toHaveBeenCalledTimes(1);
    expect(fake.removeEventListener).toHaveBeenCalledWith('message', added);
    expect(fake.listeners).toHaveLength(0);
  });

  it('getBridge() returns the same instance twice; resetBridgeForTest() makes a fresh one', () => {
    const a = getBridge();
    const b = getBridge();
    expect(b).toBe(a);
    expect(a.isHosted).toBe(true);

    resetBridgeForTest();
    const c = getBridge();
    expect(c).not.toBe(a);
    expect(c.isHosted).toBe(true);
    // the old bridge was disposed; the new one re-attached
    expect(fake.removeEventListener).toHaveBeenCalledTimes(1);
    expect(fake.addEventListener).toHaveBeenCalledTimes(2);
  });

  it('without chrome.webview, createBridge() returns the simulator (isHosted false)', () => {
    delete window.chrome;
    vi.useFakeTimers();
    vi.spyOn(console, 'info').mockImplementation(() => {});

    const bridge = createBridge();
    expect(bridge.isHosted).toBe(false);
    bridge.dispose();
  });
});
