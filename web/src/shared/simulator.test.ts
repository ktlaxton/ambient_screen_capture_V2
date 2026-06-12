// Tests for the simulated engine: synthetic frame generation (makeSimFrame)
// and the full command surface of the simulator bridge under fake timers.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createSimulatorBridge, defaultSimSettings, makeSimFrame } from './simulator';
import type { Bridge, EngineMessageMap, EngineMessageType } from './bridge';

const DISPLAY1 = '\\\\.\\DISPLAY1';
const DISPLAY2 = '\\\\.\\DISPLAY2';

function collect<K extends EngineMessageType>(bridge: Bridge, type: K): EngineMessageMap[K][] {
  const received: EngineMessageMap[K][] = [];
  bridge.on(type, (payload) => {
    received.push(payload);
  });
  return received;
}

const last = <T>(arr: T[]): T => arr[arr.length - 1];

describe('makeSimFrame', () => {
  it('produces 4 edge arrays of the requested zone count with integer 0-255 channels', () => {
    const frame = makeSimFrame(1234.5, 8, 12);
    const edges = [frame.edges.top, frame.edges.bottom, frame.edges.left, frame.edges.right];
    expect(edges).toHaveLength(4);
    for (const edge of edges) {
      expect(edge).toHaveLength(8);
      for (const rgb of edge) {
        expect(rgb).toHaveLength(3);
        for (const channel of rgb) {
          expect(Number.isInteger(channel)).toBe(true);
          expect(channel).toBeGreaterThanOrEqual(0);
          expect(channel).toBeLessThanOrEqual(255);
        }
      }
    }
    for (const channel of frame.dominant) {
      expect(Number.isInteger(channel)).toBe(true);
      expect(channel).toBeGreaterThanOrEqual(0);
      expect(channel).toBeLessThanOrEqual(255);
    }
    expect(frame.t).toBe(1234.5);
  });

  it('audio bands match the requested count and stay within [0,1]; intensity within [0,1]', () => {
    const cases: Array<[number, number, number]> = [
      [0, 8, 12],
      [5000, 3, 1],
      [123456.7, 16, 16],
    ];
    for (const [t, zones, bands] of cases) {
      const frame = makeSimFrame(t, zones, bands);
      expect(frame.audio.bands).toHaveLength(bands);
      for (const band of frame.audio.bands) {
        expect(band).toBeGreaterThanOrEqual(0);
        expect(band).toBeLessThanOrEqual(1);
      }
      expect(frame.audio.intensity).toBeGreaterThanOrEqual(0);
      expect(frame.audio.intensity).toBeLessThanOrEqual(1);
      expect(frame.edges.top).toHaveLength(zones);
    }
  });

  it('is deterministic for equal t', () => {
    expect(makeSimFrame(777.7, 8, 12)).toEqual(makeSimFrame(777.7, 8, 12));
    expect(makeSimFrame(0, 4, 6)).toEqual(makeSimFrame(0, 4, 6));
  });
});

describe('defaultSimSettings', () => {
  it('returns a fresh object every call (callers cannot corrupt later defaults)', () => {
    const a = defaultSimSettings();
    a.maxFps = 1;
    a.targetMonitorIds.push('mutated');
    expect(defaultSimSettings().maxFps).toBe(60);
    expect(defaultSimSettings().targetMonitorIds).not.toContain('mutated');
  });
});

