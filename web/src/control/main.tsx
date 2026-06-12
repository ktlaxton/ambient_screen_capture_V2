// AmbientFx control UI entry point (loaded by /control.html).
import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { initBridgeGlue } from './bridgeGlue';
import App from './App';
import './theme.css';

initBridgeGlue();

const rootEl = document.getElementById('root');
if (!rootEl) throw new Error('control.html is missing <div id="root">');

createRoot(rootEl).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
