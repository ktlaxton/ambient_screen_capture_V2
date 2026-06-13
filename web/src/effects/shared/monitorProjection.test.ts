// monitorProjection — table-driven geometry cases (Story 7.5).
// Desktop coordinates: Y grows downward. entry: 0=left 1=right 2=bottom 3=top.
// sourceRow: 0=top 1=bottom 2=left 3=right.
import { describe, expect, it } from 'vitest';
import { projectMonitors, projectionFromRelation, type LayoutRect } from './monitorProjection';

const rect = (x: number, y: number, width: number, height: number): LayoutRect => ({ x, y, width, height });

/** The sim/typical source: 2560x1440 at the origin. */
const SRC = rect(0, 0, 2560, 1440);

describe('projectMonitors — degenerate inputs', () => {
  it('returns [] for null/undefined rects', () => {
    expect(projectMonitors(null, SRC)).toEqual([]);
    expect(projectMonitors(SRC, null)).toEqual([]);
    expect(projectMonitors(undefined, undefined)).toEqual([]);
  });

  it('returns [] for zero-area monitors', () => {
    expect(projectMonitors(SRC, rect(3000, 0, 0, 1080))).toEqual([]);
    expect(projectMonitors(rect(0, 0, 2560, 0), rect(3000, 0, 1920, 1080))).toEqual([]);
  });

  it('returns [] for co-located (mirrored) monitors', () => {
    expect(projectMonitors(SRC, rect(0, 0, 2560, 1440))).toEqual([]);
    // centers within the dead zone despite differing sizes
    expect(projectMonitors(SRC, rect(320, 180, 1920, 1080))).toEqual([]);
  });
});

describe('projectMonitors — side neighbors', () => {
  it('equal monitors side by side: identity mapping onto the source right edge', () => {
    const out = projectMonitors(SRC, rect(2560, 0, 2560, 1440));
    expect(out).toEqual([{ entry: 0, sourceRow: 3, scale: 1, offset: 0, weight: 1 }]);
  });

  it('smaller raised target to the right samples only the aligned span', () => {
    // 1080p at y=180 next to a 1440p source: spans 12.5%..87.5% of the source edge
    const out = projectMonitors(SRC, rect(2560, 180, 1920, 1080));
    expect(out).toHaveLength(1);
    const e = out[0];
    expect(e.entry).toBe(0); // light enters the target's LEFT side
    expect(e.sourceRow).toBe(3); // fed by the source RIGHT edge
    expect(e.scale).toBeCloseTo(1080 / 1440);
    expect(e.offset).toBeCloseTo(180 / 1440);
    expect(e.weight).toBe(1);
    // s=0 (target top) -> 12.5% down the source edge; s=1 -> 87.5%
    expect(e.offset).toBeCloseTo(0.125);
    expect(e.scale + e.offset).toBeCloseTo(0.875);
  });

  it('target to the left mirrors entry/sourceRow', () => {
    const out = projectMonitors(SRC, rect(-1920, 180, 1920, 1080));
    expect(out).toHaveLength(1);
    expect(out[0].entry).toBe(1); // light from the RIGHT
    expect(out[0].sourceRow).toBe(2); // source LEFT edge
  });

  it('target above maps along X onto the source top edge', () => {
    const out = projectMonitors(SRC, rect(320, -1080, 1920, 1080));
    expect(out).toHaveLength(1);
    const e = out[0];
    expect(e.entry).toBe(2); // light from the BOTTOM
    expect(e.sourceRow).toBe(0); // source TOP edge
    expect(e.scale).toBeCloseTo(1920 / 2560);
    expect(e.offset).toBeCloseTo(320 / 2560);
  });

  it('target below maps onto the source bottom edge', () => {
    const out = projectMonitors(SRC, rect(0, 1440, 2560, 1440));
    expect(out).toEqual([{ entry: 3, sourceRow: 1, scale: 1, offset: 0, weight: 1 }]);
  });

  it('a gap between the monitors does not change the mapping (AC2)', () => {
    const adjacent = projectMonitors(SRC, rect(2560, 180, 1920, 1080));
    const gapped = projectMonitors(SRC, rect(2560 + 600, 180, 1920, 1080));
    expect(gapped).toEqual(adjacent);
  });

  it('a larger target than the source gets scale > 1 and a negative offset', () => {
    // source edge covers only the middle of the target's entry edge
    const out = projectMonitors(rect(0, 480, 1920, 1080), rect(1920, 0, 2560, 2160));
    expect(out).toHaveLength(1);
    const e = out[0];
    expect(e.entry).toBe(0);
    expect(e.scale).toBeCloseTo(2160 / 1080);
    expect(e.offset).toBeCloseTo(-480 / 1080);
  });

  it('a 1px sliver of overlap on the shared axis still counts as a side neighbor', () => {
    const out = projectMonitors(SRC, rect(2560, 1439, 1920, 1080));
    expect(out).toHaveLength(1);
    expect(out[0].entry).toBe(0);
    expect(out[0].weight).toBe(1);
  });
});

