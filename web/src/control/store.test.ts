// store.test.ts — unit tests for the control-UI zustand store (no React).
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { useControlStore } from './store';
import type { ApplicationSettings, ConfigPayload, MonitorInfo } from '../shared/bridge';

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

beforeEach(() => {
  vi.useFakeTimers();
  useControlStore.setState({
    settings: null,
    monitors: [],
    connected: false,
    appVersion: '',
    toasts: [],
    selectedEffectId: null,
    onboardingOpen: false,
    onboardingDecided: false,
  });
});

afterEach(() => {
  vi.clearAllTimers();
  vi.useRealTimers();
});

describe('initial state', () => {
  it('starts empty and disconnected', () => {
    const s = useControlStore.getState();
    expect(s.settings).toBeNull();
    expect(s.monitors).toEqual([]);
    expect(s.connected).toBe(false);
    expect(s.appVersion).toBe('');
    expect(s.toasts).toEqual([]);
    expect(s.selectedEffectId).toBeNull();
    expect(s.onboardingOpen).toBe(false);
    expect(s.onboardingDecided).toBe(false);
  });
});

describe('simple setters', () => {
  it('setConnected flips the flag', () => {
    useControlStore.getState().setConnected(true);
    expect(useControlStore.getState().connected).toBe(true);
  });

  it('setMonitors replaces the monitor list', () => {
    useControlStore.getState().setMonitors(MONITORS);
    expect(useControlStore.getState().monitors).toEqual(MONITORS);
  });

  it('selectEffect sets the UI-local selection', () => {
    useControlStore.getState().selectEffect('plasma');
    expect(useControlStore.getState().selectedEffectId).toBe('plasma');
  });
});

describe('applyConfig', () => {
  it('stores settings and appVersion', () => {
    const cfg = makeConfig();
    useControlStore.getState().applyConfig(cfg);
    const s = useControlStore.getState();
    expect(s.settings).toEqual(cfg.settings);
    expect(s.appVersion).toBe('2.0.0');
  });

  it('defaults the effect selection to the active effect when nothing is selected', () => {
    useControlStore.getState().applyConfig(makeConfig());
    expect(useControlStore.getState().selectedEffectId).toBe('edge-glow');
  });

  it('preserves an existing user effect selection', () => {
    useControlStore.getState().selectEffect('plasma');
    useControlStore.getState().applyConfig(makeConfig());
    expect(useControlStore.getState().selectedEffectId).toBe('plasma');
  });

  it('opens onboarding on firstRun', () => {
    useControlStore.getState().applyConfig(makeConfig({ firstRun: true }));
    expect(useControlStore.getState().onboardingOpen).toBe(true);
  });

  it('does not open onboarding when firstRun is false', () => {
    useControlStore.getState().applyConfig(makeConfig({ firstRun: false }));
    expect(useControlStore.getState().onboardingOpen).toBe(false);
  });

  it('never auto-reopens onboarding once the user has decided', () => {
    const store = useControlStore.getState();
    store.openOnboarding();
    store.closeOnboarding();
    expect(useControlStore.getState().onboardingDecided).toBe(true);
    useControlStore.getState().applyConfig(makeConfig({ firstRun: true }));
    expect(useControlStore.getState().onboardingOpen).toBe(false);
  });

  it('keeps an already-open onboarding open across config pushes', () => {
    useControlStore.getState().openOnboarding();
    useControlStore.getState().applyConfig(makeConfig({ firstRun: false }));
    expect(useControlStore.getState().onboardingOpen).toBe(true);
  });
});

describe('patchSettings', () => {
  it('is a no-op while settings are null', () => {
    useControlStore.getState().patchSettings({ isEnabled: false });
    expect(useControlStore.getState().settings).toBeNull();
  });

  it('shallow-merges the patch over existing settings', () => {
    useControlStore.getState().applyConfig(makeConfig());
    useControlStore.getState().patchSettings({ isEnabled: false, maxFps: 30 });
    const s = useControlStore.getState().settings!;
    expect(s.isEnabled).toBe(false);
    expect(s.maxFps).toBe(30);
    expect(s.activeEffectId).toBe('edge-glow'); // untouched field preserved
  });
});

describe('toasts', () => {
  it('pushToast assigns unique increasing ids', () => {
    const store = useControlStore.getState();
    store.pushToast('info', 'one');
    store.pushToast('warn', 'two');
    const toasts = useControlStore.getState().toasts;
    expect(toasts).toHaveLength(2);
    expect(toasts[0].id).not.toBe(toasts[1].id);
    expect(toasts[1].id).toBeGreaterThan(toasts[0].id);
    expect(toasts[0]).toMatchObject({ level: 'info', message: 'one', sticky: false });
    expect(toasts[1]).toMatchObject({ level: 'warn', message: 'two', sticky: false });
  });

  it('marks error toasts sticky', () => {
    useControlStore.getState().pushToast('error', 'boom');
    expect(useControlStore.getState().toasts[0].sticky).toBe(true);
  });

  it('auto-dismisses info and warn toasts after 4s', () => {
    const store = useControlStore.getState();
    store.pushToast('info', 'i');
    store.pushToast('warn', 'w');
    vi.advanceTimersByTime(3999);
    expect(useControlStore.getState().toasts).toHaveLength(2);
    vi.advanceTimersByTime(1);
    expect(useControlStore.getState().toasts).toHaveLength(0);
  });

  it('error toasts persist past the auto-dismiss window', () => {
    useControlStore.getState().pushToast('error', 'sticky');
    vi.advanceTimersByTime(60_000);
    expect(useControlStore.getState().toasts).toHaveLength(1);
  });

  it('dismissToast removes only the targeted toast', () => {
    const store = useControlStore.getState();
    store.pushToast('error', 'a');
    store.pushToast('error', 'b');
    const [first, second] = useControlStore.getState().toasts;
    useControlStore.getState().dismissToast(first.id);
    const remaining = useControlStore.getState().toasts;
    expect(remaining).toHaveLength(1);
    expect(remaining[0].id).toBe(second.id);
  });

  it('dismissing an already-dismissed toast is harmless', () => {
    useControlStore.getState().pushToast('info', 'x');
    const id = useControlStore.getState().toasts[0].id;
    useControlStore.getState().dismissToast(id);
    useControlStore.getState().dismissToast(id);
    expect(useControlStore.getState().toasts).toHaveLength(0);
    vi.advanceTimersByTime(5000); // pending auto-dismiss timer must not throw
  });
});

describe('onboarding', () => {
  it('open/close toggles visibility and close records the decision', () => {
    useControlStore.getState().openOnboarding();
    expect(useControlStore.getState().onboardingOpen).toBe(true);
    expect(useControlStore.getState().onboardingDecided).toBe(false);
    useControlStore.getState().closeOnboarding();
    expect(useControlStore.getState().onboardingOpen).toBe(false);
    expect(useControlStore.getState().onboardingDecided).toBe(true);
  });
});
