// ============================================================================
// DevicesPanel (Stories 8.1 + 8.2) — ambient RGB peripherals: master toggle,
// global peripheral brightness, and per-device placement cards (anchor, flip,
// brightness, enable) with a which-edge indicator. Placement applies live.
// ============================================================================
import type {
  AmbientDeviceInfo,
  ApplicationSettings,
  DeviceAnchor,
  DeviceConnectionState,
  RgbProviderState,
} from '../../shared/bridge';
import { useControlStore } from '../store';
import {
  resolvedDevicePlacement,
  setAmbientDevicesEnabled,
  setAudioReactiveDevices,
  setAudioReactiveDepth,
  setDevicePlacement,
  setPeripheralBrightness,
  setRgbProviderEnabled,
} from '../bridgeGlue';
import { PURCHASE_URL, usePremium } from '../premium';
import { Select, Slider, Toggle } from './controls';
import './DevicesPanel.css';

const pct = (v: number) => `${Math.round(v * 100)}%`;

const STATE_LABEL: Record<DeviceConnectionState, string> = {
  disabled: 'Off',
  connecting: 'Connecting…',
  connected: 'Connected',
  icueNotFound: 'iCUE not found',
  refused: 'Control refused',
  noDevices: 'No devices',
  error: 'Error',
};

/** Actionable guidance for every not-connected state (AC2/AC6). */
const STATE_HINT: Partial<Record<DeviceConnectionState, string>> = {
  icueNotFound:
    'Corsair iCUE must be installed and running (version 4.31 or newer). Start iCUE, then toggle peripherals off and on to retry.',
  refused:
    'iCUE refused the connection. In iCUE: Settings → enable software/SDK integrations (third-party control), then toggle peripherals off and on.',
  noDevices: 'Connected to iCUE, but it reported no RGB devices.',
  error: 'Something went wrong talking to the RGB hardware — see the log for details.',
};

const ANCHOR_OPTIONS: { value: DeviceAnchor; label: string }[] = [
  { value: 'auto', label: 'Auto' },
  { value: 'left', label: 'Left of screen' },
  { value: 'right', label: 'Right of screen' },
  { value: 'above', label: 'Above screen' },
  { value: 'below', label: 'Below screen' },
  { value: 'behind', label: 'Behind screen' },
  { value: 'surround', label: 'Surround (ring)' },
];

/** Which screen edge feeds the device — the per-device mapping indicator (AC3). */
const ANCHOR_FEED: Record<DeviceAnchor, string> = {
  auto: 'nearest edge per LED',
  left: '◀ left edge',
  right: 'right edge ▶',
  above: '▲ top edge',
  below: '▼ bottom edge',
  behind: 'nearest edge per LED',
  surround: '↻ all edges by angle',
};

/** Flip only matters where there is a direction to reverse. */
const FLIPPABLE: ReadonlySet<DeviceAnchor> = new Set(['left', 'right', 'above', 'below', 'surround']);

/** Vendor catalog (Story 8.3) — mirrors the engine's provider registry keys. */
const RGB_PROVIDERS: { key: string; name: string; sub: string }[] = [
  { key: 'corsair', name: 'Corsair iCUE', sub: 'needs iCUE running + SDK enabled' },
  { key: 'logitech', name: 'Logitech', sub: 'needs the Logitech LED SDK / G HUB' },
  { key: 'razer', name: 'Razer Chroma', sub: 'needs Synapse with Chroma' },
  { key: 'asus', name: 'ASUS Aura', sub: 'needs Aura / Armoury Crate' },
  { key: 'msi', name: 'MSI Mystic Light', sub: 'needs the Mystic Light SDK' },
  { key: 'steelseries', name: 'SteelSeries', sub: 'needs SteelSeries GG' },
  { key: 'wooting', name: 'Wooting', sub: 'needs the Wooting RGB SDK' },
];

const PROVIDER_STATE_LABEL: Record<RgbProviderState, string> = {
  connected: 'connected',
  unavailable: 'not found',
  refused: 'refused',
  error: 'error',
};

