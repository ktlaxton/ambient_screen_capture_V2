// ============================================================================
// EffectParams — auto-generated controls from the selected module's ParamDefs
// (range/select/toggle), values = stored params merged over defaults.
// Sends are debounced in bridgeGlue; local state updates optimistically.
// ============================================================================
import { useMemo } from 'react';
import { effects, effectsById } from '../../effects/registry';
import type { ParamDef } from '../../effects/types';
import { useControlStore } from '../store';
import { resolvedEffectParams, setEffectParam, resetEffectParams } from '../bridgeGlue';
import { Slider, Select, Toggle, Button } from './controls';

function formatRange(def: ParamDef, value: number): string {
  const max = def.max ?? 1;
  const min = def.min ?? 0;
  if (max <= 1 && min >= -1) return `${Math.round(value * 100)}%`;
  const step = def.step ?? 1;
  return step < 1 ? value.toFixed(2) : String(Math.round(value));
}

function ParamRow({
  effectId,
  def,
  value,
}: {
  effectId: string;
  def: ParamDef;
  value: number | string | boolean;
}) {
  if (def.type === 'range') {
    const num = typeof value === 'number' ? value : Number(def.default);
    return (
      <div className="form-row">
        <div className="row-control" style={{ justifyContent: 'stretch' }}>
          <Slider
            label={def.label}
            value={num}
            min={def.min ?? 0}
            max={def.max ?? 1}
            step={def.step ?? 0.01}
            format={(v) => formatRange(def, v)}
            onChange={(v) => setEffectParam(effectId, def.key, v)}
          />
        </div>
      </div>
    );
  }

  if (def.type === 'select') {
    const str = typeof value === 'string' ? value : String(def.default);
    return (
      <div className="form-row">
        <div className="row-label">
          <span className="name">{def.label}</span>
        </div>
        <div className="row-control">
          <Select
            value={str}
            options={def.options ?? []}
            onChange={(v) => setEffectParam(effectId, def.key, v)}
            ariaLabel={def.label}
          />
        </div>
      </div>
    );
  }

  const bool = typeof value === 'boolean' ? value : Boolean(def.default);
  return (
    <div className="form-row">
      <div className="row-label">
        <span className="name">{def.label}</span>
      </div>
      <div className="row-control">
        <Toggle checked={bool} onChange={(v) => setEffectParam(effectId, def.key, v)} ariaLabel={def.label} />
      </div>
    </div>
  );
}

export function EffectParams() {
  const selectedEffectId = useControlStore((s) => s.selectedEffectId);
  const selectEffect = useControlStore((s) => s.selectEffect);
  // Re-render on settings changes so slider values track engine reconciles.
  const settingsVersion = useControlStore((s) => s.settings?.effectParamsById);

  const module = selectedEffectId ? effectsById[selectedEffectId] : undefined;

  const effectOptions = useMemo(() => effects.map((e) => ({ value: e.id, label: e.name })), []);

  const params = useMemo(
    () => (module ? resolvedEffectParams(module.id) : {}),
    [module, settingsVersion],
  );

  return (
    <>
      <div className="card-title">
        <span className="dot" />
        Effect parameters
        <span className="spacer" />
        <Select
          size="sm"
          value={module?.id ?? ''}
          options={effectOptions}
          onChange={selectEffect}
          ariaLabel="Effect to tune"
        />
      </div>
      {!module ? (
        <p className="hint params-empty">Pick an effect in the gallery to tune it.</p>
      ) : module.params.length === 0 ? (
        <p className="hint params-empty">“{module.name}” has no adjustable parameters.</p>
      ) : (
        <>
          {module.params.map((def) => (
            <ParamRow key={def.key} effectId={module.id} def={def} value={params[def.key] ?? def.default} />
          ))}
          <div className="form-row">
            <span className="hint-faint">Changes apply live</span>
            <Button size="sm" onClick={() => resetEffectParams(module.id)}>
              Reset to defaults
            </Button>
          </div>
        </>
      )}
    </>
  );
}
