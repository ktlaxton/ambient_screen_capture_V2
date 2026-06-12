// bridgeGlue.test.ts — wiring between the bridge and the control store, plus
// the optimistic command helpers. The bridge and effects registry are mocked;
// vi.resetModules() gives each test a fresh glue/store (module-level state).
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { ApplicationSettings, ConfigPayload, MonitorInfo } from '../shared/bridge';

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
  const reset = (): void => {
    handlers.clear();
    bridge.send.mockClear();
    bridge.on.mockClear();
    bridge.isHosted = true;
  };
  return { handlers, bridge, emit, reset };
});

vi.mock('../shared/bridge', () => ({
  getBridge: () => mocks.bridge,
}));

// Stub effect modules: enough shape for defaultParamsOf(); create() must never run.
vi.mock('../effects/registry', () => {
  const never = (): never => {
    throw new Error('create() must not be called in bridgeGlue tests');
  };
  const edgeGlow = {
    id: 'edge-glow',
    name: 'Edge Glow',
    description: '',
    params: [
      { key: 'speed', label: 'Speed', type: 'range', min: 0, max: 5, step: 0.1, default: 1 },
      { key: 'mode', label: 'Mode', type: 'select', options: [], default: 'soft' },
    ],
    create: never,
  };
  const plasma = {
    id: 'plasma',
    name: 'Plasma',
    description: '',
    params: [{ key: 'scale', label: 'Scale', type: 'range', default: 2 }],
    create: never,
  };
  const effectsById: Record<string, unknown> = { 'edge-glow': edgeGlow, plasma };
  return {
    effects: [edgeGlow, plasma],
    effectsById,
    DEFAULT_EFFECT_ID: 'edge-glow',
    getEffect: (id: string) => effectsById[id] ?? edgeGlow,
  };
});

function makeSettings(overrides: Partial<ApplicationSettings> = {}): ApplicationSettings {
  return {
    isEnabled: true,
    sourceMonitorId: 'MON1',
    targetMonitorIds: ['MON2'],
    activeEffectId: 'edge-glow',
    effectByMonitorId: {},
    audioSensitivity: 0.5,
    globalIntensity: 1,
    smoothing: 0.5,
    brightness: 0.85,
    maxFps: 60,
    zonesPerEdge: 8,
    audioBands: 12,
    autostart: false,
    effectParamsById: {},
    hotkeys: {},
    presets: [],
    activePresetName: '',
    firstRunCompleted: true,
    ...overrides,
  };
}

function makeConfig(overrides: Partial<ConfigPayload> = {}): ConfigPayload {
  return { settings: makeSettings(), firstRun: false, appVersion: '2.0.0', ...overrides };
}

const MONITORS: MonitorInfo[] = [
  { id: 'MON1', name: 'Primary', x: 0, y: 0, width: 2560, height: 1440, isPrimary: true },
  { id: 'MON2', name: 'Right', x: 2560, y: 0, width: 1920, height: 1080, isPrimary: false },
];

let glue: typeof import('./bridgeGlue');
let storeMod: typeof import('./store');

const getState = () => storeMod.useControlStore.getState();

/** init + push a config so the optimistic helpers have settings to patch. */
function initWithConfig(settings: Partial<ApplicationSettings> = {}): void {
  glue.initBridgeGlue();
  mocks.emit('config', makeConfig({ settings: makeSettings(settings) }));
  mocks.bridge.send.mockClear();
}

beforeEach(async () => {
  vi.resetModules();
  mocks.reset();
  vi.useFakeTimers();
  glue = await import('./bridgeGlue');
  storeMod = await import('./store');
});

afterEach(() => {
  vi.clearAllTimers();
  vi.useRealTimers();
  vi.restoreAllMocks();
});

