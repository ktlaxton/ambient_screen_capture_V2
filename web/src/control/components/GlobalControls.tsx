// ============================================================================
// GlobalControls — intensity / smoothing / brightness / audio sensitivity
// sliders (0-100% display) + max-FPS segmented control. Optimistic + debounced
// via bridgeGlue.setGlobal.
// ============================================================================
import type { ApplicationSettings } from '../../shared/bridge';
import { setGlobal } from '../bridgeGlue';
import type { GlobalField } from '../bridgeGlue';
import { Slider, Segmented } from './controls';

const pct = (v: number) => `${Math.round(v * 100)}%`;

const SLIDERS: { field: GlobalField; label: string; value: (s: ApplicationSettings) => number }[] = [
  { field: 'intensity', label: 'Intensity', value: (s) => s.globalIntensity },
  { field: 'smoothing', label: 'Smoothing', value: (s) => s.smoothing },
  { field: 'brightness', label: 'Brightness', value: (s) => s.brightness },
  { field: 'audioSensitivity', label: 'Audio sensitivity', value: (s) => s.audioSensitivity },
];

export function GlobalControls({ settings }: { settings: ApplicationSettings }) {
  return (
    <>
      {SLIDERS.map(({ field, label, value }) => (
        <div className="form-row" key={field}>
          <div className="row-control" style={{ justifyContent: 'stretch' }}>
            <Slider
              label={label}
              value={value(settings)}
              min={0}
              max={1}
              step={0.01}
              format={pct}
              onChange={(v) => setGlobal(field, v)}
            />
          </div>
        </div>
      ))}
      <div className="form-row">
        <div className="row-label">
          <span className="name">Max frame rate</span>
          <span className="sub">caps GPU work while gaming</span>
        </div>
        <div className="row-control">
          <Segmented
            ariaLabel="Max frame rate"
            value={settings.maxFps}
            options={[
              { value: 30, label: '30' },
              { value: 60, label: '60' },
              { value: 120, label: '120' },
            ]}
            onChange={(v) => setGlobal('maxFps', v)}
          />
        </div>
      </div>
    </>
  );
}
