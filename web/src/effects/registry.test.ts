// Registry/manifest sync (NFR8) and param hygiene for every effect module.
// Pure metadata only — EffectModule.create() is never called (no WebGL in tests).
import { describe, expect, it } from 'vitest';
import { DEFAULT_EFFECT_ID, effects, effectsById, getEffect } from './registry';
import { defaultParamsOf } from './types';
import manifest from './manifest.json';

const sortById = <T extends { id: string }>(arr: T[]): T[] =>
  [...arr].sort((a, b) => a.id.localeCompare(b.id));

describe('effects registry / manifest sync (NFR8)', () => {
  it('exposes exactly 5 effects with unique ids', () => {
    expect(effects).toHaveLength(5);
    const ids = effects.map((e) => e.id);
    expect(new Set(ids).size).toBe(ids.length);
  });

  it('registry {id, name, description} equals manifest.json entries field-for-field', () => {
    const registryMeta = effects.map(({ id, name, description }) => ({ id, name, description }));
    expect(manifest.effects).toHaveLength(registryMeta.length);
    expect(sortById(registryMeta)).toEqual(sortById(manifest.effects));
  });

  it('effectsById maps every id to its module', () => {
    for (const effect of effects) {
      expect(effectsById[effect.id]).toBe(effect);
    }
  });

  it('DEFAULT_EFFECT_ID exists in the registry and the manifest', () => {
    expect(effectsById[DEFAULT_EFFECT_ID]).toBeDefined();
    expect(manifest.effects.map((e) => e.id)).toContain(DEFAULT_EFFECT_ID);
  });

  it('getEffect returns the matching module, falling back to the default for unknown ids', () => {
    for (const effect of effects) {
      expect(getEffect(effect.id)).toBe(effect);
    }
    expect(getEffect('nonsense')).toBe(effectsById[DEFAULT_EFFECT_ID]);
    expect(getEffect('')).toBe(effectsById[DEFAULT_EFFECT_ID]);
  });
});

describe('param hygiene (every module)', () => {
  for (const module of effects) {
    describe(module.id, () => {
      it('has unique param keys and a default on every ParamDef', () => {
        const keys = module.params.map((p) => p.key);
        expect(new Set(keys).size).toBe(keys.length);
        for (const def of module.params) {
          expect(def.default, `${module.id}.${def.key} must declare a default`).not.toBeUndefined();
        }
      });

      it('range params have min<max, defaults within [min,max], step>0 when present', () => {
        for (const def of module.params) {
          if (def.type !== 'range') continue;
          const label = `${module.id}.${def.key}`;
          expect(typeof def.default, label).toBe('number');
          expect(typeof def.min, `${label}.min`).toBe('number');
          expect(typeof def.max, `${label}.max`).toBe('number');
          if (typeof def.min === 'number' && typeof def.max === 'number') {
            expect(def.min, label).toBeLessThan(def.max);
            expect(def.default as number, label).toBeGreaterThanOrEqual(def.min);
            expect(def.default as number, label).toBeLessThanOrEqual(def.max);
          }
          if (def.step !== undefined) {
            expect(def.step, `${label}.step`).toBeGreaterThan(0);
          }
        }
      });

      it('select params have unique options and a default among them; toggles default to booleans', () => {
        for (const def of module.params) {
          const label = `${module.id}.${def.key}`;
          if (def.type === 'select') {
            expect(Array.isArray(def.options), `${label}.options`).toBe(true);
            const values = (def.options ?? []).map((o) => o.value);
            expect(values.length, label).toBeGreaterThan(0);
            expect(new Set(values).size, label).toBe(values.length);
            expect(typeof def.default, label).toBe('string');
            expect(values, label).toContain(def.default as string);
          } else if (def.type === 'toggle') {
            expect(typeof def.default, label).toBe('boolean');
          }
        }
      });

      it('defaultParamsOf returns exactly the declared keys with the declared defaults', () => {
        const defaults = defaultParamsOf(module);
        const keys = module.params.map((p) => p.key);
        expect(Object.keys(defaults).sort()).toEqual([...keys].sort());
        for (const def of module.params) {
          expect(defaults[def.key]).toBe(def.default);
        }
      });
    });
  }
});
