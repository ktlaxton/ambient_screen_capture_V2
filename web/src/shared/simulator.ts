// Dev/test stand-in for the C# engine. Active whenever the page runs outside
// WebView2 (plain browser, vitest). Emits a live synthetic frame stream and
// answers commands against an in-memory settings object, so the control UI and
// every effect can be developed and demoed without the native host.
//
// Useful URL params in a browser:
//   effects.html?effectId=plasma     -> simulator assigns that effect to this window
//   control.html?firstrun=1          -> forces the onboarding flow
import { MessageHub } from './bridge';
import type {
  ApplicationSettings,
  Bridge,
  CommandMap,
  CommandType,
  EngineMessageMap,
  EngineMessageType,
  FramePayload,
  MonitorInfo,
  RGB,
  WindowConfigPayload,
} from './bridge';

const SIM_MONITORS: MonitorInfo[] = [
  { id: '\\\\.\\DISPLAY1', name: 'Primary 1440p (sim)', x: 0, y: 0, width: 2560, height: 1440, isPrimary: true },
  { id: '\\\\.\\DISPLAY2', name: 'Right 1080p (sim)', x: 2560, y: 180, width: 1920, height: 1080, isPrimary: false },
  { id: '\\\\.\\DISPLAY3', name: 'Top 1080p (sim)', x: 320, y: -1080, width: 1920, height: 1080, isPrimary: false },
];

export function defaultSimSettings(): ApplicationSettings {
  return {
    isEnabled: true,
    sourceMonitorId: SIM_MONITORS[0].id,
    targetMonitorIds: [SIM_MONITORS[1].id, SIM_MONITORS[2].id],
    activeEffectId: 'edge-glow',
    effectByMonitorId: {},
    audioSensitivity: 0.5,
    globalIntensity: 1.0,
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
  };
}

function clone<T>(value: T): T {
  return JSON.parse(JSON.stringify(value)) as T;
}

function hslToRgb(h: number, s: number, l: number): RGB {
  const k = (n: number) => (n + h * 12) % 12;
  const a = s * Math.min(l, 1 - l);
  const f = (n: number) => l - a * Math.max(-1, Math.min(k(n) - 3, Math.min(9 - k(n), 1)));
  return [Math.round(f(0) * 255), Math.round(f(8) * 255), Math.round(f(4) * 255)];
}

/** Synthesizes a plausible frame: drifting hues around the screen + a 124bpm "track". */
export function makeSimFrame(elapsedMs: number, zonesPerEdge: number, audioBands: number): FramePayload {
  const ts = elapsedMs / 1000;
  const baseHue = (ts * 18) % 360;

  const edge = (count: number, offsetDeg: number, axisPhase: number): RGB[] =>
    Array.from({ length: count }, (_, i) => {
      const hue = (baseHue + offsetDeg + (i / count) * 90) % 360;
      const lum = 0.45 + 0.12 * Math.sin(ts * 1.8 + axisPhase + i * 0.7);
      return hslToRgb(hue / 360, 0.75, lum);
    });

  const beatPhase = (ts * (124 / 60)) % 1; // 124 bpm
  const kick = Math.max(0, 1 - beatPhase * 4) ** 1.5;
  const bands = Array.from({ length: audioBands }, (_, i) => {
    const fracHigh = i / Math.max(1, audioBands - 1);
    const bassBoost = i <= audioBands / 4 ? kick * (1 - fracHigh) : 0;
    const melody = (0.3 + 0.25 * Math.sin(ts * (1.3 + i * 0.43) + i * 1.7)) * (1 - fracHigh * 0.55);
    const sparkle = fracHigh > 0.6 ? 0.12 * Math.max(0, Math.sin(ts * 9 + i * 3)) : 0;
    return Math.max(0, Math.min(1, bassBoost + melody + sparkle));
  });
  const intensity = Math.min(1, bands.reduce((a, b) => a + b, 0) / bands.length + kick * 0.35);

  return {
    t: elapsedMs,
    edges: {
      top: edge(zonesPerEdge, 0, 0),
      bottom: edge(zonesPerEdge, 180, 2.1),
      left: edge(zonesPerEdge, 90, 4.2),
      right: edge(zonesPerEdge, 270, 1.3),
    },
    dominant: hslToRgb(baseHue / 360, 0.7, 0.5),
    audio: { intensity, bands },
  };
}

