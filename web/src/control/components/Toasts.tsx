// ============================================================================
// Toasts (FR12) — bottom-right stack; info/warn auto-dismiss (store handles
// the timer), errors are sticky with a close button. Never a MessageBox.
// ============================================================================
import { useControlStore } from '../store';
import './Toasts.css';

const ICONS: Record<string, string> = {
  info: 'M8 7.2 v4.2 M8 4.4 v0.4',
  warn: 'M8 4.6 v4.6 M8 11.6 v0.4',
  error: 'M5.2 5.2 l5.6 5.6 M10.8 5.2 l-5.6 5.6',
};

export function Toasts() {
  const toasts = useControlStore((s) => s.toasts);
  const dismissToast = useControlStore((s) => s.dismissToast);

  if (toasts.length === 0) return null;

  return (
    <div className="toasts" role="status" aria-live="polite">
      {toasts.map((t) => (
        <div key={t.id} className={`toast ${t.level}`}>
          <svg className="toast-icon" width="16" height="16" viewBox="0 0 16 16" aria-hidden="true">
            <circle cx="8" cy="8" r="7" fill="none" stroke="currentColor" strokeWidth="1.3" opacity="0.5" />
            <path d={ICONS[t.level] ?? ICONS.info} fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" />
          </svg>
          <span className="toast-msg">{t.message}</span>
          <button
            type="button"
            className="toast-close"
            aria-label="Dismiss"
            onClick={() => dismissToast(t.id)}
          >
            <svg width="9" height="9" viewBox="0 0 10 10" aria-hidden="true">
              <path d="M1.5 1.5 L8.5 8.5 M8.5 1.5 L1.5 8.5" stroke="currentColor" strokeWidth="1.3" strokeLinecap="round" />
            </svg>
          </button>
        </div>
      ))}
    </div>
  );
}
