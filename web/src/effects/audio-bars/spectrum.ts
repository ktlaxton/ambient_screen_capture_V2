// Spectrum Bars — CPU-side bar dynamics. Resamples the engine's variable-length
// band array (8-16) onto the visual bar count with Catmull-Rom interpolation and
// a mild high-frequency tilt, then runs per-bar attack/release envelopes plus
// gravity-decay peak-hold caps so motion stays musical even at low stream fps.
// Output is an interleaved RG Float32Array (R = eased value, G = peak) that the
// effect uploads as a barCount x 1 DataTexture.

const ATTACK_TAU = 0.02; // s — fast rise so transients land
const RELEASE_TAU = 0.3; // s — slow fall so bars bloom out
const PEAK_HOLD = 0.22; // s — peak cap hangs before falling
const PEAK_GRAVITY = 1.7; // units/s^2 — accelerating cap drop
const SETTLE_EPS = 1e-4;

const clamp01 = (x: number): number => (x < 0 ? 0 : x > 1 ? 1 : x);

/** Catmull-Rom sample of `src` at normalized position t in 0..1 (by FRACTION of length). */
function sampleCatmullRom(src: readonly number[], t: number): number {
  const n = src.length;
  if (n === 0) return 0;
  if (n === 1) return src[0] ?? 0;
  const x = clamp01(t) * (n - 1);
  const i1 = Math.min(n - 1, Math.floor(x));
  const f = x - i1;
  const i0 = Math.max(0, i1 - 1);
  const i2 = Math.min(n - 1, i1 + 1);
  const i3 = Math.min(n - 1, i1 + 2);
  const p0 = src[i0] ?? 0;
  const p1 = src[i1] ?? 0;
  const p2 = src[i2] ?? 0;
  const p3 = src[i3] ?? 0;
  const f2 = f * f;
  const f3 = f2 * f;
  return (
    0.5 *
    (2 * p1 +
      (-p0 + p2) * f +
      (2 * p0 - 5 * p1 + 4 * p2 - p3) * f2 +
      (-p0 + 3 * p1 - 3 * p2 + p3) * f3)
  );
}

export class SpectrumProcessor {
  /** Interleaved RG pairs (value, peak), length = barCount * 2. Texture-ready. */
  data: Float32Array;

  private barCount: number;
  private current: Float32Array;
  private target: Float32Array;
  private peak: Float32Array;
  private peakVel: Float32Array;
  private peakHold: Float32Array;
  private lastBands: number[] | null = null;

  constructor(barCount: number) {
    this.barCount = barCount;
    this.data = new Float32Array(barCount * 2);
    this.current = new Float32Array(barCount);
    this.target = new Float32Array(barCount);
    this.peak = new Float32Array(barCount);
    this.peakVel = new Float32Array(barCount);
    this.peakHold = new Float32Array(barCount);
  }

  get count(): number {
    return this.barCount;
  }

  /** Reallocates for a new visual bar count and re-targets from the last engine frame. */
  setBarCount(barCount: number): void {
    if (barCount === this.barCount) return;
    this.barCount = barCount;
    this.data = new Float32Array(barCount * 2);
    this.current = new Float32Array(barCount);
    this.target = new Float32Array(barCount);
    this.peak = new Float32Array(barCount);
    this.peakVel = new Float32Array(barCount);
    this.peakHold = new Float32Array(barCount);
    if (this.lastBands) this.setTargets(this.lastBands);
  }

  /** Engine-frame input: smooth resample to barCount + mild tilt so highs don't vanish. */
  setTargets(bands: readonly number[]): void {
    this.lastBands = bands.slice();
    const n = this.barCount;
    for (let i = 0; i < n; i++) {
      const f = n > 1 ? i / (n - 1) : 0.5;
      const v = sampleCatmullRom(bands, f) * (0.72 + 0.55 * f);
      this.target[i] = clamp01(v);
    }
  }

  /**
   * Advances envelopes + peaks by dt seconds and rewrites `data`.
   * Returns true when anything moved (i.e. the texture needs re-upload).
   */
  update(dt: number): boolean {
    const kAtk = 1 - Math.exp(-dt / ATTACK_TAU);
    const kRel = 1 - Math.exp(-dt / RELEASE_TAU);
    let changed = false;
    for (let i = 0; i < this.barCount; i++) {
      const tgt = this.target[i] ?? 0;
      let cur = this.current[i] ?? 0;
      const d = (tgt - cur) * (tgt > cur ? kAtk : kRel);
      cur += d;
      this.current[i] = cur;

      let pk = this.peak[i] ?? 0;
      if (cur >= pk) {
        pk = cur;
        this.peakVel[i] = 0;
        this.peakHold[i] = PEAK_HOLD;
      } else if ((this.peakHold[i] ?? 0) > 0) {
        this.peakHold[i] = (this.peakHold[i] ?? 0) - dt;
      } else {
        const vel = (this.peakVel[i] ?? 0) + PEAK_GRAVITY * dt;
        this.peakVel[i] = vel;
        pk = Math.max(cur, pk - vel * dt);
      }
      const prevPk = this.peak[i] ?? 0;
      this.peak[i] = pk;

      if (Math.abs(d) > SETTLE_EPS || Math.abs(pk - prevPk) > SETTLE_EPS) changed = true;
      this.data[i * 2] = cur;
      this.data[i * 2 + 1] = pk;
    }
    return changed;
  }
}
