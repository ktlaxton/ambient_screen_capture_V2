// ============================================================================
// PresetsPanel (FR9) — save the current setup under a name, load/delete saved
// presets; the active preset is highlighted.
// ============================================================================
import { useState } from 'react';
import type { ApplicationSettings } from '../../shared/bridge';
import { savePreset, loadPreset, deletePreset } from '../bridgeGlue';
import { TextInput, Button } from './controls';

export function PresetsPanel({ settings }: { settings: ApplicationSettings }) {
  const [name, setName] = useState('');

  const save = () => {
    const trimmed = name.trim();
    if (!trimmed) return;
    savePreset(trimmed);
    setName('');
  };

  return (
    <>
      <div className="preset-form">
        <div style={{ flex: 1 }}>
          <TextInput
            value={name}
            onChange={setName}
            onEnter={save}
            placeholder="Name this setup… e.g. “Night raid”"
            ariaLabel="Preset name"
          />
        </div>
        <Button variant="accent" onClick={save} disabled={name.trim() === ''}>
          Save
        </Button>
      </div>

      {settings.presets.length === 0 ? (
        <div className="empty-state" style={{ padding: '18px 12px' }}>
          <span>No presets yet.</span>
          <span className="hint-faint">Dial in a look you like, then save it for one-click recall (tray too).</span>
        </div>
      ) : (
        <div className="preset-list">
          {settings.presets.map((p) => {
            const active = p.name === settings.activePresetName;
            return (
              <div key={p.name} className={`preset-item${active ? ' active' : ''}`}>
                {active && <span className="preset-active-pip" aria-label="Active preset" />}
                <span className="preset-name">{p.name}</span>
                <Button size="sm" onClick={() => loadPreset(p.name)} disabled={active}>
                  Load
                </Button>
                <Button size="sm" variant="danger" onClick={() => deletePreset(p.name)}>
                  Delete
                </Button>
              </div>
            );
          })}
        </div>
      )}
    </>
  );
}
