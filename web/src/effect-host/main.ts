// effect-host/main.ts — entry for effects.html: the runtime inside every
// fullscreen effect window. Plain TS (no React): creates the canvas, connects
// the bridge (auto-simulator outside WebView2), and wires EffectHost +
// RenderLoop. The C# engine appends ?monitorId=... when it spawns the window.
//
// URL params:
//   ?monitorId=...  engine-assigned monitor id (absent in plain-browser dev —
//                   the host then adopts the first windowConfig it receives)
//   ?fps=1          show the debug FPS overlay (measured / cap)

import { getBridge } from '../shared/bridge';
import { EffectHost } from './host';
import { FpsOverlay } from './fps-overlay';
import { RenderLoop } from './loop';

const RESIZE_DEBOUNCE_MS = 50;

function boot(): void {
  const root = document.getElementById('root');
  if (!root) {
    console.error('[effect-host] #root not found — effects.html is malformed');
    return;
  }

  const params = new URLSearchParams(location.search);
  const monitorId = params.get('monitorId');

  const canvas = document.createElement('canvas');
  root.appendChild(canvas);

  const bridge = getBridge();
  const host = new EffectHost(canvas, root, monitorId);
  const loop = new RenderLoop({
    render: (timeMs, dtMs) => host.renderSafe(timeMs, dtMs),
    isActive: () => host.hasInstance(),
  });
  host.onMaxFpsChange = (fps) => loop.setMaxFps(fps);
  host.onFatalError = () => loop.stop();
  host.onReportError = (source, message) => bridge.send('reportError', { source, message });

  const unsubscribes = [
    bridge.on('windowConfig', (cfg) => host.handleWindowConfig(cfg)),
    bridge.on('config', (cfg) => host.handleConfig(cfg)),
    bridge.on('frame', (frame) => host.handleFrame(frame)),
  ];

  // Resize: trailing 50ms debounce, CSS pixels of #root.
  let resizeTimer: number | null = null;
  const observer = new ResizeObserver(() => {
    if (resizeTimer !== null) window.clearTimeout(resizeTimer);
    resizeTimer = window.setTimeout(() => {
      resizeTimer = null;
      host.resize(root.clientWidth, root.clientHeight);
    }, RESIZE_DEBOUNCE_MS);
  });
  observer.observe(root);

  const overlay = params.get('fps') === '1' ? new FpsOverlay(document.body, loop) : null;

  loop.start(); // idles until the first windowConfig creates an instance
  bridge.send('requestState', {});

  window.addEventListener('pagehide', () => {
    for (const off of unsubscribes) off();
    observer.disconnect();
    if (resizeTimer !== null) window.clearTimeout(resizeTimer);
    overlay?.dispose();
    loop.dispose();
    host.dispose();
    bridge.dispose();
  });
}

boot();