function DeviceCard({ device, settings }: { device: AmbientDeviceInfo; settings: ApplicationSettings }) {
  // Read through settings so the card re-renders on optimistic patches and config pushes.
  void settings.devicePlacements;
  const placement = resolvedDevicePlacement(device.id);

  return (
    <li className={`device-item${placement.enabled ? '' : ' device-off'}`}>
      <div className="device-head">
        <div className="device-title">
          <span className="device-name">{device.name}</span>
          <span className="device-meta">
            {device.type} · {device.ledCount} LED{device.ledCount === 1 ? '' : 's'} ·{' '}
            {ANCHOR_FEED[placement.anchor]}
          </span>
        </div>
        <Toggle
          checked={placement.enabled}
          onChange={(enabled) => setDevicePlacement(device.id, { enabled })}
          ariaLabel={`Include ${device.name}`}
        />
      </div>
      <div className="device-controls">
        <Select
          value={placement.anchor}
          options={ANCHOR_OPTIONS}
          onChange={(v) => setDevicePlacement(device.id, { anchor: v as DeviceAnchor })}
          ariaLabel={`Placement for ${device.name}`}
        />
        {FLIPPABLE.has(placement.anchor) && (
          <label className="device-flip">
            <Toggle
              checked={placement.flip}
              onChange={(flip) => setDevicePlacement(device.id, { flip })}
              ariaLabel={`Reverse direction for ${device.name}`}
            />
            <span>Reverse</span>
          </label>
        )}
        <div className="device-brightness">
          <Slider
            label="Brightness"
            value={placement.brightness}
            min={0}
            max={1}
            step={0.01}
            format={pct}
            onChange={(brightness) => setDevicePlacement(device.id, { brightness })}
          />
        </div>
      </div>
    </li>
  );
}

export function DevicesPanel({ settings }: { settings: ApplicationSettings }) {
  const devices = useControlStore((s) => s.devices);
  const premium = usePremium();
  const state = devices.connectionState;
  const hint = STATE_HINT[state];

  // RGB peripherals are a Premium feature (Epic 9). Free users see the pitch, not the controls.
  if (!premium) {
    return (
      <div className="devices-locked">
        <span className="device-state-chip state-icueNotFound">
          <i className="device-state-dot" />
          Premium
        </span>
        <p className="device-hint">
          Spill the ambient glow onto your keyboard, mouse, light strips and fans.
          RGB peripherals are part of AmbientFx Premium.
        </p>
        <a className="license-upgrade" href={PURCHASE_URL} target="_blank" rel="noreferrer noopener">
          Upgrade to Premium
        </a>
      </div>
    );
  }

  return (
    <>
      <div className="form-row">
        <div className="row-label">
          <span className="name">Light up my peripherals</span>
          <span className="sub">
            keyboard, mouse and strips glow with the matching screen edge
            {settings.isEnabled ? '' : ' — runs while effects are live'}
          </span>
        </div>
        <div className="row-control devices-toggle-control">
          <span className={`device-state-chip state-${state}`}>
            <i className="device-state-dot" />
            {STATE_LABEL[state]}
          </span>
          <Toggle
            checked={settings.ambientDevicesEnabled}
            onChange={setAmbientDevicesEnabled}
            ariaLabel="Light up my peripherals"
          />
        </div>
      </div>

      <div className="form-row">
        <div className="row-control" style={{ justifyContent: 'stretch' }}>
          <Slider
            label="Peripheral brightness"
            value={settings.peripheralBrightness}
            min={0}
            max={1}
            step={0.01}
            format={pct}
            onChange={setPeripheralBrightness}
          />
        </div>
      </div>

      <div className="form-row">
        <div className="row-label">
          <span className="name">Pulse with audio</span>
          <span className="sub">peripherals dim and surge with what you hear</span>
        </div>
        <div className="row-control">
          <Toggle
            checked={settings.audioReactiveDevices}
            onChange={setAudioReactiveDevices}
            ariaLabel="Pulse with audio"
          />
        </div>
      </div>
      {settings.audioReactiveDevices && (
        <div className="form-row">
          <div className="row-control" style={{ justifyContent: 'stretch' }}>
            <Slider
              label="Audio depth"
              value={settings.audioReactiveDepth}
              min={0}
              max={1}
              step={0.01}
              format={pct}
              onChange={setAudioReactiveDepth}
            />
          </div>
        </div>
      )}

      <div className="provider-section">
        <span className="provider-section-title">Vendors</span>
        <ul className="provider-list" aria-label="RGB vendors">
          {RGB_PROVIDERS.map(({ key, name, sub }) => {
            const enabled = settings.rgbProviders.includes(key);
            const status = devices.providers.find((p) => p.key === key);
            return (
              <li className="provider-item" key={key}>
                <div className="provider-title">
                  <span className="provider-name">
                    {name}
                    {status && (
                      <i className={`provider-state provider-${status.state}`}>
                        {PROVIDER_STATE_LABEL[status.state]}
                        {status.state === 'connected' ? ` · ${status.deviceCount}` : ''}
                      </i>
                    )}
                  </span>
                  <span className="provider-sub">{sub}</span>
                </div>
                <Toggle
                  checked={enabled}
                  onChange={(on) => setRgbProviderEnabled(key, on)}
                  ariaLabel={`Use ${name}`}
                />
              </li>
            );
          })}
        </ul>
      </div>

      {state === 'connected' && devices.devices.length > 0 ? (
        <ul className="device-list" aria-label="Discovered RGB devices">
          {devices.devices.map((d) => (
            <DeviceCard key={d.id} device={d} settings={settings} />
          ))}
        </ul>
      ) : (
        hint && <p className="device-hint">{hint}</p>
      )}
    </>
  );
}
