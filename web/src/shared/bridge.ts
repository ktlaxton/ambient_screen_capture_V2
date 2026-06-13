// =============================================================================
// AmbientFx bridge contract — the TypeScript mirror of src/Engine/Bridge/*.cs.
// Treat this as a versioned contract: any change here must change the C# side.
// =============================================================================
import type { WebView2Bridge as WebView2Host } from './webview2';
import { createSimulatorBridge } from './simulator';

export type RGB = [number, number, number];

/** Edge-zone colors, sRGB 0-255. Top/bottom run left-to-right; left/right top-to-bottom. */
export interface EdgeColors {
  top: RGB[];
  bottom: RGB[];
  left: RGB[];
  right: RGB[];
}

export interface AudioData {
  /** Overall audio intensity 0..1. */
  intensity: number;
  /** Normalized 0..1 per log-spaced frequency band, low to high. */
  bands: number[];
}

/** The high-frequency per-frame stream from the engine (~MaxFps per second). */
export interface FramePayload {
  /** Engine timestamp, ms, monotonic. */
  t: number;
  edges: EdgeColors;
  /** Overall dominant color [r,g,b] 0-255. */
  dominant: RGB;
  audio: AudioData;
}

export type StatusLevel = 'info' | 'warn' | 'error';

export interface StatusPayload {
  level: StatusLevel;
  message: string;
}

/** Bounds are device pixels in virtual-desktop coordinates. */
export interface MonitorInfo {
  id: string;
  name: string;
  x: number;
  y: number;
  width: number;
  height: number;
  isPrimary: boolean;
}

export type EffectParams = Record<string, number | string | boolean>;

/** What closing the control window does (Story 7.3). 'ask' shows a one-time choice. */
export type CloseAction = 'ask' | 'quit' | 'minimizeToTray';

export interface Preset {
  name: string;
  snapshot: ApplicationSettings;
}

export interface ApplicationSettings {
  isEnabled: boolean;
  sourceMonitorId: string;
  targetMonitorIds: string[];
  activeEffectId: string;
  effectByMonitorId: Record<string, string>;
  audioSensitivity: number;
  globalIntensity: number;
  smoothing: number;
  brightness: number;
  maxFps: number;
  zonesPerEdge: number;
  audioBands: number;
  autostart: boolean;
  effectParamsById: Record<string, EffectParams>;
  hotkeys: Record<string, string>;
  presets: Preset[];
  activePresetName: string;
  firstRunCompleted: boolean;
  closeAction: CloseAction;
  /** Velopack update feed (Story 7.4); blank = the project's GitHub Releases feed. */
  updateFeedUrl: string;
  /** Master toggle for ambient RGB peripherals (Epic 8 / Story 8.1). */
  ambientDevicesEnabled: boolean;
  /** Peripheral LED brightness 0..1, separate from the on-screen brightness. */
  peripheralBrightness: number;
  /** Per-device placement overrides keyed by stable device id (Story 8.2); no entry = Auto. */
  devicePlacements: Record<string, DevicePlacement>;
  /** Enabled RGB vendor providers (Story 8.3), e.g. ["corsair"]. Others are opt-in. */
  rgbProviders: string[];
  /** Audio-reactive peripheral layer on/off (Story 8.3). */
  audioReactiveDevices: boolean;
  /** Audio-reactive depth 0..1: 0 = no effect, 1 = silence goes dark. */
  audioReactiveDepth: number;
}

/** Where a device sits relative to the screen (Story 8.2). auto/behind = nearest-edge mapping. */
export type DeviceAnchor = 'auto' | 'left' | 'right' | 'above' | 'below' | 'behind' | 'surround';

/** Per-device placement + tuning (Story 8.2). */
export interface DevicePlacement {
  anchor: DeviceAnchor;
  /** Reverses zone order along the fed edge (strips mounted backwards). */
  flip: boolean;
  /** Per-device multiplier 0..1 on top of the global peripheral brightness. */
  brightness: number;
  /** False excludes the device (its LEDs go dark) without disabling the feature. */
  enabled: boolean;
}

