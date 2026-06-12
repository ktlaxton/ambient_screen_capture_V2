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
  completeOnboarding: Record<string, never>;
  /** Web layer fatal/runtime error report so the engine can log + toast (NFR5/AC7). */
  reportError: { source: string; message: string };
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
