// ============================================================================
// AmbientBackground — a fixed, very low-opacity radial glow tinted live by the
// current dominant screen color, so the app itself feels ambient. Reads the
// frame feed inside its own rAF loop (never via React state) and paints a tiny
// canvas that CSS stretches over the window (free blur from upscaling).
// ============================================================================
import { useEffect, useRef } from 'react';
import { getLatestFrame } from '../frameFeed';

const REDRAW_MS = 90; // ~11fps is plenty for a slow ambient wash
const SMOOTH = 0.07;

export function AmbientBackground() {
  const ref = useRef<HTMLCanvasElement>(null);

  useEffect(() => {
    const canvas = ref.current;
    if (!canvas) return;
    const ctx = canvas.getContext('2d');
    if (!ctx) return;

    // Start on the brand accent so the boot screen already glows.
    const tint = { r: 34, g: 120, b: 200 };
    let raf = 0;
    let last = 0;

    const draw = (now: number) => {
      raf = requestAnimationFrame(draw);
      if (now - last < REDRAW_MS) return;
      last = now;

      const frame = getLatestFrame();
      if (frame) {
        tint.r += (frame.dominant[0] - tint.r) * SMOOTH;
        tint.g += (frame.dominant[1] - tint.g) * SMOOTH;
        tint.b += (frame.dominant[2] - tint.b) * SMOOTH;
      }

      // Tiny backing store; CSS scales it up -> soft for free.
      const w = Math.max(2, Math.round(canvas.clientWidth / 12));
      const h = Math.max(2, Math.round(canvas.clientHeight / 12));
      if (canvas.width !== w) canvas.width = w;
      if (canvas.height !== h) canvas.height = h;

      const rgb = `${Math.round(tint.r)}, ${Math.round(tint.g)}, ${Math.round(tint.b)}`;
      ctx.clearRect(0, 0, w, h);

      const top = ctx.createRadialGradient(w * 0.5, h * 0.1, 0, w * 0.5, h * 0.1, Math.max(w, h) * 0.85);
      top.addColorStop(0, `rgba(${rgb}, 0.85)`);
      top.addColorStop(1, 'rgba(0, 0, 0, 0)');
      ctx.fillStyle = top;
      ctx.fillRect(0, 0, w, h);

      const corner = ctx.createRadialGradient(w * 0.92, h * 0.95, 0, w * 0.92, h * 0.95, Math.max(w, h) * 0.6);
      corner.addColorStop(0, `rgba(${rgb}, 0.4)`);
      corner.addColorStop(1, 'rgba(0, 0, 0, 0)');
      ctx.fillStyle = corner;
      ctx.fillRect(0, 0, w, h);
    };

    raf = requestAnimationFrame(draw);
    return () => cancelAnimationFrame(raf);
  }, []);

  return <canvas ref={ref} className="ambient-bg" aria-hidden="true" />;
}
