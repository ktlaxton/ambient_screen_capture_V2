// Single source of truth for available effects. manifest.json carries the same
// ids/names for anything that can't import TS — a vitest test keeps them in sync.
import type { EffectModule } from './types';
import edgeGlow from './edge-glow';
import plasma from './plasma';
import audioBars from './audio-bars';
import particles from './particles';
import aurora from './aurora';

export const effects: EffectModule[] = [edgeGlow, plasma, audioBars, particles, aurora];

export const effectsById: Record<string, EffectModule> = Object.fromEntries(
  effects.map((effect) => [effect.id, effect]),
);

export const DEFAULT_EFFECT_ID = 'edge-glow';

export function getEffect(id: string): EffectModule {
  return effectsById[id] ?? effectsById[DEFAULT_EFFECT_ID];
}
