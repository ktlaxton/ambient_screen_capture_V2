// ============================================================================
// Onboarding (FR11) — full-window first-run wizard:
//   1. welcome  2. pick source + targets (embedded monitor map)
//   3. pick a starting effect (mini gallery) -> completeOnboarding + power CTA.
// Skippable at any point (skip also completes first-run so it never nags).
// ============================================================================
import { useEffect, useState } from 'react';
import { useControlStore } from '../store';
import { completeOnboarding, setEnabled, setSourceMonitor } from '../bridgeGlue';
import { MonitorMap } from './MonitorMap';
import { EffectGallery } from './EffectGallery';
import { Button } from './controls';
import './Onboarding.css';

const STEPS = 3;

export function Onboarding() {
  const closeOnboarding = useControlStore((s) => s.closeOnboarding);
  const settings = useControlStore((s) => s.settings);
  const monitors = useControlStore((s) => s.monitors);
  const [step, setStep] = useState(0);

  // Default the capture source to the primary display so "Turn it on" can never
  // fire source-less (the engine would instantly revert it to off with a warning).
  useEffect(() => {
    if (!settings || settings.sourceMonitorId || monitors.length === 0) return;
    const primary = monitors.find((m) => m.isPrimary) ?? monitors[0];
    setSourceMonitor(primary.id);
  }, [settings, monitors]);

  const finish = (turnOn: boolean) => {
    completeOnboarding();
    if (turnOn) setEnabled(true);
    closeOnboarding();
  };

  const skip = () => finish(false);

  const sourcePicked = (settings?.sourceMonitorId ?? '') !== '';
  const targetsPicked = (settings?.targetMonitorIds.length ?? 0) > 0;

  return (
    <div className="onboarding" role="dialog" aria-modal="true" aria-label="Welcome to AmbientFx">
      <div className="ob-panel">
        <button type="button" className="ob-skip" onClick={skip}>
          Skip setup
        </button>

        {step === 0 && (
          <div className="ob-step" key="welcome">
            <div className="ob-logo" aria-hidden="true" />
            <h1 className="ob-title">
              Welcome to Ambient<span className="grad-text">Fx</span>
            </h1>
            <p className="ob-lede">
              Your idle monitors become reactive ambient lighting — driven by the colors on your main
              screen and the sound of your game.
            </p>
            <ul className="ob-points">
              <li>
                <i className="pt c" />
                Real edge-color capture from your primary display
              </li>
              <li>
                <i className="pt v" />
                Audio-reactive WebGL effects on every target monitor
              </li>
              <li>
                <i className="pt c" />
                Lives in the tray, off your taskbar, out of your way
              </li>
            </ul>
          </div>
        )}

        {step === 1 && (
          <div className="ob-step" key="monitors">
            <h2 className="ob-heading">Choose your displays</h2>
            <p className="ob-sub">
              The <span className="ob-cyan">source</span> is captured; <span className="ob-violet">targets</span>{' '}
              glow. Click a display to toggle it as a target — hover and pick “Set source” to change the
              capture screen.
            </p>
            <div className="ob-map">
              <MonitorMap compact />
            </div>
          </div>
        )}

        {step === 2 && (
          <div className="ob-step" key="effect">
            <h2 className="ob-heading">Pick a starting effect</h2>
            <p className="ob-sub">Live previews — you can switch or tune them any time.</p>
            <div className="ob-gallery">
              <EffectGallery compact />
            </div>
          </div>
        )}

        <div className="ob-footer">
          <div className="ob-dots" aria-label={`Step ${step + 1} of ${STEPS}`}>
            {Array.from({ length: STEPS }, (_, i) => (
              <i key={i} className={i === step ? 'on' : ''} />
            ))}
          </div>
          <div className="ob-nav">
            {step > 0 && (
              <Button onClick={() => setStep((s) => Math.max(0, s - 1))}>Back</Button>
            )}
            {step < STEPS - 1 ? (
              <Button variant="accent" size="lg" onClick={() => setStep((s) => s + 1)}>
                {step === 0 ? 'Get started' : 'Next'}
              </Button>
            ) : (
              <>
                <Button onClick={() => finish(false)}>Finish</Button>
                <Button
                  variant="accent"
                  size="lg"
                  onClick={() => finish(true)}
                  disabled={!sourcePicked || !targetsPicked}
                >
                  Turn it on
                </Button>
              </>
            )}
          </div>
        </div>
      </div>
    </div>
  );
}
