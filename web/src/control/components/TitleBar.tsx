// Custom window chrome (40px): drag region + logo + caption buttons.
import { useEffect, useState } from 'react';
import { getBridge } from '../../shared/bridge';
import { windowCommand } from '../bridgeGlue';
import './TitleBar.css';

export function TitleBar() {
  const [maximized, setMaximized] = useState(false);

  // The engine pushes the native state on every change, so Aero snap, Win+Up and
  // drag-region double-click keep the glyph honest (local state alone desyncs).
  useEffect(
    () => getBridge().on('windowState', (s) => setMaximized(s.state === 'maximized')),
    [],
  );

  const toggleMaximize = () => {
    windowCommand(maximized ? 'restore' : 'maximize');
    setMaximized((v) => !v);
  };

  return (
    <header className="titlebar">
      <div className="titlebar-brand">
        <span className="titlebar-logo" aria-hidden="true">
          <svg width="16" height="16" viewBox="0 0 16 16">
            <rect x="1" y="3" width="9" height="7" rx="1.4" fill="none" stroke="#fff" strokeWidth="1.5" />
            <path d="M12.5 4.5 q3 3.5 0 7" fill="none" stroke="#fff" strokeWidth="1.5" strokeLinecap="round" opacity="0.9" />
            <path d="M5.5 13.5 h0" stroke="#fff" strokeWidth="1.5" strokeLinecap="round" />
          </svg>
        </span>
        <span className="titlebar-wordmark">
          Ambient<span className="grad-text">Fx</span>
        </span>
      </div>

      <div className="titlebar-buttons">
        <button
          type="button"
          className="titlebar-btn"
          aria-label="Minimize"
          onClick={() => windowCommand('minimize')}
        >
          <svg width="10" height="10" viewBox="0 0 10 10">
            <path d="M1 5 H9" stroke="currentColor" strokeWidth="1.2" strokeLinecap="round" />
          </svg>
        </button>
        <button
          type="button"
          className="titlebar-btn"
          aria-label={maximized ? 'Restore' : 'Maximize'}
          onClick={toggleMaximize}
        >
          {maximized ? (
            <svg width="10" height="10" viewBox="0 0 10 10">
              <rect x="1" y="3" width="6" height="6" rx="1" fill="none" stroke="currentColor" strokeWidth="1.1" />
              <path d="M3.5 3 V1.8 a0.8 0.8 0 0 1 0.8-0.8 H8.2 a0.8 0.8 0 0 1 0.8 0.8 V6.2 a0.8 0.8 0 0 1-0.8 0.8 H7" fill="none" stroke="currentColor" strokeWidth="1.1" />
            </svg>
          ) : (
            <svg width="10" height="10" viewBox="0 0 10 10">
              <rect x="1.5" y="1.5" width="7" height="7" rx="1" fill="none" stroke="currentColor" strokeWidth="1.1" />
            </svg>
          )}
        </button>
        <button
          type="button"
          className="titlebar-btn close"
          aria-label="Close"
          onClick={() => windowCommand('close')}
        >
          <svg width="10" height="10" viewBox="0 0 10 10">
            <path d="M1.5 1.5 L8.5 8.5 M8.5 1.5 L1.5 8.5" stroke="currentColor" strokeWidth="1.2" strokeLinecap="round" />
          </svg>
        </button>
      </div>
    </header>
  );
}
