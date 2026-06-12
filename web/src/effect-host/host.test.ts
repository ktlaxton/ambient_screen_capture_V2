// host.test.ts — EffectHost adoption/swap/settings/frame logic with the effects
// registry mocked: getEffect() returns stub modules whose create() returns a
// recording fake EffectInstance. No WebGL, no real effects.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type {
  ApplicationSettings,
  ConfigPayload,
  FramePayload,
  WindowConfigPayload,
} from '../shared/bridge';
import { EffectHost } from './host';

interface FakeInstance {
  effectId: string;
  onFrame: ReturnType<typeof vi.fn>;
  render: ReturnType<typeof vi.fn>;
  setParams: ReturnType<typeof vi.fn>;
  setGlobals: ReturnType<typeof vi.fn>;
  resize: ReturnType<typeof vi.fn>;
  dispose: ReturnType<typeof vi.fn>;
}

const registry = vi.hoisted(() => {
  const created: { effectId: string; instance: FakeInstance; ctx: unknown }[] = [];
  const state = { failNextCreate: false };

  function makeModule(id: string, params: { key: string; default: number | string | boolean }[]) {
    return {
      id,
      name: id,
      description: '',
      params: params.map((p) => ({ ...p, label: p.key, type: 'range' })),
      create: vi.fn((ctx: unknown) => {
        if (state.failNextCreate) {
          state.failNextCreate = false;
          throw new Error(`create failed for ${id}`);
        }
        const instance: FakeInstance = {
          effectId: id,
          onFrame: vi.fn(),
          render: vi.fn(),
          setParams: vi.fn(),
          setGlobals: vi.fn(),
          resize: vi.fn(),
          dispose: vi.fn(),
        };
        created.push({ effectId: id, instance, ctx });
        return instance;
      }),
    };
  }

  const edgeGlow = makeModule('edge-glow', [
    { key: 'glow', default: 0.5 },
    { key: 'mode', default: 'soft' },
  ]);
  const plasma = makeModule('plasma', [{ key: 'scale', default: 2 }]);
  const effectsById: Record<string, unknown> = { 'edge-glow': edgeGlow, plasma };

  return { created, state, edgeGlow, plasma, effectsById };
});

vi.mock('../effects/registry', () => ({
  effects: [registry.edgeGlow, registry.plasma],
  effectsById: registry.effectsById,
  DEFAULT_EFFECT_ID: 'edge-glow',
  getEffect: (id: string) => registry.effectsById[id] ?? registry.edgeGlow,
}));

function makeWindowConfig(overrides: Partial<WindowConfigPayload> = {}): WindowConfigPayload {
  return {
    monitorId: 'MON2',
    effectId: 'edge-glow',
    monitor: null,
    source: null,
    relation: 'right',
    ...overrides,
  };
}

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

function makeConfig(settings: Partial<ApplicationSettings> = {}): ConfigPayload {
  return { settings: makeSettings(settings), firstRun: false, appVersion: '2.0.0' };
}

function makeFrame(t = 1): FramePayload {
  return {
    t,
    edges: { top: [[1, 2, 3]], bottom: [[4, 5, 6]], left: [[7, 8, 9]], right: [[9, 9, 9]] },
    dominant: [128, 64, 32],
    audio: { intensity: 0.4, bands: [0.1] },
  };
}

function lastInstance(): FakeInstance {
  expect(registry.created.length).toBeGreaterThan(0);
  return registry.created[registry.created.length - 1].instance;
}

function makeHost(monitorId: string | null = 'MON2'): EffectHost {
  const canvas = document.createElement('canvas');
  const root = document.createElement('div');
  return new EffectHost(canvas, root, monitorId);
}

beforeEach(() => {
  registry.created.length = 0;
  registry.state.failNextCreate = false;
  registry.edgeGlow.create.mockClear();
  registry.plasma.create.mockClear();
  vi.spyOn(console, 'error').mockImplementation(() => {});
});

afterEach(() => {
  vi.restoreAllMocks();
});