/** Ambient peripheral connection state (Story 8.1). These are normal states, not errors. */
export type DeviceConnectionState =
  | 'disabled'
  | 'connecting'
  | 'connected'
  | 'icueNotFound'
  | 'refused'
  | 'noDevices'
  | 'error';

/** One discovered RGB peripheral for the read-only device list. */
export interface AmbientDeviceInfo {
  id: string;
  name: string;
  /** Vendor SDK device class, e.g. "Keyboard", "Mouse", "LedStripe". */
  type: string;
  ledCount: number;
}

/** One vendor provider's outcome in the last connect (Story 8.3). */
export type RgbProviderState = 'connected' | 'unavailable' | 'refused' | 'error';

export interface RgbProviderStatus {
  /** Stable provider key, e.g. "corsair", "razer". */
  key: string;
  name: string;
  state: RgbProviderState;
  deviceCount: number;
}

/** Ambient RGB peripheral state, pushed on every change and on requestState (Story 8.1). */
export interface DevicesPayload {
  connectionState: DeviceConnectionState;
  devices: AmbientDeviceInfo[];
  /** Per-vendor outcomes from the last connect (Story 8.3); empty when not connected. */
  providers: RgbProviderStatus[];
}

export interface ConfigPayload {
  settings: ApplicationSettings;
  firstRun: boolean;
  appVersion: string;
}

export interface MonitorsPayload {
  monitors: MonitorInfo[];
}

export type MonitorRelation = 'left' | 'right' | 'above' | 'below' | 'none';

/** Sent to each effect window after load and whenever its assignment changes (FR7). */
export interface WindowConfigPayload {
  monitorId: string;
  effectId: string;
  monitor: MonitorInfo | null;
  source: MonitorInfo | null;
  relation: MonitorRelation;
}

/** Control-window native state (keeps the custom title bar's maximize glyph honest). */
export interface WindowStatePayload {
  state: 'normal' | 'maximized' | 'minimized';
}

/** Engine -> web messages. */
export interface EngineMessageMap {
  frame: FramePayload;
  status: StatusPayload;
  config: ConfigPayload;
  monitors: MonitorsPayload;
  windowConfig: WindowConfigPayload;
  windowState: WindowStatePayload;
  /** The user closed the window while closeAction is 'ask' — show the choice modal (Story 7.3). */
  closePrompt: Record<string, never>;
  /** Ambient RGB peripheral connection state + device list (Story 8.1). */
  devices: DevicesPayload;
}

/** Web -> engine commands. */
export interface CommandMap {
  setEnabled: { enabled: boolean };
  setSourceMonitor: { monitorId: string };
  setTargetMonitors: { monitorIds: string[] };
  /** monitorId omitted/null/"all" => set the global effect and clear per-monitor overrides.
   *  With a monitorId, an EMPTY effectId clears that monitor's override back to the global. */
  setEffect: { monitorId?: string | null; effectId: string };
  setEffectParams: { effectId: string; params: EffectParams };
  /** Partial update — omitted fields are left unchanged. */
  setGlobal: {
    intensity?: number;
    smoothing?: number;
    brightness?: number;
    audioSensitivity?: number;
    maxFps?: number;
  };
  savePreset: { name: string };
  loadPreset: { name: string };
  deletePreset: { name: string };
  setAutostart: { enabled: boolean };
  /** keys: gesture string like "Ctrl+Alt+A"; empty string unbinds. */
  setHotkey: { action: string; keys: string };
  requestState: Record<string, never>;
  /** Custom-chrome window controls for the control window. */
  windowCommand: { action: 'minimize' | 'maximize' | 'restore' | 'close' };
  /** Fully terminate the app (Story 7.3) — same path as the tray Exit item. */
  quitApp: Record<string, never>;
  /** Persisted close-the-window behavior. */
  setCloseAction: { action: CloseAction };
  /** The user's answer to the closePrompt modal. */
  resolveClosePrompt: { action: 'quit' | 'minimizeToTray'; remember: boolean };
  /** Manual update check (Story 7.4); results come back as status toasts. */
  checkForUpdates: Record<string, never>;
  completeOnboarding: Record<string, never>;
  /** Web layer fatal/runtime error report so the engine can log + toast (NFR5/AC7). */
  reportError: { source: string; message: string };
  /** Ambient RGB peripherals (Stories 8.1/8.3). Partial update — omitted fields are left unchanged. */
  setDevices: { enabled?: boolean; brightness?: number; audioReactive?: boolean; audioDepth?: number };
  /** Replaces the enabled RGB vendor provider set (Story 8.3); the engine reconnects. */
  setRgbProviders: { providers: string[] };
  /** Per-device placement/tuning (Story 8.2). Partial update against the stored placement. */
  setDevicePlacement: {
    deviceId: string;
    anchor?: DeviceAnchor;
    flip?: boolean;
    brightness?: number;
    enabled?: boolean;
  };
}

