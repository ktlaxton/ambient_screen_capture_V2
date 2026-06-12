// ============================================================================
// bridgeGlue — the single place where the control UI talks to the engine.
// Subscribes once on boot, exposes optimistic command helpers (local patch
// immediately, debounced send), and lets engine config pushes reconcile.
// ============================================================================
import { getBridge } from '../shared/bridge';
import type {
  ApplicationSettings,
  CommandMap,
  CommandType,
  EffectParams,
} from '../shared/bridge';
import { effectsById } from '../effects/registry';
import { defaultParamsOf } from '../effects/types';
import { startFrameFeed } from './frameFeed';
import { useControlStore } from './store';

let initialized = false;

/** Wire the bridge to the store. Idempotent; call once from main.tsx. */
export function initBridgeGlue(): void {
  if (initialized) return;
  initialized = true;

  const bridge = getBridge();
  const store = useControlStore.getState();
  store.setConnected(bridge.isHosted);

  bridge.on('config', (payload) => useControlStore.getState().applyConfig(payload));
  bridge.on('monitors', (payload) => useControlStore.getState().setMonitors(payload.monitors));
  bridge.on('status', (payload) =>
    useControlStore.getState().pushToast(payload.level, payload.message),
  );

  startFrameFeed();
  bridge.send('requestState', {});
}

// ----------------------------------------------------------------- sends --

export function send<K extends CommandType>(type: K, payload: CommandMap[K]): void {
  getBridge().send(type, payload);
}

const SEND_DEBOUNCE_MS = 120;
const timers = new Map<string, ReturnType<typeof setTimeout>>();

function sendDebounced<K extends CommandType>(key: string, type: K, payload: CommandMap[K]): void {
  const existing = timers.get(key);
  if (existing !== undefined) clearTimeout(existing);
  timers.set(
    key,
    setTimeout(() => {
      timers.delete(key);
      send(type, payload);
    }, SEND_DEBOUNCE_MS),
  );
}

// ------------------------------------------------- optimistic command API --

export function setEnabled(enabled: boolean): void {
  useControlStore.getState().patchSettings({ isEnabled: enabled });
  send('setEnabled', { enabled });
}

export function setSourceMonitor(monitorId: string): void {
  const { settings, patchSettings } = useControlStore.getState();
  if (!settings || settings.sourceMonitorId === monitorId) return;
  // A monitor cannot be both source and target.
  const targets = settings.targetMonitorIds.filter((id) => id !== monitorId);
  patchSettings({ sourceMonitorId: monitorId, targetMonitorIds: targets });
  send('setSourceMonitor', { monitorId });
  if (targets.length !== settings.targetMonitorIds.length) {
    send('setTargetMonitors', { monitorIds: targets });
  }
}

export function toggleTargetMonitor(monitorId: string): void {
  const { settings, patchSettings } = useControlStore.getState();
  if (!settings || settings.sourceMonitorId === monitorId) return;
  const has = settings.targetMonitorIds.includes(monitorId);
  const targets = has
    ? settings.targetMonitorIds.filter((id) => id !== monitorId)
    : [...settings.targetMonitorIds, monitorId];
  patchSettings({ targetMonitorIds: targets });
  send('setTargetMonitors', { monitorIds: targets });
}

/** Set the global effect (clears per-monitor overrides, per contract). */
export function setEffectGlobal(effectId: string): void {
  const { patchSettings, selectEffect } = useControlStore.getState();
  patchSettings({ activeEffectId: effectId, effectByMonitorId: {} });
  selectEffect(effectId);
  send('setEffect', { effectId });
}

/** Per-monitor effect override; empty effectId clears back to global. */
export function setEffectForMonitor(monitorId: string, effectId: string): void {
  const { settings, patchSettings } = useControlStore.getState();
  if (!settings) return;
  const overrides = { ...settings.effectByMonitorId };
  const clearing = effectId === '' || effectId === settings.activeEffectId;
  if (clearing) {
    delete overrides[monitorId];
  } else {
    overrides[monitorId] = effectId;
  }
  patchSettings({ effectByMonitorId: overrides });
  // Empty effectId is the protocol's "clear override" — sending the current global id
  // instead would pin this monitor to it even after the global effect changes.
  send('setEffect', { monitorId, effectId: clearing ? '' : effectId });
}

/** Resolved params for an effect: stored values merged over module defaults. */
export function resolvedEffectParams(effectId: string): EffectParams {
  const settings = useControlStore.getState().settings;
  const module = effectsById[effectId];
  const defaults = module ? defaultParamsOf(module) : {};
  return { ...defaults, ...(settings?.effectParamsById[effectId] ?? {}) };
}

export function setEffectParam(
  effectId: string,
  key: string,
  value: number | string | boolean,
): void {
  const { settings, patchSettings } = useControlStore.getState();
  if (!settings) return;
  const params = { ...(settings.effectParamsById[effectId] ?? {}), [key]: value };
  patchSettings({
    effectParamsById: { ...settings.effectParamsById, [effectId]: params },
  });
  sendDebounced(`setEffectParams:${effectId}`, 'setEffectParams', {
    effectId,
    params: { ...resolvedEffectParams(effectId), [key]: value },
  });
}

export function resetEffectParams(effectId: string): void {
  const { settings, patchSettings } = useControlStore.getState();
  if (!settings) return;
  const module = effectsById[effectId];
  const defaults = module ? defaultParamsOf(module) : {};
  patchSettings({
    effectParamsById: { ...settings.effectParamsById, [effectId]: { ...defaults } },
  });
  send('setEffectParams', { effectId, params: defaults });
}

export type GlobalField = 'intensity' | 'smoothing' | 'brightness' | 'audioSensitivity' | 'maxFps';

const GLOBAL_TO_SETTING: Record<
  GlobalField,
  keyof Pick<
    ApplicationSettings,
    'globalIntensity' | 'smoothing' | 'brightness' | 'audioSensitivity' | 'maxFps'
  >
> = {
  intensity: 'globalIntensity',
  smoothing: 'smoothing',
  brightness: 'brightness',
  audioSensitivity: 'audioSensitivity',
  maxFps: 'maxFps',
};

export function setGlobal(field: GlobalField, value: number): void {
  useControlStore.getState().patchSettings({ [GLOBAL_TO_SETTING[field]]: value });
  sendDebounced(`setGlobal:${field}`, 'setGlobal', { [field]: value });
}

export function setAutostart(enabled: boolean): void {
  useControlStore.getState().patchSettings({ autostart: enabled });
  send('setAutostart', { enabled });
}

export function setHotkey(action: string, keys: string): void {
  const { settings, patchSettings } = useControlStore.getState();
  if (settings) patchSettings({ hotkeys: { ...settings.hotkeys, [action]: keys } });
  send('setHotkey', { action, keys });
}

export function savePreset(name: string): void {
  send('savePreset', { name });
}

export function loadPreset(name: string): void {
  send('loadPreset', { name });
}

export function deletePreset(name: string): void {
  send('deletePreset', { name });
}

export function windowCommand(action: CommandMap['windowCommand']['action']): void {
  send('windowCommand', { action });
}

export function completeOnboarding(): void {
  useControlStore.getState().patchSettings({ firstRunCompleted: true });
  send('completeOnboarding', {});
}
