// ============================================================================
// MonitorMap (FR8) — scaled 2D map of the virtual desktop. Click a non-source
// display to toggle it as a target; the hover pill sets the SOURCE. The source
// outline is ringed by a canvas overlay animating with live edge colors.
// ============================================================================
import { useEffect, useMemo, useRef, useState } from 'react';
import type { MonitorInfo } from '../../shared/bridge';
import { effects } from '../../effects/registry';
import { useControlStore } from '../store';
import { setSourceMonitor, toggleTargetMonitor, setEffectForMonitor } from '../bridgeGlue';
import { getLatestFrame } from '../frameFeed';
import { Select } from './controls';
import './MonitorMap.css';

interface Rect {
  x: number;
  y: number;
  w: number;
  h: number;
}

interface Layout {
  rects: Map<string, Rect>;
  width: number;
  height: number;
}

const PAD = 18;

function computeLayout(monitors: MonitorInfo[], width: number, height: number): Layout | null {
  if (monitors.length === 0 || width <= 0 || height <= 0) return null;
  const minX = Math.min(...monitors.map((m) => m.x));
  const minY = Math.min(...monitors.map((m) => m.y));
  const maxX = Math.max(...monitors.map((m) => m.x + m.width));
  const maxY = Math.max(...monitors.map((m) => m.y + m.height));
  const bw = Math.max(1, maxX - minX);
  const bh = Math.max(1, maxY - minY);
  const scale = Math.min((width - PAD * 2) / bw, (height - PAD * 2) / bh);
  const offX = (width - bw * scale) / 2 - minX * scale;
  const offY = (height - bh * scale) / 2 - minY * scale;
  const rects = new Map<string, Rect>();
  for (const m of monitors) {
    rects.set(m.id, {
      x: m.x * scale + offX,
      y: m.y * scale + offY,
      w: m.width * scale,
      h: m.height * scale,
    });
  }
  return { rects, width, height };
}

const GLOW_REDRAW_MS = 33; // ~30fps

/** Draws live edge-zone colors as a glowing ring around the source rect. */
function useEdgeGlowOverlay(
  canvasRef: React.RefObject<HTMLCanvasElement | null>,
  sourceRectRef: React.RefObject<Rect | null>,
  sizeRef: React.RefObject<{ w: number; h: number }>,
) {
  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas) return;
    const ctx = canvas.getContext('2d');
    if (!ctx) return;
    let raf = 0;
    let last = 0;

    const draw = (now: number) => {
      raf = requestAnimationFrame(draw);
      if (now - last < GLOW_REDRAW_MS) return;
      last = now;

      const { w, h } = sizeRef.current;
      if (canvas.width !== w) canvas.width = w;
      if (canvas.height !== h) canvas.height = h;
      ctx.clearRect(0, 0, w, h);

      const rect = sourceRectRef.current;
      const frame = getLatestFrame();
      if (!rect || !frame) return;

      const pulse = 0.65 + 0.35 * frame.audio.intensity;
      ctx.globalAlpha = pulse;
      const seg = 3;

      const drawZone = (color: [number, number, number], x: number, y: number, zw: number, zh: number) => {
        const css = `rgb(${color[0]}, ${color[1]}, ${color[2]})`;
        ctx.fillStyle = css;
        ctx.shadowColor = css;
        ctx.shadowBlur = 9;
        ctx.fillRect(x, y, zw, zh);
      };

      const { top, bottom, left, right } = frame.edges;
      const nT = top.length;
      for (let i = 0; i < nT; i++) {
        const zw = rect.w / nT;
        drawZone(top[i], rect.x + i * zw + 1, rect.y - seg - 1, zw - 2, seg);
      }
      const nB = bottom.length;
      for (let i = 0; i < nB; i++) {
        const zw = rect.w / nB;
        drawZone(bottom[i], rect.x + i * zw + 1, rect.y + rect.h + 1, zw - 2, seg);
      }
      const nL = left.length;
      for (let i = 0; i < nL; i++) {
        const zh = rect.h / nL;
        drawZone(left[i], rect.x - seg - 1, rect.y + i * zh + 1, seg, zh - 2);
      }
      const nR = right.length;
      for (let i = 0; i < nR; i++) {
        const zh = rect.h / nR;
        drawZone(right[i], rect.x + rect.w + 1, rect.y + i * zh + 1, seg, zh - 2);
      }
      ctx.globalAlpha = 1;
      ctx.shadowBlur = 0;
    };

    raf = requestAnimationFrame(draw);
    return () => cancelAnimationFrame(raf);
  }, [canvasRef, sourceRectRef, sizeRef]);
}

