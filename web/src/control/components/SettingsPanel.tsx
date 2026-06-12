// ============================================================================
// SettingsPanel (FR10) — autostart toggle + global hotkey capture fields.
// Hotkey capture: arm the field, press a combo (must include Ctrl/Alt/Win),
// Esc clears the binding.
// ============================================================================
import { useState } from 'react';
import type { ApplicationSettings } from '../../shared/bridge';
import { useControlStore } from '../store';
import { setAutostart, setHotkey } from '../bridgeGlue';
import { Toggle } from './controls';
import './SettingsPanel.css';

const HOTKEY_ACTIONS: { action: string; label: string; sub: string }[] = [
  { action: 'toggleEnabled', label: 'Toggle effects', sub: 'master on / off from anywhere' },
  { action: 'openSettings', label: 'Open settings', sub: 'bring up this window' },
  { action: 'nextPreset', label: 'Next preset', sub: 'cycle saved presets' },
];

const MODIFIER_KEYS = new Set(['Control', 'Alt', 'Shift', 'Meta', 'AltGraph']);

function normalizeKey(key: string, code: string): string {
  if (key === ' ') return 'Space';
  if (key.startsWith('Arrow')) return key.slice(5);
  if (key.length === 1) {
    // Use the physical key for letters/digits so Shift'ed combos stay stable.
    if (/^Key[A-Z]$/.test(code)) return code.slice(3);
    if (/^Digit[0-9]$/.test(code)) return code.slice(5);
    return key.toUpperCase();
  }
  return key;
}

function HotkeyField({ action }: { action: string }) {
  const binding = useControlStore((s) => s.settings?.hotkeys[action] ?? '');
  const [arming, setArming] = useState(false);

  const onKeyDown = (e: React.KeyboardEvent<HTMLButtonElement>) => {
    if (!arming) return;
    e.preventDefault();
    e.stopPropagation();

    if (e.key === 'Escape') {
      setHotkey(action, '');
      setArming(false);
      return;
    }
    if (MODIFIER_KEYS.has(e.key)) return; // keep listening for the main key
    if (!(e.ctrlKey || e.altKey || e.metaKey)) return; // require a real modifier

    const parts: string[] = [];
    if (e.ctrlKey) parts.push('Ctrl');
    if (e.altKey) parts.push('Alt');
    if (e.shiftKey) parts.push('Shift');
    if (e.metaKey) parts.push('Win');
    parts.push(normalizeKey(e.key, e.code));
    setHotkey(action, parts.join('+'));
    setArming(false);
  };

  return (
    <button
      type="button"
      className={`hotkey-field${arming ? ' arming' : ''}${binding ? '' : ' unset'}`}
      onClick={() => setArming(true)}
      onBlur={() => setArming(false)}
      onKeyDown={onKeyDown}
      aria-label={`Hotkey for ${action}`}
    >
      {arming ? (
        <span className="hotkey-hint">Press a combo… Esc clears</span>
      ) : binding ? (
        binding.split('+').map((part, i) => (
          <span className="key-cap" key={`${part}-${i}`}>
            {part}
          </span>
        ))
      ) : (
        'Not set'
      )}
    </button>
  );
}

export function SettingsPanel({ settings }: { settings: ApplicationSettings }) {
  const appVersion = useControlStore((s) => s.appVersion);
  const connected = useControlStore((s) => s.connected);

  return (
    <>
      <div className="form-row">
        <div className="row-label">
          <span className="name">Start with Windows</span>
          <span className="sub">launch minimized to the tray at login</span>
        </div>
        <div className="row-control">
          <Toggle checked={settings.autostart} onChange={setAutostart} ariaLabel="Start with Windows" />
        </div>
      </div>

      {HOTKEY_ACTIONS.map(({ action, label, sub }) => (
        <div className="form-row" key={action}>
          <div className="row-label">
            <span className="name">{label}</span>
            <span className="sub">{sub}</span>
          </div>
          <div className="row-control">
            <HotkeyField action={action} />
          </div>
        </div>
      ))}

      <div className="settings-footer">
        AmbientFx {appVersion || 'dev'} · {connected ? 'native engine' : 'browser simulator'}
      </div>
    </>
  );
}
