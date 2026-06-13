// ============================================================================
// ClosePrompt (Story 7.3 AC1) — shown when the user closes the window while
// CloseAction is 'ask': keep running in the tray, or quit for real, with a
// "remember my choice" toggle. In-WebView UI (no native MessageBox).
// ============================================================================
import { useEffect, useState } from 'react';
import { useControlStore } from '../store';
import { resolveClosePrompt } from '../bridgeGlue';
import { Button, Toggle } from './controls';
import './ClosePrompt.css';

export function ClosePrompt() {
  const closeClosePrompt = useControlStore((s) => s.closeClosePrompt);
  const [remember, setRemember] = useState(true);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') closeClosePrompt();
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [closeClosePrompt]);

  return (
    <div className="close-overlay" role="dialog" aria-modal="true" aria-label="Close AmbientFx">
      <div className="close-card">
        <h2 className="close-title">Close AmbientFx?</h2>
        <p className="close-sub">
          AmbientFx can keep your ambient effects running from the system tray, or shut down
          completely — nothing left running in the background.
        </p>

        <div className="close-actions">
          <Button variant="accent" size="lg" onClick={() => resolveClosePrompt('minimizeToTray', remember)}>
            Keep running in tray
          </Button>
          <Button variant="ghost" size="lg" onClick={() => resolveClosePrompt('quit', remember)}>
            Quit AmbientFx
          </Button>
        </div>

        <label className="close-remember">
          <Toggle checked={remember} onChange={setRemember} ariaLabel="Remember my choice" />
          <span>Remember my choice (change it any time in Settings)</span>
        </label>

        <button type="button" className="close-cancel" onClick={closeClosePrompt}>
          Cancel
        </button>
      </div>
    </div>
  );
}