export type EngineMessageType = keyof EngineMessageMap;
export type CommandType = keyof CommandMap;

export interface Bridge {
  /** True when running inside the WebView2 host; false in a plain browser (simulator). */
  readonly isHosted: boolean;
  send<K extends CommandType>(type: K, payload: CommandMap[K]): void;
  /** Subscribe to an engine message. Returns an unsubscribe function. */
  on<K extends EngineMessageType>(type: K, handler: (payload: EngineMessageMap[K]) => void): () => void;
  dispose(): void;
}

/** Shared dispatch table used by both the real bridge and the simulator. */
export class MessageHub {
  private handlers = new Map<string, Set<(payload: never) => void>>();

  on<K extends EngineMessageType>(type: K, handler: (payload: EngineMessageMap[K]) => void): () => void {
    let set = this.handlers.get(type);
    if (!set) {
      set = new Set();
      this.handlers.set(type, set);
    }
    set.add(handler as (payload: never) => void);
    return () => set.delete(handler as (payload: never) => void);
  }

  emit<K extends EngineMessageType>(type: K, payload: EngineMessageMap[K]): void {
    const set = this.handlers.get(type);
    if (!set) return;
    for (const handler of [...set]) {
      try {
        (handler as (p: EngineMessageMap[K]) => void)(payload);
      } catch (err) {
        console.error(`[bridge] handler for "${type}" threw`, err);
      }
    }
  }

  clear(): void {
    this.handlers.clear();
  }
}

class WebView2BridgeImpl implements Bridge {
  readonly isHosted = true;
  private hub = new MessageHub();
  private listener: (event: { data: unknown }) => void;

  constructor(private host: WebView2Host) {
    this.listener = (event) => {
      const msg = event.data as { type?: unknown; payload?: unknown } | null;
      if (!msg || typeof msg.type !== 'string') return;
      this.hub.emit(msg.type as EngineMessageType, msg.payload as never);
    };
    host.addEventListener('message', this.listener);
  }

  send<K extends CommandType>(type: K, payload: CommandMap[K]): void {
    this.host.postMessage({ type, payload });
  }

  on<K extends EngineMessageType>(type: K, handler: (payload: EngineMessageMap[K]) => void): () => void {
    return this.hub.on(type, handler);
  }

  dispose(): void {
    this.host.removeEventListener('message', this.listener);
    this.hub.clear();
  }
}

/** Creates a new bridge: the real WebView2 bridge when hosted, otherwise the dev simulator. */
export function createBridge(): Bridge {
  const host = typeof window !== 'undefined' ? window.chrome?.webview : undefined;
  if (host) return new WebView2BridgeImpl(host);
  console.info('[bridge] window.chrome.webview not found — running with the simulated engine');
  return createSimulatorBridge();
}

let singleton: Bridge | null = null;

/** App-wide shared bridge instance. */
export function getBridge(): Bridge {
  singleton ??= createBridge();
  return singleton;
}

/** Test hook: reset the shared instance. */
export function resetBridgeForTest(): void {
  singleton?.dispose();
  singleton = null;
}