describe('initBridgeGlue', () => {
  it('sends requestState on init', () => {
    glue.initBridgeGlue();
    expect(mocks.bridge.send).toHaveBeenCalledWith('requestState', {});
  });

  it('reflects bridge.isHosted into connected', () => {
    glue.initBridgeGlue();
    expect(getState().connected).toBe(true);
  });

  it('reflects a non-hosted bridge as disconnected', () => {
    mocks.bridge.isHosted = false;
    glue.initBridgeGlue();
    expect(getState().connected).toBe(false);
  });

  it('subscribes to config, monitors, status, and the frame feed', () => {
    glue.initBridgeGlue();
    expect([...mocks.handlers.keys()].sort()).toEqual(['config', 'frame', 'monitors', 'status']);
  });

  it('is idempotent: a second call adds no subscriptions and sends nothing', () => {
    glue.initBridgeGlue();
    const onCalls = mocks.bridge.on.mock.calls.length;
    const sendCalls = mocks.bridge.send.mock.calls.length;
    glue.initBridgeGlue();
    expect(mocks.bridge.on.mock.calls.length).toBe(onCalls);
    expect(mocks.bridge.send.mock.calls.length).toBe(sendCalls);
  });

  it('config message updates store settings and appVersion', () => {
    glue.initBridgeGlue();
    const cfg = makeConfig({ appVersion: '9.9.9' });
    mocks.emit('config', cfg);
    expect(getState().settings).toEqual(cfg.settings);
    expect(getState().appVersion).toBe('9.9.9');
  });

  it('monitors message updates the monitor list', () => {
    glue.initBridgeGlue();
    mocks.emit('monitors', { monitors: MONITORS });
    expect(getState().monitors).toEqual(MONITORS);
  });

  it('status message creates a toast with the matching level and message', () => {
    glue.initBridgeGlue();
    mocks.emit('status', { level: 'warn', message: 'capture degraded' });
    expect(getState().toasts).toHaveLength(1);
    expect(getState().toasts[0]).toMatchObject({
      level: 'warn',
      message: 'capture degraded',
      sticky: false,
    });
  });

  it('error status creates a sticky toast', () => {
    glue.initBridgeGlue();
    mocks.emit('status', { level: 'error', message: 'capture failed' });
    expect(getState().toasts[0].sticky).toBe(true);
  });
});

describe('setEnabled', () => {
  it('patches the store optimistically and sends the command', () => {
    initWithConfig({ isEnabled: true });
    glue.setEnabled(false);
    expect(getState().settings?.isEnabled).toBe(false);
    expect(mocks.bridge.send).toHaveBeenCalledWith('setEnabled', { enabled: false });
  });
});

describe('setSourceMonitor', () => {
  it('moves the new source out of the target list and sends both commands', () => {
    initWithConfig({ sourceMonitorId: 'MON1', targetMonitorIds: ['MON2', 'MON3'] });
    glue.setSourceMonitor('MON2');
    const s = getState().settings!;
    expect(s.sourceMonitorId).toBe('MON2');
    expect(s.targetMonitorIds).toEqual(['MON3']);
    expect(mocks.bridge.send).toHaveBeenCalledWith('setSourceMonitor', { monitorId: 'MON2' });
    expect(mocks.bridge.send).toHaveBeenCalledWith('setTargetMonitors', { monitorIds: ['MON3'] });
  });

  it('does not send setTargetMonitors when targets are unaffected', () => {
    initWithConfig({ sourceMonitorId: 'MON1', targetMonitorIds: ['MON2'] });
    glue.setSourceMonitor('MON3');
    expect(mocks.bridge.send).toHaveBeenCalledWith('setSourceMonitor', { monitorId: 'MON3' });
    expect(mocks.bridge.send).toHaveBeenCalledTimes(1);
  });

  it('is a no-op when the monitor is already the source', () => {
    initWithConfig({ sourceMonitorId: 'MON1' });
    glue.setSourceMonitor('MON1');
    expect(mocks.bridge.send).not.toHaveBeenCalled();
  });

  it('is a no-op before settings arrive', () => {
    glue.initBridgeGlue();
    mocks.bridge.send.mockClear();
    glue.setSourceMonitor('MON2');
    expect(mocks.bridge.send).not.toHaveBeenCalled();
  });
});

describe('toggleTargetMonitor', () => {
  it('adds a monitor that is not a target yet', () => {
    initWithConfig({ targetMonitorIds: ['MON2'] });
    glue.toggleTargetMonitor('MON3');
    expect(getState().settings?.targetMonitorIds).toEqual(['MON2', 'MON3']);
    expect(mocks.bridge.send).toHaveBeenCalledWith('setTargetMonitors', {
      monitorIds: ['MON2', 'MON3'],
    });
  });

  it('removes a monitor that is already a target', () => {
    initWithConfig({ targetMonitorIds: ['MON2', 'MON3'] });
    glue.toggleTargetMonitor('MON2');
    expect(getState().settings?.targetMonitorIds).toEqual(['MON3']);
    expect(mocks.bridge.send).toHaveBeenCalledWith('setTargetMonitors', { monitorIds: ['MON3'] });
  });

  it('refuses to make the source monitor a target', () => {
    initWithConfig({ sourceMonitorId: 'MON1', targetMonitorIds: [] });
    glue.toggleTargetMonitor('MON1');
    expect(getState().settings?.targetMonitorIds).toEqual([]);
    expect(mocks.bridge.send).not.toHaveBeenCalled();
  });
});

