// ============================================================================
// EffectGallery (FR8) — one card per registered effect module with a LIVE
// preview. Previews are instantiated lazily (IntersectionObserver), fed the
// shared frame stream, rendered at <=30fps, paused when off-screen or the tab
// is hidden, and disposed on unmount.
// ============================================================================
import { useEffect, useRef, useState } from 'react';
import type { EffectInstance, EffectModule } from '../../effects/types';
import { effects } from '../../effects/registry';
import { useControlStore } from '../store';
import { setEffectGlobal, resolvedEffectParams } from '../bridgeGlue';
import { getLatestFrame, subscribeFrames } from '../frameFeed';
import './EffectGallery.css';

const PREVIEW_FPS = 30;
const FRAME_MS = 1000 / PREVIEW_FPS;

/** Drives one preview instance: lazy create, capped loop, pause, dispose. */
function usePreview(
  module: EffectModule,
  cardRef: React.RefObject<HTMLElement | null>,
  canvasRef: React.RefObject<HTMLCanvasElement | null>,
): boolean {
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    const card = cardRef.current;
    const canvas = canvasRef.current;
    if (!card || !canvas) return;

    let instance: EffectInstance | null = null;
    let disposed = false;
    let visible = false;
    let raf = 0;
    let lastRender = 0;
    let unsubFrame: (() => void) | null = null;
    let unsubStore: (() => void) | null = null;
    let lastParamsJson = '';
    let lastGlobalsJson = '';

    const applySettings = () => {
      if (!instance) return;
      const s = useControlStore.getState().settings;
      const params = resolvedEffectParams(module.id);
      const paramsJson = JSON.stringify(params);
      if (paramsJson !== lastParamsJson) {
        lastParamsJson = paramsJson;
        instance.setParams(params);
      }
      const globals = { intensity: s?.globalIntensity ?? 1, brightness: s?.brightness ?? 1 };
      const globalsJson = JSON.stringify(globals);
      if (globalsJson !== lastGlobalsJson) {
        lastGlobalsJson = globalsJson;
        instance.setGlobals(globals);
      }
    };

    const resize = () => {
      if (!instance) return;
      const w = canvas.clientWidth;
      const h = canvas.clientHeight;
      if (w > 0 && h > 0) instance.resize(w, h);
    };

    const ensureInstance = () => {
      if (instance || disposed) return;
      try {
        instance = module.create({ canvas, windowConfig: null, preview: true });
      } catch (err) {
        console.error(`[gallery] failed to create preview for "${module.id}"`, err);
        setFailed(true);
        return;
      }
      const f = getLatestFrame();
      if (f) instance.onFrame(f);
      unsubFrame = subscribeFrames((frame) => instance?.onFrame(frame));
      unsubStore = useControlStore.subscribe(() => applySettings());
      applySettings();
      resize();
    };

    const loop = (now: number) => {
      raf = requestAnimationFrame(loop);
      if (now - lastRender < FRAME_MS) return;
      const dt = lastRender === 0 ? FRAME_MS : now - lastRender;
      lastRender = now;
      instance?.render(now, dt);
    };

    const start = () => {
      if (raf === 0 && visible && !document.hidden && instance) {
        lastRender = 0;
        raf = requestAnimationFrame(loop);
      }
    };
    const stop = () => {
      if (raf !== 0) cancelAnimationFrame(raf);
      raf = 0;
    };

    const ro = new ResizeObserver(resize);
    ro.observe(canvas);

    const io = new IntersectionObserver(
      (entries) => {
        visible = entries[0]?.isIntersecting ?? false;
        if (visible) {
          ensureInstance();
          start();
        } else {
          stop();
        }
      },
      { threshold: 0.05 },
    );
    io.observe(card);

    const onVisibility = () => {
      if (document.hidden) stop();
      else start();
    };
    document.addEventListener('visibilitychange', onVisibility);

    return () => {
      disposed = true;
      stop();
      io.disconnect();
      ro.disconnect();
      document.removeEventListener('visibilitychange', onVisibility);
      unsubFrame?.();
      unsubStore?.();
      instance?.dispose();
      instance = null;
    };
  }, [module, cardRef, canvasRef]);

  return failed;
}

function EffectCard({
  module,
  active,
  compact,
  onPick,
}: {
  module: EffectModule;
  active: boolean;
  compact: boolean;
  onPick: (effectId: string) => void;
}) {
  const cardRef = useRef<HTMLButtonElement>(null);
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const failed = usePreview(module, cardRef, canvasRef);

  return (
    <button
      ref={cardRef}
      type="button"
      className={`fx-card${active ? ' active' : ''}${compact ? ' compact' : ''}`}
      onClick={() => onPick(module.id)}
      aria-pressed={active}
    >
      <div className="fx-preview">
        {failed ? <div className="fx-fallback" aria-hidden="true" /> : <canvas ref={canvasRef} />}
        {active && <span className="fx-badge">ACTIVE</span>}
      </div>
      <div className="fx-meta">
        <span className="fx-name">{module.name}</span>
        {!compact && <span className="fx-desc">{module.description}</span>}
      </div>
    </button>
  );
}

export function EffectGallery({
  compact = false,
  onPick,
}: {
  compact?: boolean;
  /** Override the click action (onboarding); defaults to setting the global effect. */
  onPick?: (effectId: string) => void;
}) {
  const activeEffectId = useControlStore((s) => s.settings?.activeEffectId ?? '');
  const pick = onPick ?? setEffectGlobal;

  return (
    <div className={`fx-gallery${compact ? ' compact' : ''}`}>
      {effects.map((module) => (
        <EffectCard
          key={module.id}
          module={module}
          active={module.id === activeEffectId}
          compact={compact}
          onPick={pick}
        />
      ))}
    </div>
  );
}