export function createSimulatorBridge(): Bridge {
  const hub = new MessageHub();
  const settings = defaultSimSettings();
  const urlParams = typeof location !== 'undefined' ? new URLSearchParams(location.search) : new URLSearchParams();
  if (urlParams.get('firstrun') === '1') settings.firstRunCompleted = false;

  const emit = <K extends EngineMessageType>(type: K, payload: EngineMessageMap[K]) => hub.emit(type, payload);

  const pushConfig = () =>
    emit('config', {
      settings: clone(settings),
      firstRun: !settings.firstRunCompleted,
      appVersion: '0.0.0-simulator',
    });

  const windowConfig = (): WindowConfigPayload => {
    const monitorId = urlParams.get('monitorId') ?? SIM_MONITORS[1].id;
    const monitor = SIM_MONITORS.find((m) => m.id === monitorId) ?? SIM_MONITORS[1];
    const effectId =
      urlParams.get('effectId') ?? settings.effectByMonitorId[monitor.id] ?? settings.activeEffectId;
    return { monitorId: monitor.id, effectId, monitor, source: SIM_MONITORS[0], relation: 'right' };
  };

  const start = typeof performance !== 'undefined' ? performance.now() : 0;
  const frameTimer = setInterval(() => {
    if (!settings.isEnabled) return;
    emit('frame', makeSimFrame(performance.now() - start, settings.zonesPerEdge, settings.audioBands));
  }, 1000 / 60);

  const statusTimer = setTimeout(
    () => emit('status', { level: 'info', message: 'Simulator mode — native engine not connected.' }),
    800,
  );

  const send = <K extends CommandType>(type: K, payload: CommandMap[K]): void => {
    switch (type) {
      case 'requestState': {
        pushConfig();
        emit('monitors', { monitors: clone(SIM_MONITORS) });
        emit('windowConfig', windowConfig());
        break;
      }
      case 'setEnabled': {
        settings.isEnabled = (payload as CommandMap['setEnabled']).enabled;
        pushConfig();
        break;
      }
      case 'setSourceMonitor': {
        settings.sourceMonitorId = (payload as CommandMap['setSourceMonitor']).monitorId;
        pushConfig();
        break;
      }
      case 'setTargetMonitors': {
        settings.targetMonitorIds = [...(payload as CommandMap['setTargetMonitors']).monitorIds];
        pushConfig();
        break;
      }
      case 'setEffect': {
        const cmd = payload as CommandMap['setEffect'];
        if (!cmd.monitorId || cmd.monitorId === 'all') {
          settings.activeEffectId = cmd.effectId;
          settings.effectByMonitorId = {};
        } else {
          settings.effectByMonitorId[cmd.monitorId] = cmd.effectId;
        }
        pushConfig();
        emit('windowConfig', windowConfig());
        break;
      }
      case 'setEffectParams': {
        const cmd = payload as CommandMap['setEffectParams'];
        settings.effectParamsById[cmd.effectId] = {
          ...settings.effectParamsById[cmd.effectId],
          ...cmd.params,
        };
        pushConfig();
        break;
      }
      case 'setGlobal': {
        const g = payload as CommandMap['setGlobal'];
        if (g.intensity !== undefined) settings.globalIntensity = g.intensity;
        if (g.smoothing !== undefined) settings.smoothing = g.smoothing;
        if (g.brightness !== undefined) settings.brightness = g.brightness;
        if (g.audioSensitivity !== undefined) settings.audioSensitivity = g.audioSensitivity;
        if (g.maxFps !== undefined) settings.maxFps = g.maxFps;
        pushConfig();
        break;
      }
      case 'savePreset': {
        const name = (payload as CommandMap['savePreset']).name.trim();
        if (!name) break;
        const snapshot = clone(settings);
        snapshot.presets = [];
        settings.presets = [...settings.presets.filter((p) => p.name !== name), { name, snapshot }];
        settings.activePresetName = name;
        pushConfig();
        break;
      }
      case 'loadPreset': {
        const name = (payload as CommandMap['loadPreset']).name;
        const preset = settings.presets.find((p) => p.name === name);
        if (!preset) break;
        const keep = settings.presets;
        Object.assign(settings, clone(preset.snapshot));
        settings.presets = keep;
        settings.activePresetName = name;
        pushConfig();
        break;
      }
      case 'deletePreset': {
        const name = (payload as CommandMap['deletePreset']).name;
        settings.presets = settings.presets.filter((p) => p.name !== name);
        if (settings.activePresetName === name) settings.activePresetName = '';
        pushConfig();
        break;
      }
      case 'setAutostart': {
        settings.autostart = (payload as CommandMap['setAutostart']).enabled;
        pushConfig();
        break;
      }
      case 'setHotkey': {
        const cmd = payload as CommandMap['setHotkey'];
        settings.hotkeys[cmd.action] = cmd.keys;
        pushConfig();
        break;
      }
      case 'completeOnboarding': {
        settings.firstRunCompleted = true;
        pushConfig();
        break;
      }
      case 'windowCommand': {
        console.info('[simulator] windowCommand:', (payload as CommandMap['windowCommand']).action);
        break;
      }
      case 'reportError': {
        const cmd = payload as CommandMap['reportError'];
        console.warn('[simulator] web error report:', cmd.source, cmd.message);
        break;
      }
      default:
        console.warn('[simulator] unhandled command:', type, payload);
    }
  };

  return {
    isHosted: false,
    send,
    on: (type, handler) => hub.on(type, handler),
    dispose: () => {
      clearInterval(frameTimer);
      clearTimeout(statusTimer);
      hub.clear();
    },
  };
}
