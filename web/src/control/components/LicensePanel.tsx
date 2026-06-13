// ============================================================================
// LicensePanel (Epic 9 / Story 9.2) — shows the current edition and either the
// Premium receipt (licensed-to + remove) or the free-tier upsell + key entry.
// The engine verifies the key offline and pushes back the real entitlement, so
// the input is intentionally non-optimistic.
// ============================================================================
import { useState } from 'react';
import { useControlStore } from '../store';
import { setLicenseKey } from '../bridgeGlue';
import { PURCHASE_URL } from '../premium';
import { Button, TextInput } from './controls';
import './LicensePanel.css';

const PREMIUM_PERKS = [
  'Glow every monitor (free lights one)',
  'The full effect library',
  'Per-monitor effects',
  'RGB peripherals: keyboard, mouse, strips & fans',
];

export function LicensePanel() {
  const license = useControlStore((s) => s.license);
  const storedKey = useControlStore((s) => s.settings?.licenseKey ?? '');
  const [draft, setDraft] = useState('');

  if (license.isPremium) {
    return (
      <div className="license-panel is-premium">
        <div className="license-head">
          <span className="license-badge premium">★ Premium</span>
          <div className="license-headtext">
            <span className="license-title">AmbientFx Premium is active</span>
            {license.licensedTo && (
              <span className="license-sub">Licensed to {license.licensedTo}</span>
            )}
            {license.expires && (
              <span className="license-sub">Renews / expires {license.expires}</span>
            )}
          </div>
        </div>
        <Button
          variant="ghost"
          size="sm"
          onClick={() => setLicenseKey('')}
          title="Remove this license from this machine"
        >
          Remove license
        </Button>
      </div>
    );
  }

  return (
    <div className="license-panel">
      <div className="license-head">
        <span className="license-badge free">Free</span>
        <div className="license-headtext">
          <span className="license-title">Unlock AmbientFx Premium</span>
          <span className="license-sub">A one-time purchase — your whole desk reacts.</span>
        </div>
      </div>

      <ul className="license-perks">
        {PREMIUM_PERKS.map((perk) => (
          <li key={perk}>{perk}</li>
        ))}
      </ul>

      <div className="license-actions">
        {/* target=_blank → the engine routes new windows to the OS browser (ControlWindow). */}
        <a className="license-upgrade" href={PURCHASE_URL} target="_blank" rel="noreferrer noopener">
          Upgrade to Premium
        </a>
      </div>

      <div className="license-activate">
        <span className="license-label">Already bought? Enter your key</span>
        <div className="license-entry">
          <TextInput
            value={draft}
            onChange={setDraft}
            placeholder="AFX1.…"
            ariaLabel="License key"
            onEnter={() => draft.trim() && setLicenseKey(draft)}
          />
          <Button
            variant="accent"
            size="sm"
            disabled={!draft.trim()}
            onClick={() => setLicenseKey(draft)}
            title="Activate this license key"
          >
            Activate
          </Button>
        </div>
        {storedKey && (
          <span className="license-warn">
            The saved license key isn't valid here — re-enter it or upgrade.
          </span>
        )}
      </div>
    </div>
  );
}
