// ============================================================================
// zustand store for the control UI. Holds low-frequency state only — the 60Hz
// frame stream deliberately bypasses this (see frameFeed.ts).
// ============================================================================
import { create } from 'zustand';
import type {
  ApplicationSettings,
  ConfigPayload,
  MonitorInfo,
  StatusLevel,
} from '../shared/bridge';

export interface Toast {
  id: number;
  level: StatusLevel;
  message: string;
  /** Errors are sticky; info/warn auto-dismiss. */
  sticky: boolean;
}

interface ControlState {
  settings: ApplicationSettings | null;
  monitors: MonitorInfo[];
  /** True when running inside the WebView2 host (vs the browser simulator). */
  connected: boolean;
  appVersion: string;
  toasts: Toast[];
  /** UI-local selection driving the effect parameters panel. */
  selectedEffectId: string | null;
  /** First-run onboarding overlay visibility. */
  onboardingOpen: boolean;
  /** Once the user finishes or skips, never auto-reopen this session. */
  onboardingDecided: boolean;
}

interface ControlActions {
  setConnected: (connected: boolean) => void;
  applyConfig: (payload: ConfigPayload) => void;
  setMonitors: (monitors: MonitorInfo[]) => void;
  pushToast: (level: StatusLevel, message: string) => void;
  dismissToast: (id: number) => void;
  selectEffect: (effectId: string) => void;
  /** Optimistic local patch; engine config pushes reconcile (last-write-wins). */
  patchSettings: (patch: Partial<ApplicationSettings>) => void;
  openOnboarding: () => void;
  closeOnboarding: () => void;
}

export type ControlStore = ControlState & ControlActions;

let toastSeq = 0;
const TOAST_AUTO_DISMISS_MS = 4000;

export const useControlStore = create<ControlStore>()((set, get) => ({
  settings: null,
  monitors: [],
  connected: false,
  appVersion: '',
  toasts: [],
  selectedEffectId: null,
  onboardingOpen: false,
  onboardingDecided: false,

  setConnected: (connected) => set({ connected }),

  applyConfig: (payload) => {
    const state = get();
    set({
      settings: payload.settings,
      appVersion: payload.appVersion,
      selectedEffectId: state.selectedEffectId ?? payload.settings.activeEffectId,
      onboardingOpen:
        state.onboardingOpen || (payload.firstRun && !state.onboardingDecided),
    });
  },

  setMonitors: (monitors) => set({ monitors }),

  pushToast: (level, message) => {
    const id = ++toastSeq;
    const sticky = level === 'error';
    set((s) => ({ toasts: [...s.toasts, { id, level, message, sticky }] }));
    if (!sticky) {
      setTimeout(() => get().dismissToast(id), TOAST_AUTO_DISMISS_MS);
    }
  },

  dismissToast: (id) =>
    set((s) => ({ toasts: s.toasts.filter((t) => t.id !== id) })),

  selectEffect: (effectId) => set({ selectedEffectId: effectId }),

  patchSettings: (patch) =>
    set((s) => (s.settings ? { settings: { ...s.settings, ...patch } } : {})),

  openOnboarding: () => set({ onboardingOpen: true }),

  closeOnboarding: () => set({ onboardingOpen: false, onboardingDecided: true }),
}));