describe('windowConfig adoption', () => {
  it('starts without an instance', () => {
    expect(makeHost().hasInstance()).toBe(false);
  });

  it('adopts a windowConfig with the matching monitorId and creates the effect', () => {
    const host = makeHost('MON2');
    host.handleWindowConfig(makeWindowConfig({ monitorId: 'MON2', effectId: 'edge-glow' }));
    expect(host.hasInstance()).toBe(true);
    expect(registry.edgeGlow.create).toHaveBeenCalledTimes(1);
  });

  it('ignores a windowConfig for a different monitor', () => {
    const host = makeHost('MON2');
    host.handleWindowConfig(makeWindowConfig({ monitorId: 'MON3' }));
    expect(host.hasInstance()).toBe(false);
    expect(registry.edgeGlow.create).not.toHaveBeenCalled();
  });

  it('an unassigned host adopts the first windowConfig and then rejects others', () => {
    const host = makeHost(null);
    host.handleWindowConfig(makeWindowConfig({ monitorId: 'MON5', effectId: 'edge-glow' }));
    expect(host.hasInstance()).toBe(true);
    // Now bound to MON5: a different monitor's config must not swap the effect.
    host.handleWindowConfig(makeWindowConfig({ monitorId: 'MON6', effectId: 'plasma' }));
    expect(registry.plasma.create).not.toHaveBeenCalled();
  });

  it('passes the windowConfig and canvas through to create()', () => {
    const host = makeHost('MON2');
    const cfg = makeWindowConfig();
    host.handleWindowConfig(cfg);
    expect(registry.created[0].ctx).toMatchObject({ windowConfig: cfg, preview: false });
  });

  it('re-pushing the same effect does not recreate the instance', () => {
    const host = makeHost('MON2');
    host.handleWindowConfig(makeWindowConfig({ effectId: 'edge-glow' }));
    host.handleWindowConfig(makeWindowConfig({ effectId: 'edge-glow' }));
    expect(registry.edgeGlow.create).toHaveBeenCalledTimes(1);
  });

  it('an effectId change disposes the old instance and creates the new effect', () => {
    const host = makeHost('MON2');
    host.handleWindowConfig(makeWindowConfig({ effectId: 'edge-glow' }));
    const first = lastInstance();
    host.handleWindowConfig(makeWindowConfig({ effectId: 'plasma' }));
    expect(first.dispose).toHaveBeenCalledTimes(1);
    expect(registry.plasma.create).toHaveBeenCalledTimes(1);
    expect(host.hasInstance()).toBe(true);
  });

  it('gives each effect swap a fresh canvas (old context is force-lost on dispose)', () => {
    const host = makeHost('MON2');
    host.handleWindowConfig(makeWindowConfig({ effectId: 'edge-glow' }));
    host.handleWindowConfig(makeWindowConfig({ effectId: 'plasma' }));
    const firstCanvas = registry.created[0].ctx.canvas;
    const secondCanvas = registry.created[1].ctx.canvas;
    // Reusing the canvas would hand the new renderer a context-lost canvas and crash
    // three's precision probe; the swap must allocate a pristine one.
    expect(secondCanvas).not.toBe(firstCanvas);
  });

  it('an unknown effectId falls back to the default effect and does not thrash', () => {
    const host = makeHost('MON2');
    host.handleWindowConfig(makeWindowConfig({ effectId: 'bogus' }));
    expect(registry.edgeGlow.create).toHaveBeenCalledTimes(1);
    // Resolves to the same default — no dispose/create churn.
    host.handleWindowConfig(makeWindowConfig({ effectId: 'edge-glow' }));
    expect(registry.edgeGlow.create).toHaveBeenCalledTimes(1);
    expect(lastInstance().dispose).not.toHaveBeenCalled();
  });

  it('survives a create() that throws and stays instance-less', () => {
    const host = makeHost('MON2');
    registry.state.failNextCreate = true;
    expect(() => host.handleWindowConfig(makeWindowConfig())).not.toThrow();
    expect(host.hasInstance()).toBe(false);
  });

  it('a throwing dispose on the old instance still completes the swap', () => {
    const host = makeHost('MON2');
    host.handleWindowConfig(makeWindowConfig({ effectId: 'edge-glow' }));
    lastInstance().dispose.mockImplementation(() => {
      throw new Error('dispose boom');
    });
    host.handleWindowConfig(makeWindowConfig({ effectId: 'plasma' }));
    expect(host.hasInstance()).toBe(true);
    expect(registry.plasma.create).toHaveBeenCalledTimes(1);
  });
});

