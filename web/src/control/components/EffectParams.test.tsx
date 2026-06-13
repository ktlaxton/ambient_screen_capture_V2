// Story 7.2 AC2/AC7: the auto-generated param UI renders the new 'color' and
// 'palette' control types straight from ParamDefs (no per-effect UI code).
// Rendered with react-dom into happy-dom; no user-event simulation needed.
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { act } from 'react';
import { createRoot, type Root } from 'react-dom/client';
import type { ApplicationSettings } from '../../shared/bridge';
import { PALETTES } from '../../effects/shared/palettes';
import { useControlStore } from '../store';
import { EffectParams } from './EffectParams';

(globalThis as Record<string, unknown>).IS_REACT_ACT_ENVIRONMENT = true;

function makeSettings(): ApplicationSettings {
  return {
    isEnabled: false,
    sourceMonitorId: '',
    targetMonitorIds: [],
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
    closeAction: 'ask',
    updateFeedUrl: '',
    ambientDevicesEnabled: false,
    peripheralBrightness: 1,
    devicePlacements: {},
    rgbProviders: ['corsair'],
    audioReactiveDevices: false,
    audioReactiveDepth: 0.5,
  };
}

describe('EffectParams control rendering', () => {
  let host: HTMLDivElement;
  let root: Root;

  beforeEach(() => {
    host = document.createElement('div');
    document.body.appendChild(host);
    root = createRoot(host);
    useControlStore.setState({
      settings: makeSettings(),
      selectedEffectId: 'edge-glow',
    });
  });

  afterEach(() => {
    act(() => root.unmount());
    host.remove();
  });

  it('renders a color picker for color params and swatches for palette params', () => {
    act(() => {
      root.render(<EffectParams />);
    });

    const colorInput = host.querySelector('input.ctl-color') as HTMLInputElement | null;
    expect(colorInput).not.toBeNull();
    expect(colorInput!.type).toBe('color');
    expect(colorInput!.value.toLowerCase()).toBe('#4f7cff');

    const swatchGroup = host.querySelector('.ctl-palette');
    expect(swatchGroup).not.toBeNull();
    const swatches = swatchGroup!.querySelectorAll('.ctl-palette-swatch');
    expect(swatches.length).toBe(PALETTES.length);
    const active = swatchGroup!.querySelectorAll('.ctl-palette-swatch.active');
    expect(active.length).toBe(1);
    expect(active[0].getAttribute('aria-label')).toBe('Warm'); // edge-glow default
  });

  it('still renders range sliders and toggles alongside the new types', () => {
    act(() => {
      root.render(<EffectParams />);
    });
    expect(host.querySelectorAll('input[type="range"]').length).toBeGreaterThan(0);
    expect(host.querySelectorAll('.ctl-toggle').length).toBeGreaterThan(0);
  });
});