describe('effect selection', () => {
  it('setEffectGlobal sets the active effect, clears overrides, and selects it', () => {
    initWithConfig({ effectByMonitorId: { MON2: 'plasma' } });
    glue.setEffectGlobal('plasma');
    const s = getState().settings!;
    expect(s.activeEffectId).toBe('plasma');
    expect(s.effectByMonitorId).toEqual({});
    expect(getState().selectedEffectId).toBe('plasma');
    expect(mocks.bridge.send).toHaveBeenCalledWith('setEffect', { effectId: 'plasma' });
  });

  it('setEffectForMonitor records a per-monitor override', () => {
    initWithConfig({ activeEffectId: 'edge-glow' });
    glue.setEffectForMonitor('MON2', 'plasma');
    expect(getState().settings?.effectByMonitorId).toEqual({ MON2: 'plasma' });
    expect(mocks.bridge.send).toHaveBeenCalledWith('setEffect', {
      monitorId: 'MON2',
      effectId: 'plasma',
    });
  });

  it('setEffectForMonitor with an empty id clears the override back to global', () => {
    initWithConfig({ activeEffectId: 'edge-glow', effectByMonitorId: { MON2: 'plasma' } });
    glue.setEffectForMonitor('MON2', '');
    expect(getState().settings?.effectByMonitorId).toEqual({});
    // The protocol's clear operation is an EMPTY effectId — sending the current global
    // id instead would pin the monitor to it even after the global effect changes.
    expect(mocks.bridge.send).toHaveBeenCalledWith('setEffect', {
      monitorId: 'MON2',
      effectId: '',
    });
  });

  it('setEffectForMonitor with the global effect id removes the override', () => {
    initWithConfig({ activeEffectId: 'edge-glow', effectByMonitorId: { MON2: 'plasma' } });
    glue.setEffectForMonitor('MON2', 'edge-glow');
    expect(getState().settings?.effectByMonitorId).toEqual({});
    expect(mocks.bridge.send).toHaveBeenCalledWith('setEffect', {
      monitorId: 'MON2',
      effectId: '',
    });
  });
});

describe('effect params', () => {
  it('resolvedEffectParams merges stored values over module defaults', () => {
    initWithConfig({ effectParamsById: { 'edge-glow': { speed: 3 } } });
    expect(glue.resolvedEffectParams('edge-glow')).toEqual({ speed: 3, mode: 'soft' });
  });

  it('resolvedEffectParams returns pure defaults when nothing is stored', () => {
    initWithConfig();
    expect(glue.resolvedEffectParams('edge-glow')).toEqual({ speed: 1, mode: 'soft' });
  });

  it('setEffectParam patches the store immediately and debounces the send', () => {
    initWithConfig();
    glue.setEffectParam('edge-glow', 'speed', 2);
    expect(getState().settings?.effectParamsById['edge-glow']).toEqual({ speed: 2 });
    expect(mocks.bridge.send).not.toHaveBeenCalled();
    vi.advanceTimersByTime(120);
    expect(mocks.bridge.send).toHaveBeenCalledTimes(1);
    expect(mocks.bridge.send).toHaveBeenCalledWith('setEffectParams', {
      effectId: 'edge-glow',
      params: { speed: 2, mode: 'soft' },
    });
  });

  it('rapid setEffectParam calls collapse into one send with all values merged', () => {
    initWithConfig();
    glue.setEffectParam('edge-glow', 'speed', 2);
    vi.advanceTimersByTime(60);
    glue.setEffectParam('edge-glow', 'mode', 'hard');
    vi.advanceTimersByTime(119);
    expect(mocks.bridge.send).not.toHaveBeenCalled();
    vi.advanceTimersByTime(1);
    expect(mocks.bridge.send).toHaveBeenCalledTimes(1);
    expect(mocks.bridge.send).toHaveBeenCalledWith('setEffectParams', {
      effectId: 'edge-glow',
      params: { speed: 2, mode: 'hard' },
    });
  });

  it('debounce is keyed per effect: params for different effects both send', () => {
    initWithConfig();
    glue.setEffectParam('edge-glow', 'speed', 2);
    glue.setEffectParam('plasma', 'scale', 4);
    vi.advanceTimersByTime(120);
    expect(mocks.bridge.send).toHaveBeenCalledTimes(2);
  });

  it('resetEffectParams restores defaults in the store and sends them immediately', () => {
    initWithConfig({ effectParamsById: { 'edge-glow': { speed: 5, mode: 'hard' } } });
    glue.resetEffectParams('edge-glow');
    expect(getState().settings?.effectParamsById['edge-glow']).toEqual({
      speed: 1,
      mode: 'soft',
    });
    expect(mocks.bridge.send).toHaveBeenCalledWith('setEffectParams', {
      effectId: 'edge-glow',
      params: { speed: 1, mode: 'soft' },
    });
  });
});