describe('handleConfig', () => {
  it('applies merged params (defaults overlaid with stored overrides) to the instance', () => {
    const host = makeHost('MON2');
    host.handleWindowConfig(makeWindowConfig({ effectId: 'edge-glow' }));
    const instance = lastInstance();
    host.handleConfig(makeConfig({ effectParamsById: { 'edge-glow': { glow: 0.9 } } }));
    expect(instance.setParams).toHaveBeenLastCalledWith({ glow: 0.9, mode: 'soft' });
  });

  it('applies the global intensity/brightness to the instance', () => {
    const host = makeHost('MON2');
    host.handleWindowConfig(makeWindowConfig());
    const instance = lastInstance();
    host.handleConfig(makeConfig({ globalIntensity: 0.7, brightness: 0.4 }));
    expect(instance.setGlobals).toHaveBeenLastCalledWith({ intensity: 0.7, brightness: 0.4 });
  });

  it('uses safe default globals/params when windowConfig lands before config', () => {
    const host = makeHost('MON2');
    host.handleWindowConfig(makeWindowConfig({ effectId: 'edge-glow' }));
    const instance = lastInstance();
    expect(instance.setGlobals).toHaveBeenCalledWith({ intensity: 1, brightness: 1 });
    expect(instance.setParams).toHaveBeenCalledWith({ glow: 0.5, mode: 'soft' });
  });

  it('pushes a valid maxFps through onMaxFpsChange', () => {
    const host = makeHost('MON2');
    const onMaxFps = vi.fn();
    host.onMaxFpsChange = onMaxFps;
    host.handleConfig(makeConfig({ maxFps: 30 }));
    expect(onMaxFps).toHaveBeenCalledWith(30);
  });

  it('sanitizes an invalid maxFps to the 60fps default', () => {
    const host = makeHost('MON2');
    const onMaxFps = vi.fn();
    host.onMaxFpsChange = onMaxFps;
    host.handleConfig(makeConfig({ maxFps: 0 }));
    expect(onMaxFps).toHaveBeenLastCalledWith(60);
    host.handleConfig(makeConfig({ maxFps: NaN }));
    expect(onMaxFps).toHaveBeenLastCalledWith(60);
  });

  it('defensively swaps when settings carry a per-monitor override for this monitor', () => {
    const host = makeHost('MON2');
    host.handleWindowConfig(makeWindowConfig({ effectId: 'edge-glow' }));
    const first = lastInstance();
    host.handleConfig(
      makeConfig({
        effectByMonitorId: { MON2: 'plasma' },
        globalIntensity: 0.6,
        brightness: 0.5,
        effectParamsById: { plasma: { scale: 9 } },
      }),
    );
    expect(first.dispose).toHaveBeenCalledTimes(1);
    expect(registry.plasma.create).toHaveBeenCalledTimes(1);
    // The swap re-applies the new settings to the replacement instance.
    const replacement = lastInstance();
    expect(replacement.effectId).toBe('plasma');
    expect(replacement.setGlobals).toHaveBeenCalledWith({ intensity: 0.6, brightness: 0.5 });
    expect(replacement.setParams).toHaveBeenCalledWith({ scale: 9 });
  });

  it('does not swap when the override matches the running effect', () => {
    const host = makeHost('MON2');
    host.handleWindowConfig(makeWindowConfig({ effectId: 'plasma' }));
    host.handleConfig(makeConfig({ effectByMonitorId: { MON2: 'plasma' } }));
    expect(registry.plasma.create).toHaveBeenCalledTimes(1);
    expect(lastInstance().dispose).not.toHaveBeenCalled();
  });

  it('ignores overrides for other monitors', () => {
    const host = makeHost('MON2');
    host.handleWindowConfig(makeWindowConfig({ effectId: 'edge-glow' }));
    host.handleConfig(makeConfig({ effectByMonitorId: { MON3: 'plasma' } }));
    expect(registry.plasma.create).not.toHaveBeenCalled();
    expect(lastInstance().dispose).not.toHaveBeenCalled();
  });
});

