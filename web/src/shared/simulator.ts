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
  AmbientDeviceInfo,
  ApplicationSettings,
  Bridge,
  CommandMap,
  CommandType,
  DevicesPayload,
  EngineMessageMap,
  EngineMessageType,
  FramePayload,
  MonitorInfo,
  RGB,
  RgbProviderStatus,
  WindowConfigPayload,
} from './bridge';

const SIM_DEVICES: AmbientDeviceInfo[] = [
  { id: '0:K95 RGB Platinum (sim)', name: 'K95 RGB Platinum (sim)', type: 'Keyboard', ledCount: 108 },
  { id: '1:Dark Core RGB Pro (sim)', name: 'Dark Core RGB Pro (sim)', type: 'Mouse', ledCount: 4 },
  { id: '2:LS100 Light Strip (sim)', name: 'LS100 Light Strip (sim)', type: 'LedStripe', ledCount: 27 },
];

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

  // Coarse fallback relation, mirroring MonitorLayout.ComputeRelation (effects
  // primarily read the rects; this keeps the payload self-consistent).
  const relationFor = (source: MonitorInfo, target: MonitorInfo): WindowConfigPayload['relation'] => {
    if (source.id === target.id) return 'none';
    const ndx = (target.x + target.width / 2 - (source.x + source.width / 2)) / ((source.width + target.width) / 2);
    const ndy = (target.y + target.height / 2 - (source.y + source.height / 2)) / ((source.height + target.height) / 2);
    if (Math.abs(ndx) < 0.05 && Math.abs(ndy) < 0.05) return 'none';
    if (Math.abs(ndx) >= Math.abs(ndy)) return ndx > 0 ? 'right' : 'left';
    return ndy > 0 ? 'below' : 'above';
  };

  const windowConfig = (): WindowConfigPayload => {
    const monitorId = urlParams.get('monitorId') ?? SIM_MONITORS[1].id;
    const monitor = SIM_MONITORS.find((m) => m.id === monitorId) ?? SIM_MONITORS[1];
    const source = SIM_MONITORS.find((m) => m.id === settings.sourceMonitorId) ?? SIM_MONITORS[0];
    const effectId =
      urlParams.get('effectId') ?? settings.effectByMonitorId[monitor.id] ?? settings.activeEffectId;
    return { monitorId: monitor.id, effectId, monitor, source, relation: relationFor(source, monitor) };
  };

  // Peripherals mirror the engine: connected only while the master power AND the
  // peripherals toggle are both on (the device service runs with the pipeline).
  // Corsair is the only simulated vendor; other enabled providers report unavailable.
  const devicesPayload = (): DevicesPayload => {
    if (!settings.isEnabled || !settings.ambientDevicesEnabled) {
      return { connectionState: 'disabled', devices: [], providers: [] };
    }
    const corsairOn = settings.rgbProviders.includes('corsair');
    const providers = settings.rgbProviders.map((key) => ({
      key,
      name: key === 'corsair' ? 'Corsair iCUE' : key,
      state: (key === 'corsair' ? 'connected' : 'unavailable') as RgbProviderStatus['state'],
      deviceCount: key === 'corsair' ? SIM_DEVICES.length : 0,
    }));
    return corsairOn
      ? { connectionState: 'connected', devices: clone(SIM_DEVICES), providers }
      : { connectionState: 'icueNotFound', devices: [], providers };
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
        emit('devices', devicesPayload());
        break;
      }
      case 'setEnabled': {
        settings.isEnabled = (payload as CommandMap['setEnabled']).enabled;
        pushConfig();
        emit('devices', devicesPayload());
        break;
      }
      case 'setDevices': {
        const cmd = payload as CommandMap['setDevices'];
        if (cmd.enabled !== undefined) settings.ambientDevicesEnabled = cmd.enabled;
        if (cmd.brightness !== undefined) settings.peripheralBrightness = cmd.brightness;
        if (cmd.audioReactive !== undefined) settings.audioReactiveDevices = cmd.audioReactive;
        if (cmd.audioDepth !== undefined) settings.audioReactiveDepth = cmd.audioDepth;
        pushConfig();
        emit('devices', devicesPayload());
        break;
      }
      case 'setRgbProviders': {
        settings.rgbProviders = [...(payload as CommandMap['setRgbProviders']).providers];
        pushConfig();
        emit('devices', devicesPayload());
        break;
      }
      case 'setDevicePlacement': {
        const cmd = payload as CommandMap['setDevicePlacement'];
        const current = settings.devicePlacements[cmd.deviceId] ?? {
          anchor: 'auto',
          flip: false,
          brightness: 1,
          enabled: true,
        };
        settings.devicePlacements[cmd.deviceId] = {
          ...current,
          ...(cmd.anchor !== undefined && { anchor: cmd.anchor }),
          ...(cmd.flip !== undefined && { flip: cmd.flip }),
          ...(cmd.brightness !== undefined && { brightness: cmd.brightness }),
          ...(cmd.enabled !== undefined && { enabled: cmd.enabled }),
        };
        pushConfig();
        break;
      }
      case 'setSourceMonitor': {
        settings.sourceMonitorId = (payload as CommandMap['setSourceMonitor']).monitorId;
        pushConfig();
        emit('windowConfig', windowConfig()); // source moved -> layout-aware effects re-orient
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
        const action = (payload as CommandMap['windowCommand']).action;
        console.info('[simulator] windowCommand:', action);
        // Mirror the engine's close routing so the prompt is testable in a browser.
        if (action === 'close' && settings.closeAction === 'ask') emit('closePrompt', {});
        break;
      }
      case 'quitApp': {
        emit('status', { level: 'info', message: 'Simulator: quitApp would terminate the engine.' });
        break;
      }
      case 'checkForUpdates': {
        emit('status', { level: 'info', message: 'Simulator: updates are only available in the installed app.' });
        break;
      }
      case 'setCloseAction': {
        settings.closeAction = (payload as CommandMap['setCloseAction']).action;
        pushConfig();
        break;
      }
      case 'resolveClosePrompt': {
        const cmd = payload as CommandMap['resolveClosePrompt'];
        if (cmd.remember) settings.closeAction = cmd.action;
        pushConfig();
        emit('status', { level: 'info', message: `Simulator: close resolved as ${cmd.action}.` });
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
