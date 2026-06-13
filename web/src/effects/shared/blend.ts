// Blend-mode param plumbing (Story 7.2, AC4): one shared enum -> three.js
// blending mapping so effects never hand-roll blending constants.
import * as THREE from 'three';
import type { EffectParams } from '../../shared/bridge';

export type BlendMode = 'normal' | 'additive' | 'screen';

export const BLEND_OPTIONS: { value: BlendMode; label: string }[] = [
  { value: 'normal', label: 'Normal' },
  { value: 'additive', label: 'Additive' },
  { value: 'screen', label: 'Screen' },
];

const BLEND_VALUES = new Set<string>(BLEND_OPTIONS.map((o) => o.value));

export function readBlendMode(params: EffectParams, key: string, fallback: BlendMode): BlendMode {
  const v = params[key];
  return typeof v === 'string' && BLEND_VALUES.has(v) ? (v as BlendMode) : fallback;
}

/** Applies a blend mode to a material ('screen' = src + dst*(1-src), via CustomBlending). */
export function applyBlendMode(material: THREE.Material, mode: BlendMode): void {
  switch (mode) {
    case 'additive':
      material.blending = THREE.AdditiveBlending;
      break;
    case 'screen':
      material.blending = THREE.CustomBlending;
      material.blendEquation = THREE.AddEquation;
      material.blendSrc = THREE.OneFactor;
      material.blendDst = THREE.OneMinusSrcColorFactor;
      break;
    default:
      material.blending = THREE.NormalBlending;
      break;
  }
  material.needsUpdate = true;
}