describe('projectMonitors — diagonal placements', () => {
  it('exact corner-to-corner blends both edges 50/50', () => {
    // target's bottom-left corner touches the source's top-right corner
    const out = projectMonitors(SRC, rect(2560, -1080, 1920, 1080));
    expect(out).toHaveLength(2);
    const [h, v] = out;
    expect(h.entry).toBe(0); // from the left (source right edge)
    expect(h.sourceRow).toBe(3);
    expect(v.entry).toBe(2); // from the bottom (source top edge)
    expect(v.sourceRow).toBe(0);
    expect(h.weight).toBeCloseTo(0.5);
    expect(v.weight).toBeCloseTo(0.5);
  });

  it('barely-diagonal (far right, slightly past the top) is dominated by the horizontal edge', () => {
    // 50px past the top edge but 800px past the right edge
    const out = projectMonitors(SRC, rect(2560 + 800, -1080 - 50, 1920, 1080));
    expect(out).toHaveLength(2);
    const [h, v] = out;
    expect(h.entry).toBe(0);
    expect(v.entry).toBe(2);
    expect(h.weight).toBeGreaterThan(0.85);
    expect(h.weight + v.weight).toBeCloseTo(1);
  });

  it('mostly-above diagonal is dominated by the vertical edge', () => {
    const out = projectMonitors(SRC, rect(2560 + 50, -1080 - 800, 1920, 1080));
    expect(out).toHaveLength(2);
    const [h, v] = out;
    expect(v.weight).toBeGreaterThan(0.85);
    expect(h.weight + v.weight).toBeCloseTo(1);
  });

  it('bottom-left diagonal picks the opposite edges', () => {
    const out = projectMonitors(SRC, rect(-1920, 1440, 1920, 1080));
    expect(out).toHaveLength(2);
    const [h, v] = out;
    expect(h.entry).toBe(1); // light from the right (source left edge)
    expect(h.sourceRow).toBe(2);
    expect(v.entry).toBe(3); // light from the top (source bottom edge)
    expect(v.sourceRow).toBe(1);
  });

  it('diagonal mappings are fully out of span (clamped by the consumer)', () => {
    const out = projectMonitors(SRC, rect(2560, -1080, 1920, 1080));
    const h = out[0]; // Y-mapping: target sits entirely above the source
    // s in [0,1] maps to e = s*scale + offset entirely <= 0
    expect(h.scale + h.offset).toBeLessThanOrEqual(0);
  });
});

describe('projectMonitors — overlapping rects (defensive)', () => {
  it('partially overlapping rects fall back to the dominant center direction', () => {
    const out = projectMonitors(SRC, rect(2000, 100, 1920, 1080)); // overlaps, mostly to the right
    expect(out).toHaveLength(1);
    expect(out[0].entry).toBe(0);
    expect(out[0].sourceRow).toBe(3);
    expect(out[0].weight).toBe(1);
  });
});

describe('projectionFromRelation — coarse fallback', () => {
  it('maps each relation to the identity-span edge', () => {
    expect(projectionFromRelation('right')).toEqual([{ entry: 0, sourceRow: 3, scale: 1, offset: 0, weight: 1 }]);
    expect(projectionFromRelation('left')).toEqual([{ entry: 1, sourceRow: 2, scale: 1, offset: 0, weight: 1 }]);
    expect(projectionFromRelation('above')).toEqual([{ entry: 2, sourceRow: 0, scale: 1, offset: 0, weight: 1 }]);
    expect(projectionFromRelation('below')).toEqual([{ entry: 3, sourceRow: 1, scale: 1, offset: 0, weight: 1 }]);
  });

  it("returns [] for 'none' and unknown strings", () => {
    expect(projectionFromRelation('none')).toEqual([]);
    expect(projectionFromRelation('sideways')).toEqual([]);
  });
});