describe('setGlobal', () => {
  it('maps the field to the settings key and debounces the send', () => {
    initWithConfig({ globalIntensity: 1 });
    glue.setGlobal('intensity', 0.4);
    expect(getState().settings?.globalIntensity).toBe(0.4);
    expect(mocks.bridge.send).not.toHaveBeenCalled();
    vi.advanceTimersByTime(120);
    expect(mocks.bridge.send).toHaveBeenCalledWith('setGlobal', { intensity: 0.4 });
  });

  it('debounces per field: only the last value of a field is sent', () => {
    initWithConfig();
    glue.setGlobal('maxFps', 30);
    glue.setGlobal('maxFps', 45);
    glue.setGlobal('brightness', 0.7);
    vi.advanceTimersByTime(120);
    expect(mocks.bridge.send).toHaveBeenCalledTimes(2);
    expect(mocks.bridge.send).toHaveBeenCalledWith('setGlobal', { maxFps: 45 });
    expect(mocks.bridge.send).toHaveBeenCalledWith('setGlobal', { brightness: 0.7 });
    expect(getState().settings?.maxFps).toBe(45);
    expect(getState().settings?.brightness).toBe(0.7);
  });
});

describe('misc commands', () => {
  it('setAutostart patches and sends', () => {
    initWithConfig({ autostart: false });
    glue.setAutostart(true);
    expect(getState().settings?.autostart).toBe(true);
    expect(mocks.bridge.send).toHaveBeenCalledWith('setAutostart', { enabled: true });
  });

  it('setHotkey merges into the hotkeys map and sends', () => {
    initWithConfig({ hotkeys: { toggle: 'Ctrl+Alt+A' } });
    glue.setHotkey('nextEffect', 'Ctrl+Alt+N');
    expect(getState().settings?.hotkeys).toEqual({
      toggle: 'Ctrl+Alt+A',
      nextEffect: 'Ctrl+Alt+N',
    });
    expect(mocks.bridge.send).toHaveBeenCalledWith('setHotkey', {
      action: 'nextEffect',
      keys: 'Ctrl+Alt+N',
    });
  });

  it('preset commands pass straight through', () => {
    initWithConfig();
    glue.savePreset('Gaming');
    glue.loadPreset('Gaming');
    glue.deletePreset('Gaming');
    expect(mocks.bridge.send).toHaveBeenCalledWith('savePreset', { name: 'Gaming' });
    expect(mocks.bridge.send).toHaveBeenCalledWith('loadPreset', { name: 'Gaming' });
    expect(mocks.bridge.send).toHaveBeenCalledWith('deletePreset', { name: 'Gaming' });
  });

  it('windowCommand passes straight through', () => {
    initWithConfig();
    glue.windowCommand('minimize');
    expect(mocks.bridge.send).toHaveBeenCalledWith('windowCommand', { action: 'minimize' });
  });

  it('completeOnboarding patches firstRunCompleted and sends', () => {
    initWithConfig({ firstRunCompleted: false });
    glue.completeOnboarding();
    expect(getState().settings?.firstRunCompleted).toBe(true);
    expect(mocks.bridge.send).toHaveBeenCalledWith('completeOnboarding', {});
  });
});
