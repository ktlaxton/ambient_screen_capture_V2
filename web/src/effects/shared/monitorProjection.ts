// Position-aware monitor projection (Story 7.5, FR7): maps a target monitor's
// light-entry edge(s) onto the aligned span of the source monitor's edge in
// virtual-desktop coordinates. Pure math, no three.js — usable by any
// layout-aware effect. Y grows downward (Windows virtual-desktop convention).
import type { MonitorRelation } from '../../shared/bridge';

/** Subset of MonitorInfo the projection needs (virtual-desktop pixels). */
export interface LayoutRect {
  x: number;
  y: number;
  width: number;
  height: number;
}

/** Window side the light enters from: 0=left 1=right 2=bottom 3=top. */
export type EntrySide = 0 | 1 | 2 | 3;

/** Source edge strip index (FramePayload/DataTexture rows): 0=top 1=bottom 2=left 3=right. */
export type SourceRow = 0 | 1 | 2 | 3;

/**
 * One entry edge's contribution. `s` runs 0..1 along the target's entry edge in
 * desktop direction (downward for vertical edges, rightward for horizontal);
 * the aligned source-edge coordinate is `e = s * scale + offset`, where e in
 * [0,1] spans the source edge with the same orientation. e outside [0,1] means
 * that part of the target extends past the source edge's ends (consumers clamp
 * the color to the nearest end and may attenuate).
 */
export interface EdgeProjection {
  entry: EntrySide;
  sourceRow: SourceRow;
  scale: number;
  offset: number;
  /** Contribution weight; 1 for a single edge, fractions summing to 1 for corner blends. */
  weight: number;
}

/** Normalized center-distance below which the monitors are considered co-located
 *  (mirrored displays). Mirrors MonitorLayout.DeadZone on the C# side. */
const DEAD_ZONE = 0.05;

/** Affine s→e mapping along the vertical (Y) axis for left/right entry edges. */
function mapY(source: LayoutRect, target: LayoutRect): { scale: number; offset: number } {
  return { scale: target.height / source.height, offset: (target.y - source.y) / source.height };
}

/** Affine s→e mapping along the horizontal (X) axis for top/bottom entry edges. */
function mapX(source: LayoutRect, target: LayoutRect): { scale: number; offset: number } {
  return { scale: target.width / source.width, offset: (target.x - source.x) / source.width };
}

/** Target right of source → light enters target's LEFT, fed by source RIGHT edge (etc.). */
function horizontalEdge(source: LayoutRect, target: LayoutRect, right: boolean, weight: number): EdgeProjection {
  return { entry: right ? 0 : 1, sourceRow: right ? 3 : 2, ...mapY(source, target), weight };
}

function verticalEdge(source: LayoutRect, target: LayoutRect, below: boolean, weight: number): EdgeProjection {
  return { entry: below ? 3 : 2, sourceRow: below ? 1 : 0, ...mapX(source, target), weight };
}

/**
 * Computes the entry-edge projection(s) of `target` relative to `source`.
 *
 * - Side neighbors (any X/Y overlap on the shared axis, gaps allowed) → one edge.
 * - Diagonal placements (separated on both axes, exact corner included) → the two
 *   nearest edges, weighted by how far past each axis the target sits (a barely
 *   diagonal "right and slightly up" stays mostly a right neighbor). Weights sum to 1.
 * - Gap size never changes the mapping or the weights' total — projection is by
 *   alignment, not distance (AC2).
 * - Returns [] for null/zero-area rects or co-located (mirrored) monitors —
 *   callers fall back to ambient/halo behavior.
 */
export function projectMonitors(
  source: LayoutRect | null | undefined,
  target: LayoutRect | null | undefined,
): EdgeProjection[] {
  if (!source || !target) return [];
  if (source.width <= 0 || source.height <= 0 || target.width <= 0 || target.height <= 0) return [];

  const avgW = (source.width + target.width) / 2;
  const avgH = (source.height + target.height) / 2;
  const ndx = (target.x + target.width / 2 - (source.x + source.width / 2)) / avgW;
  const ndy = (target.y + target.height / 2 - (source.y + source.height / 2)) / avgH;
  if (Math.abs(ndx) < DEAD_ZONE && Math.abs(ndy) < DEAD_ZONE) return []; // mirrored / co-located

  // Signed gap per axis: > 0 separated, 0 touching, < 0 overlapping.
  const gapX = Math.max(target.x - (source.x + source.width), source.x - (target.x + target.width));
  const gapY = Math.max(target.y - (source.y + source.height), source.y - (target.y + target.height));
  const right = ndx > 0;
  const below = ndy > 0;

  if (gapY < 0 && gapX >= 0) return [horizontalEdge(source, target, right, 1)];
  if (gapX < 0 && gapY >= 0) return [verticalEdge(source, target, below, 1)];

  if (gapX >= 0 && gapY >= 0) {
    // Diagonal: blend the two edges meeting at the nearest source corner. The
    // axis the target sits further past contributes more (resolution-normalized).
    const gx = gapX / avgW;
    const gy = gapY / avgH;
    const wH = gx + gy < 1e-6 ? 0.5 : gx / (gx + gy);
    return [
      horizontalEdge(source, target, right, wH),
      verticalEdge(source, target, below, 1 - wH),
    ];
  }

  // Overlapping rects (not co-located — partial overlap can't be configured in
  // Windows, but be defensive): dominant center direction, single edge.
  return Math.abs(ndx) >= Math.abs(ndy)
    ? [horizontalEdge(source, target, right, 1)]
    : [verticalEdge(source, target, below, 1)];
}

/**
 * Identity-mapped projection from the coarse relation string — the fallback for
 * payloads without monitor rects (older engines, hand-built configs). 'none' → [].
 */
export function projectionFromRelation(relation: MonitorRelation | string): EdgeProjection[] {
  switch (relation) {
    case 'right':
      return [{ entry: 0, sourceRow: 3, scale: 1, offset: 0, weight: 1 }];
    case 'left':
      return [{ entry: 1, sourceRow: 2, scale: 1, offset: 0, weight: 1 }];
    case 'above':
      return [{ entry: 2, sourceRow: 0, scale: 1, offset: 0, weight: 1 }];
    case 'below':
      return [{ entry: 3, sourceRow: 1, scale: 1, offset: 0, weight: 1 }];
    default:
      return [];
  }
}