describe('createSimulatorBridge', () => {
  let bridge: Bridge | null = null;

  beforeEach(() => {
    vi.useFakeTimers();
  });

  afterEach(() => {
    bridge?.dispose();
    bridge = null;
    vi.useRealTimers();
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  it('emits frames on a ~16.7ms cadence while enabled', () => {
    bridge = createSimulatorBridge();
    const frames = collect(bridge, 'frame');

    expect(frames).toHaveLength(0);
    vi.advanceTimersByTime(1000);
    // 60Hz interval: allow slack for fractional-delay rounding in fake timers.
    expect(frames.length).toBeGreaterThanOrEqual(55);
    expect(frames.length).toBeLessThanOrEqual(65);
    expect(frames[0].edges.top).toHaveLength(8);
    expect(frames[0].audio.bands).toHaveLength(12);
  });

  it('setEnabled false stops frame emission; re-enabling resumes it', () => {
    bridge = createSimulatorBridge();
    const frames = collect(bridge, 'frame');

    vi.advanceTimersByTime(100);
    const runningCount = frames.length;
    expect(runningCount).toBeGreaterThan(0);

    bridge.send('setEnabled', { enabled: false });
    vi.advanceTimersByTime(500);
    expect(frames.length).toBe(runningCount);

    bridge.send('setEnabled', { enabled: true });
    vi.advanceTimersByTime(100);
    expect(frames.length).toBeGreaterThan(runningCount);
  });

  it('emits an info status shortly after startup', () => {
    bridge = createSimulatorBridge();
    const statuses = collect(bridge, 'status');

    vi.advanceTimersByTime(800);
    expect(statuses).toHaveLength(1);
    expect(statuses[0].level).toBe('info');
  });

  it('requestState emits config + monitors + windowConfig', () => {
    bridge = createSimulatorBridge();
    const configs = collect(bridge, 'config');
    const monitors = collect(bridge, 'monitors');
    const windowConfigs = collect(bridge, 'windowConfig');

    bridge.send('requestState', {});

    expect(configs).toHaveLength(1);
    expect(configs[0].settings).toEqual(defaultSimSettings());
    expect(configs[0].firstRun).toBe(false);
    expect(configs[0].appVersion).toBe('0.0.0-simulator');

    expect(monitors).toHaveLength(1);
    expect(monitors[0].monitors).toHaveLength(3);
    expect(monitors[0].monitors.filter((m) => m.isPrimary)).toHaveLength(1);

    expect(windowConfigs).toHaveLength(1);
    const wc = windowConfigs[0];
    expect(wc.monitorId).toBe(DISPLAY2);
    expect(wc.monitor?.id).toBe(DISPLAY2);
    expect(wc.effectId).toBe('edge-glow');
    expect(wc.source?.id).toBe(DISPLAY1);
    expect(wc.relation).toBe('right');
  });

  it('setGlobal merges partial fields into the next config', () => {
    bridge = createSimulatorBridge();
    const configs = collect(bridge, 'config');

    bridge.send('setGlobal', { intensity: 0.25 });
    let s = last(configs).settings;
    expect(s.globalIntensity).toBe(0.25);
    expect(s.smoothing).toBe(0.5);
    expect(s.brightness).toBe(0.85);
    expect(s.audioSensitivity).toBe(0.5);
    expect(s.maxFps).toBe(60);

    bridge.send('setGlobal', { smoothing: 0.9, maxFps: 30 });
    s = last(configs).settings;
    expect(s.globalIntensity).toBe(0.25);
    expect(s.smoothing).toBe(0.9);
    expect(s.maxFps).toBe(30);
    expect(s.brightness).toBe(0.85);
    expect(s.audioSensitivity).toBe(0.5);
  });

  it('savePreset -> loadPreset -> deletePreset lifecycle', () => {
    bridge = createSimulatorBridge();
    const configs = collect(bridge, 'config');

    // whitespace-only names are rejected (no config push, nothing stored)
    bridge.send('savePreset', { name: '   ' });
    expect(configs).toHaveLength(0);

    bridge.send('setGlobal', { brightness: 0.4 });
    bridge.send('savePreset', { name: '  Night  ' });
    let cfg = last(configs);
    expect(cfg.settings.presets).toHaveLength(1);
    expect(cfg.settings.presets[0].name).toBe('Night');
    expect(cfg.settings.presets[0].snapshot.presets).toEqual([]);
    expect(cfg.settings.presets[0].snapshot.brightness).toBe(0.4);
    expect(cfg.settings.activePresetName).toBe('Night');

    // saving the same name replaces, not duplicates
    bridge.send('setGlobal', { brightness: 0.7 });
    bridge.send('savePreset', { name: 'Night' });
    cfg = last(configs);
    expect(cfg.settings.presets).toHaveLength(1);
    expect(cfg.settings.presets[0].snapshot.brightness).toBe(0.7);

    // mutate, then load restores the snapshot but keeps the preset list
    bridge.send('setGlobal', { brightness: 0.95 });
    bridge.send('loadPreset', { name: 'Night' });
    cfg = last(configs);
    expect(cfg.settings.brightness).toBe(0.7);
    expect(cfg.settings.presets).toHaveLength(1);
    expect(cfg.settings.activePresetName).toBe('Night');

    // loading an unknown preset changes nothing and emits nothing
    const before = configs.length;
    bridge.send('loadPreset', { name: 'does-not-exist' });
    expect(configs).toHaveLength(before);

    bridge.send('deletePreset', { name: 'Night' });
    cfg = last(configs);
    expect(cfg.settings.presets).toEqual([]);
    expect(cfg.settings.activePresetName).toBe('');
  });

  it('setEffect handles global vs per-monitor assignment', () => {
    bridge = createSimulatorBridge();
    const configs = collect(bridge, 'config');
    const windowConfigs = collect(bridge, 'windowConfig');

    bridge.send('setEffect', { effectId: 'plasma' });
    let s = last(configs).settings;
    expect(s.activeEffectId).toBe('plasma');
    expect(s.effectByMonitorId).toEqual({});
    expect(last(windowConfigs).effectId).toBe('plasma');

    bridge.send('setEffect', { monitorId: DISPLAY2, effectId: 'aurora' });
    s = last(configs).settings;
    expect(s.activeEffectId).toBe('plasma');
    expect(s.effectByMonitorId).toEqual({ [DISPLAY2]: 'aurora' });
    // the simulated effect window sits on DISPLAY2, so its override wins
    expect(last(windowConfigs).effectId).toBe('aurora');

    bridge.send('setEffect', { monitorId: 'all', effectId: 'particles' });
    s = last(configs).settings;
    expect(s.activeEffectId).toBe('particles');
    expect(s.effectByMonitorId).toEqual({});
    expect(last(windowConfigs).effectId).toBe('particles');
  });

  it('setEffectParams merges into existing params for that effect', () => {
    bridge = createSimulatorBridge();
    const configs = collect(bridge, 'config');

    bridge.send('setEffectParams', { effectId: 'plasma', params: { scale: 2, warp: 0.1 } });
    bridge.send('setEffectParams', { effectId: 'plasma', params: { warp: 0.7 } });
    bridge.send('setEffectParams', { effectId: 'aurora', params: { sway: 0.3 } });

    const s = last(configs).settings;
    expect(s.effectParamsById['plasma']).toEqual({ scale: 2, warp: 0.7 });
    expect(s.effectParamsById['aurora']).toEqual({ sway: 0.3 });
  });

  it('completeOnboarding flips firstRun (started via ?firstrun=1)', () => {
    vi.stubGlobal('location', new URL('http://localhost/control.html?firstrun=1'));
    bridge = createSimulatorBridge();
    const configs = collect(bridge, 'config');

    bridge.send('requestState', {});
    expect(last(configs).firstRun).toBe(true);
    expect(last(configs).settings.firstRunCompleted).toBe(false);

    bridge.send('completeOnboarding', {});
    expect(last(configs).firstRun).toBe(false);
    expect(last(configs).settings.firstRunCompleted).toBe(true);
  });

  it('dispose stops all timers (no emissions afterwards)', () => {
    bridge = createSimulatorBridge();
    const frames = collect(bridge, 'frame');
    const statuses = collect(bridge, 'status');

    bridge.dispose();
    bridge = null;
    vi.advanceTimersByTime(2000);

    expect(frames).toHaveLength(0);
    expect(statuses).toHaveLength(0);
  });
});
