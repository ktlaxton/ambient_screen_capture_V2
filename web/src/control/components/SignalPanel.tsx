// ============================================================================
// SignalPanel — proof the capture is REAL (AC1/AC2): a screen-shaped rectangle
// ringed by the live edge-zone colors, the dominant swatch + hex, an FFT bar
// strip and an intensity meter. Pure canvas, drawn in its own 30fps rAF loop
// straight from the frame feed (no React state involved).
// ============================================================================
import { useEffect, useRef } from 'react';
import type { FramePayload } from '../../shared/bridge';
import { getLatestFrame, isLiveSignal, rgbToHex } from '../frameFeed';
import './SignalPanel.css';

const REDRAW_MS = 33;
const FONT = '600 9px "Segoe UI Variable Display", "Segoe UI", system-ui, sans-serif';

function drawScreenRing(ctx: CanvasRenderingContext2D, frame: FramePayload, x: number, y: number, w: number, h: number) {
  // Faint dominant-tinted interior.
  const [dr, dg, db] = frame.dominant;
  ctx.fillStyle = `rgba(${dr}, ${dg}, ${db}, 0.09)`;
  ctx.strokeStyle = 'rgba(255, 255, 255, 0.08)';
  ctx.lineWidth = 1;
  ctx.beginPath();
  ctx.roundRect(x, y, w, h, 6);
  ctx.fill();
  ctx.stroke();

  const seg = 3.5;
  const zone = (rgb: [number, number, number], zx: number, zy: number, zw: number, zh: number) => {
    const css = `rgb(${rgb[0]}, ${rgb[1]}, ${rgb[2]})`;
    ctx.fillStyle = css;
    ctx.shadowColor = css;
    ctx.shadowBlur = 10;
    ctx.fillRect(zx, zy, zw, zh);
  };

  const { top, bottom, left, right } = frame.edges;
  for (let i = 0; i < top.length; i++) {
    const zw = w / top.length;
    zone(top[i], x + i * zw + 1, y - seg - 2, zw - 2, seg);
  }
  for (let i = 0; i < bottom.length; i++) {
    const zw = w / bottom.length;
    zone(bottom[i], x + i * zw + 1, y + h + 2, zw - 2, seg);
  }
  for (let i = 0; i < left.length; i++) {
    const zh = h / left.length;
    zone(left[i], x - seg - 2, y + i * zh + 1, seg, zh - 2);
  }
  for (let i = 0; i < right.length; i++) {
    const zh = h / right.length;
    zone(right[i], x + w + 2, y + i * zh + 1, seg, zh - 2);
  }
  ctx.shadowBlur = 0;

  ctx.fillStyle = 'rgba(139, 147, 167, 0.55)';
  ctx.font = FONT;
  ctx.textAlign = 'center';
  // This panel is the proof the capture is REAL (AC1) — label honestly when the
  // feed has fallen back to the synthesized demo signal (effects off).
  ctx.fillText(isLiveSignal() ? 'SOURCE FEED' : 'DEMO SIGNAL', x + w / 2, y + h / 2 + 3);
}

function lerpChannel(a: number, b: number, t: number): number {
  return Math.round(a + (b - a) * t);
}

/** Brand gradient color for FFT bar i of n (cyan -> violet). */
function barColor(i: number, n: number): string {
  const t = n <= 1 ? 0 : i / (n - 1);
  const r = lerpChannel(34, 139, t);
  const g = lerpChannel(211, 92, t);
  const b = lerpChannel(238, 246, t);
  return `rgb(${r}, ${g}, ${b})`;
}

function drawSpectrum(ctx: CanvasRenderingContext2D, frame: FramePayload, x: number, y: number, w: number, h: number) {
  const bands = frame.audio.bands;
  const n = Math.max(1, bands.length);
  const gap = 3;
  const barW = (w - gap * (n - 1)) / n;

  for (let i = 0; i < n; i++) {
    const v = Math.max(0, Math.min(1, bands[i] ?? 0));
    const bh = Math.max(2, v * h);
    const bx = x + i * (barW + gap);
    const color = barColor(i, n);
    ctx.fillStyle = 'rgba(255, 255, 255, 0.04)';
    ctx.fillRect(bx, y, barW, h);
    ctx.fillStyle = color;
    ctx.shadowColor = color;
    ctx.shadowBlur = 7;
    ctx.fillRect(bx, y + h - bh, barW, bh);
  }
  ctx.shadowBlur = 0;

  ctx.fillStyle = 'rgba(139, 147, 167, 0.55)';
  ctx.font = FONT;
  ctx.textAlign = 'left';
  ctx.fillText('SPECTRUM', x, y - 6);
}

