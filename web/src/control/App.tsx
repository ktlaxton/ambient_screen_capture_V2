// ============================================================================
// App — single-window dashboard shell: title bar, power header, section grid,
// onboarding overlay and toast stack. Heavy live visuals live in components
// that read the frame feed directly (never through React state).
// ============================================================================
import type { ApplicationSettings } from '../shared/bridge';
import { useControlStore } from './store';
import { setEnabled } from './bridgeGlue';
import { AmbientBackground } from './components/AmbientBackground';
import { TitleBar } from './components/TitleBar';
import { Toggle } from './components/controls';
import { MonitorMap } from './components/MonitorMap';
import { SignalPanel } from './components/SignalPanel';
import { EffectGallery } from './components/EffectGallery';
import { EffectParams } from './components/EffectParams';
import { GlobalControls } from './components/GlobalControls';
import { PresetsPanel } from './components/PresetsPanel';
import { SettingsPanel } from './components/SettingsPanel';
import { Onboarding } from './components/Onboarding';
import { Toasts } from './components/Toasts';
import './App.css';

function PowerHeader({ settings }: { settings: ApplicationSettings }) {
  const connected = useControlStore((s) => s.connected);
  return (
    <header className="power-header">
      <div className="power-left">
        <Toggle large checked={settings.isEnabled} onChange={setEnabled} ariaLabel="Master power" />
        <div className="power-text">
          <span className={`power-title${settings.isEnabled ? ' on' : ''}`}>
            {settings.isEnabled ? 'Effects live' : 'Effects off'}
          </span>
          <span className="power-sub">
            {settings.isEnabled
              ? 'Secondary monitors are reacting to your screen and audio'
              : 'Your monitors are released for normal use'}
          </span>
        </div>
      </div>
      <div className={`conn-chip${connected ? ' hosted' : ''}`}>
        <i className="conn-dot" />
        {connected ? 'Engine connected' : 'Simulator'}
      </div>
    </header>
  );
}

function BootScreen() {
  return (
    <div className="boot-screen">
      <div className="boot-logo" />
      <div className="boot-shimmer" aria-label="Loading">
        <i />
        <i />
        <i />
      </div>
    </div>
  );
}

export default function App() {
  const settings = useControlStore((s) => s.settings);
  const onboardingOpen = useControlStore((s) => s.onboardingOpen);

  return (
    <>
      <AmbientBackground />
      <TitleBar />
      {settings === null ? (
        <BootScreen />
      ) : (
        <div className={`app-body${settings.isEnabled ? '' : ' app-off'}`}>
          <PowerHeader settings={settings} />
          <main className="dashboard">
            <section className="card col-7">
              <div className="card-title">
                <span className="dot" />
                Displays
                <span className="spacer" />
                <span className="hint-faint">click a display to toggle it as a target</span>
              </div>
              <MonitorMap />
            </section>
            <section className="card col-5">
              <div className="card-title">
                <span className="dot" />
                Live signal
                <span className="spacer" />
                <span className="live-pip" aria-hidden />
              </div>
              <SignalPanel />
            </section>
            <section className="card col-12">
              <div className="card-title">
                <span className="dot" />
                Effects
                <span className="spacer" />
                <span className="hint-faint">live previews</span>
              </div>
              <EffectGallery />
            </section>
            <section className="card col-6">
              <EffectParams />
            </section>
            <section className="card col-6">
              <div className="card-title">
                <span className="dot" />
                Global controls
              </div>
              <GlobalControls settings={settings} />
            </section>
            <section className="card col-6">
              <div className="card-title">
                <span className="dot" />
                Presets
              </div>
              <PresetsPanel settings={settings} />
            </section>
            <section className="card col-6">
              <div className="card-title">
                <span className="dot" />
                Settings
              </div>
              <SettingsPanel settings={settings} />
            </section>
          </main>
        </div>
      )}
      {onboardingOpen && <Onboarding />}
      <Toasts />
    </>
  );
}