describe('frames', () => {
  it('forwards frames to the instance onFrame', () => {
    const host = makeHost('MON2');
    host.handleWindowConfig(makeWindowConfig());
    const frame = makeFrame(42);
    host.handleFrame(frame);
    expect(lastInstance().onFrame).toHaveBeenCalledWith(frame);
  });

  it('drops frames silently while no instance exists', () => {
    const host = makeHost('MON2');
    expect(() => host.handleFrame(makeFrame())).not.toThrow();
  });

  it('swallows a throwing onFrame and keeps delivering subsequent frames', () => {
    const host = makeHost('MON2');
    host.handleWindowConfig(makeWindowConfig());
    const instance = lastInstance();
    instance.onFrame.mockImplementation(() => {
      throw new Error('onFrame boom');
    });
    expect(() => host.handleFrame(makeFrame(1))).not.toThrow();
    expect(() => host.handleFrame(makeFrame(2))).not.toThrow();
    expect(instance.onFrame).toHaveBeenCalledTimes(2);
  });
});

describe('renderSafe', () => {
  it('delegates to instance.render with time and dt', () => {
    const host = makeHost('MON2');
    host.handleWindowConfig(makeWindowConfig());
    host.renderSafe(1000, 16);
    expect(lastInstance().render).toHaveBeenCalledWith(1000, 16);
  });

  it('is a no-op without an instance', () => {
    expect(() => makeHost().renderSafe(0, 16)).not.toThrow();
  });

  it('fires onFatalError after 30 consecutive render failures', () => {
    const host = makeHost('MON2');
    const onFatal = vi.fn();
    host.onFatalError = onFatal;
    host.handleWindowConfig(makeWindowConfig());
    lastInstance().render.mockImplementation(() => {
      throw new Error('render boom');
    });
    for (let i = 0; i < 29; i++) host.renderSafe(i, 16);
    expect(onFatal).not.toHaveBeenCalled();
    host.renderSafe(29, 16);
    expect(onFatal).toHaveBeenCalledTimes(1);
  });

  it('a successful render resets the consecutive-failure counter', () => {
    const host = makeHost('MON2');
    const onFatal = vi.fn();
    host.onFatalError = onFatal;
    host.handleWindowConfig(makeWindowConfig());
    const instance = lastInstance();
    const boom = () => {
      throw new Error('render boom');
    };
    instance.render.mockImplementation(boom);
    for (let i = 0; i < 29; i++) host.renderSafe(i, 16);
    instance.render.mockImplementation(() => {}); // one success
    host.renderSafe(29, 16);
    instance.render.mockImplementation(boom);
    for (let i = 0; i < 29; i++) host.renderSafe(30 + i, 16);
    expect(onFatal).not.toHaveBeenCalled();
  });
});

describe('resize and dispose', () => {
  it('forwards valid sizes to the instance', () => {
    const host = makeHost('MON2');
    host.handleWindowConfig(makeWindowConfig());
    host.resize(800, 600);
    expect(lastInstance().resize).toHaveBeenCalledWith(800, 600);
  });

  it('ignores non-positive sizes', () => {
    const host = makeHost('MON2');
    host.handleWindowConfig(makeWindowConfig());
    const instance = lastInstance();
    instance.resize.mockClear(); // swapTo may have called it with the stub element size
    host.resize(0, 600);
    host.resize(800, -1);
    expect(instance.resize).not.toHaveBeenCalled();
  });

  it('swallows a throwing resize', () => {
    const host = makeHost('MON2');
    host.handleWindowConfig(makeWindowConfig());
    lastInstance().resize.mockImplementation(() => {
      throw new Error('resize boom');
    });
    expect(() => host.resize(800, 600)).not.toThrow();
  });

  it('dispose releases the instance', () => {
    const host = makeHost('MON2');
    host.handleWindowConfig(makeWindowConfig());
    const instance = lastInstance();
    host.dispose();
    expect(instance.dispose).toHaveBeenCalledTimes(1);
    expect(host.hasInstance()).toBe(false);
  });
});