export function MonitorMap({ compact = false }: { compact?: boolean }) {
  const monitors = useControlStore((s) => s.monitors);
  const settings = useControlStore((s) => s.settings);
  const wrapRef = useRef<HTMLDivElement>(null);
  const overlayRef = useRef<HTMLCanvasElement>(null);
  const [size, setSize] = useState({ w: 0, h: 0 });

  useEffect(() => {
    const el = wrapRef.current;
    if (!el) return;
    const ro = new ResizeObserver(() => {
      setSize({ w: el.clientWidth, h: el.clientHeight });
    });
    ro.observe(el);
    return () => ro.disconnect();
  }, []);

  const layout = useMemo(() => computeLayout(monitors, size.w, size.h), [monitors, size]);

  const sourceId = settings?.sourceMonitorId ?? '';
  const targetIds = settings?.targetMonitorIds ?? [];

  // Refs feed the rAF overlay without re-running its effect.
  const sourceRectRef = useRef<Rect | null>(null);
  const sizeRef = useRef({ w: 0, h: 0 });
  useEffect(() => {
    sourceRectRef.current = layout?.rects.get(sourceId) ?? null;
    sizeRef.current = { w: size.w, h: size.h };
  }, [layout, sourceId, size]);

  useEdgeGlowOverlay(overlayRef, sourceRectRef, sizeRef);

  const effectOptions = useMemo(
    () => [{ value: '', label: 'Global effect' }, ...effects.map((e) => ({ value: e.id, label: e.name }))],
    [],
  );

  if (monitors.length === 0) {
    return (
      <div className={`monmap${compact ? ' compact' : ''}`} ref={wrapRef}>
        <div className="empty-state">
          <span className="glyph">🖥</span>
          <span>Waiting for the engine to report your displays…</span>
          <span className="hint-faint">Plug in a second monitor to light things up.</span>
        </div>
      </div>
    );
  }

  return (
    <div className={`monmap${compact ? ' compact' : ''}`} ref={wrapRef}>
      <canvas ref={overlayRef} className="monmap-overlay" aria-hidden="true" />
      {layout &&
        monitors.map((m) => {
          const r = layout.rects.get(m.id);
          if (!r) return null;
          const isSource = m.id === sourceId;
          const isTarget = targetIds.includes(m.id);
          return (
            <div
              key={m.id}
              role="button"
              tabIndex={0}
              aria-label={`${m.name} — ${isSource ? 'source' : isTarget ? 'target' : 'unused'}`}
              className={`mon${isSource ? ' src' : ''}${isTarget ? ' tgt' : ''}`}
              style={{ left: r.x, top: r.y, width: r.w, height: r.h }}
              onClick={() => {
                if (!isSource) toggleTargetMonitor(m.id);
              }}
              onKeyDown={(e) => {
                if ((e.key === 'Enter' || e.key === ' ') && !isSource) {
                  e.preventDefault();
                  toggleTargetMonitor(m.id);
                }
              }}
            >
              <div className="mon-head">
                <span className="mon-name" title={m.name}>
                  {m.name}
                </span>
                {m.isPrimary && (
                  <svg className="mon-star" width="11" height="11" viewBox="0 0 12 12" aria-label="Primary">
                    <path
                      d="M6 0.8 L7.5 4.2 L11.2 4.6 L8.4 7 L9.2 10.7 L6 8.8 L2.8 10.7 L3.6 7 L0.8 4.6 L4.5 4.2 Z"
                      fill="currentColor"
                    />
                  </svg>
                )}
              </div>

              {isSource && <span className="mon-tag src-tag">SOURCE</span>}
              {isTarget && (
                <span className="mon-tag tgt-tag">
                  <svg width="9" height="9" viewBox="0 0 10 10" aria-hidden="true">
                    <path d="M1.5 5.5 L4 8 L8.5 2.5" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" />
                  </svg>
                  TARGET
                </span>
              )}

              {!isSource && (
                <button
                  type="button"
                  className="mon-make-src"
                  onClick={(e) => {
                    e.stopPropagation();
                    setSourceMonitor(m.id);
                  }}
                >
                  Set source
                </button>
              )}

              {isTarget && !compact && r.w > 96 && (
                <div
                  className="mon-effect"
                  onClick={(e) => e.stopPropagation()}
                  onKeyDown={(e) => e.stopPropagation()}
                  role="presentation"
                >
                  <Select
                    size="sm"
                    ariaLabel={`Effect for ${m.name}`}
                    value={settings?.effectByMonitorId[m.id] ?? ''}
                    options={effectOptions}
                    onChange={(v) => setEffectForMonitor(m.id, v)}
                  />
                </div>
              )}
            </div>
          );
        })}
    </div>
  );
}