function drawIntensity(ctx: CanvasRenderingContext2D, frame: FramePayload, x: number, y: number, w: number) {
  const v = Math.max(0, Math.min(1, frame.audio.intensity));
  const h = 6;
  ctx.fillStyle = 'rgba(255, 255, 255, 0.06)';
  ctx.beginPath();
  ctx.roundRect(x, y, w, h, 3);
  ctx.fill();

  if (v > 0.005) {
    const grad = ctx.createLinearGradient(x, 0, x + w, 0);
    grad.addColorStop(0, '#22d3ee');
    grad.addColorStop(1, '#8b5cf6');
    ctx.fillStyle = grad;
    ctx.shadowColor = '#22d3ee';
    ctx.shadowBlur = 8;
    ctx.beginPath();
    ctx.roundRect(x, y, Math.max(h, w * v), h, 3);
    ctx.fill();
    ctx.shadowBlur = 0;
  }

  ctx.fillStyle = 'rgba(139, 147, 167, 0.55)';
  ctx.font = FONT;
  ctx.textAlign = 'left';
  ctx.fillText('INTENSITY', x, y - 5);
}

export function SignalPanel() {
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const hexRef = useRef<HTMLSpanElement>(null);
  const swatchRef = useRef<HTMLSpanElement>(null);

  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas) return;
    const ctx = canvas.getContext('2d');
    if (!ctx) return;
    let raf = 0;
    let last = 0;
    let lastHexUpdate = 0;

    const draw = (now: number) => {
      raf = requestAnimationFrame(draw);
      if (now - last < REDRAW_MS) return;
      last = now;

      const cssW = canvas.clientWidth;
      const cssH = canvas.clientHeight;
      if (cssW === 0 || cssH === 0) return;
      const dpr = Math.min(window.devicePixelRatio || 1, 1.5);
      const pw = Math.round(cssW * dpr);
      const ph = Math.round(cssH * dpr);
      if (canvas.width !== pw) canvas.width = pw;
      if (canvas.height !== ph) canvas.height = ph;
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
      ctx.clearRect(0, 0, cssW, cssH);

      const frame = getLatestFrame();
      if (!frame) {
        ctx.fillStyle = 'rgba(139, 147, 167, 0.5)';
        ctx.font = FONT;
        ctx.textAlign = 'center';
        ctx.fillText('WAITING FOR SIGNAL…', cssW / 2, cssH / 2);
        return;
      }

      // Layout: screen ring on the left ~42%, spectrum + intensity right.
      const ringW = Math.min(cssW * 0.4, 190);
      const ringH = ringW * 0.58;
      const ringX = 14;
      const ringY = Math.max(16, (cssH - ringH) / 2 - 8);
      drawScreenRing(ctx, frame, ringX, ringY, ringW, ringH);

      const rightX = ringX + ringW + 30;
      const rightW = cssW - rightX - 16;
      if (rightW > 40) {
        const specY = 26;
        const specH = cssH - specY - 52;
        drawSpectrum(ctx, frame, rightX, specY, rightW, specH);
        drawIntensity(ctx, frame, rightX, cssH - 26, rightW);
      }

      // Dominant swatch DOM bits, throttled to ~5fps (text churn is cheap but pointless faster).
      if (now - lastHexUpdate > 200) {
        lastHexUpdate = now;
        const hex = rgbToHex(frame.dominant);
        if (hexRef.current) hexRef.current.textContent = hex.toUpperCase();
        if (swatchRef.current) {
          swatchRef.current.style.background = hex;
          swatchRef.current.style.boxShadow = `0 0 10px ${hex}`;
        }
      }
    };

    raf = requestAnimationFrame(draw);
    return () => cancelAnimationFrame(raf);
  }, []);

  return (
    <div className="signal-panel">
      <canvas ref={canvasRef} className="signal-canvas" />
      <div className="signal-footer">
        <span ref={swatchRef} className="dom-swatch" aria-hidden="true" />
        <span className="dom-label">Dominant</span>
        <span ref={hexRef} className="dom-hex">
          —
        </span>
      </div>
    </div>
  );
}
