// Particle Field shaders. Motion is entirely stateless in the vertex shader
// (seeded sin/cos pseudo-curl from static attributes + time uniforms), so there
// are zero per-frame attribute uploads. Colors come from the 4-row edge
// DataTexture sampled by the particle's wrapped position; audio arrives as a
// 16-slot uniform float array sampled by each particle's band affinity.

const HASH_12 = /* glsl */ `
float hash12(vec2 p) {
  vec3 p3 = fract(vec3(p.xyx) * 0.1031);
  p3 += dot(p3, p3.yzx + 33.33);
  return fract((p3.x + p3.y) * p3.z);
}
`;

export const POINTS_VERTEX = /* glsl */ `
attribute vec4 aSeed;  // 4x random 0..1: drift dir / speed / domain offset / phase
attribute vec4 aProps; // x: size factor, y: band affinity, z: palette coord, w: hue jitter

uniform float uFlowTime;   // integrated swirl time (CPU-eased, bass/intensity scaled)
uniform float uDriftTime;  // integrated base-drift time
uniform float uFlowAmp;    // swirl displacement amplitude (world units)
uniform float uKick;       // eased onset energy 0..1 -> outward radial push
uniform vec3 uBounds;      // wrap half-extents (x, y) and half-depth (z)
uniform vec2 uFlip;        // 1 = mirror that axis (monitor adjacency, FR7)
uniform float uPointScale; // px = worldSize * uPointScale / viewDistance
uniform float uSizeBase;   // world-space base sprite size
uniform float uMaxPoint;   // gl_PointSize clamp (px)
uniform float uPulse;      // audio size-pulse strength 0..1 (band adds up to +150%)
uniform float uLift;       // audio brightness-lift strength
uniform sampler2D uEdges;  // zonesPerEdge x 4 rows: top, bottom, left, right
uniform vec3 uDominant;    // eased dominant screen color 0..1
uniform float uBands[16];  // eased audio bands, resampled to 16 slots

varying vec3 vColor;
varying float vAlpha;

float bandValue(float aff) {
  float f = clamp(aff, 0.0, 1.0) * 15.0;
  int i = int(f);
  return mix(uBands[i], uBands[min(i + 1, 15)], fract(f));
}

// Cheap hue rotation around the grey axis (small angles only).
vec3 hueShift(vec3 c, float a) {
  const vec3 k = vec3(0.577350269);
  float ca = cos(a);
  return c * ca + cross(k, c) * sin(a) + k * dot(k, c) * (1.0 - ca);
}

void main() {
  vec3 base = position * uBounds; // static home position inside the wrap box
  float band = bandValue(aProps.y);

  // --- motion: slow per-particle drift + layered curl-like swirl ---
  float sp = 0.55 + 0.9 * aSeed.y;
  vec2 driftDir = normalize(vec2(aSeed.x - 0.5, aSeed.z - 0.5) + vec2(1e-4, 2e-4));
  vec3 p = base;
  p.xy += driftDir * (uDriftTime * sp);

  float tf = uFlowTime + aSeed.w * 6.2831853;
  vec2 q = base.xy * 1.3 + aSeed.xz * 4.0;
  vec2 swirl;
  swirl.x = sin(q.y * 1.7 + tf * 0.9) + 0.5 * sin(q.y * 3.1 - tf * 0.6 + aSeed.z * 6.0);
  swirl.y = cos(q.x * 1.4 - tf * 0.7) + 0.5 * cos(q.x * 2.9 + tf * 0.5 + aSeed.w * 6.0);
  swirl += 0.6 * vec2(sin(base.y * 2.3 + tf * 0.43), cos(base.x * 2.1 - tf * 0.37));
  p.xy += swirl * uFlowAmp * (0.6 + 0.4 * aSeed.x);

  // Gentle outward kick on strong onsets, stronger for the particle's band.
  vec2 rdir = p.xy / max(length(p.xy), 0.05);
  p.xy += rdir * uKick * (0.3 + 0.7 * band) * 0.22 * uBounds.y;

  // Wrap around the view bounds (mod space) so density stays uniform forever.
  p.x = mod(p.x + uBounds.x, 2.0 * uBounds.x) - uBounds.x;
  p.y = mod(p.y + uBounds.y, 2.0 * uBounds.y) - uBounds.y;

  // --- color: sample screen edges by normalized position, blend to dominant ---
  vec2 n = clamp(p.xy / (2.0 * uBounds.xy) + 0.5, 0.0, 1.0);
  n = mix(n, 1.0 - n, uFlip);
  vec3 cTop    = texture2D(uEdges, vec2(n.x, 0.125)).rgb;
  vec3 cBottom = texture2D(uEdges, vec2(n.x, 0.375)).rgb;
  vec3 cLeft   = texture2D(uEdges, vec2(1.0 - n.y, 0.625)).rgb;
  vec3 cRight  = texture2D(uEdges, vec2(1.0 - n.y, 0.875)).rgb;
  float wT = n.y * n.y;
  float wB = (1.0 - n.y) * (1.0 - n.y);
  float wL = (1.0 - n.x) * (1.0 - n.x);
  float wR = n.x * n.x;
  vec3 edgeCol = (cTop * wT + cBottom * wB + cLeft * wL + cRight * wR)
               / (wT + wB + wL + wR + 1e-4);
  float center = 1.0 - smoothstep(0.0, 0.75, length(n - 0.5) * 2.0);
  vec3 col = mix(edgeCol, uDominant, center * (0.35 + 0.45 * aProps.z));
  col = max(hueShift(col, (aProps.w - 0.5) * 0.55), 0.0);
  col = max(col, vec3(0.020, 0.025, 0.045)); // motes stay faintly luminous on dark screens

  float lift = 1.0 + uLift * band;
  vColor = col * lift;

  // --- size + projection ---
  float pulse = 1.0 + uPulse * band * 1.5; // up to +150%
  vec4 mv = modelViewMatrix * vec4(p, 1.0);
  float dist = max(0.4, -mv.z);
  gl_PointSize = clamp(uSizeBase * aProps.x * pulse * uPointScale / dist, 0.0, uMaxPoint);
  gl_Position = projectionMatrix * mv;

  // Small sprites are dimmer; far sprites fade slightly for depth.
  float sizeDim = mix(0.35, 1.0, smoothstep(0.4, 1.6, aProps.x * pulse));
  float depthFade = mix(1.0, 0.55, clamp((dist - 1.35) / 1.8, 0.0, 1.0));
  vAlpha = sizeDim * depthFade;
}
`;

export const POINTS_FRAGMENT = /* glsl */ `
uniform float uBrightness;
varying vec3 vColor;
varying float vAlpha;
${HASH_12}
void main() {
  vec2 d = gl_PointCoord - 0.5;
  float r = length(d) * 2.0;
  if (r > 1.0) discard;
  float falloff = 1.0 - smoothstep(0.0, 1.0, r);
  float glow = falloff * falloff * (0.45 + 0.55 * falloff); // soft core, faked in-shader
  vec3 c = vColor * (glow * vAlpha * uBrightness);
  // Tiny hash dither so dim additive gradients don't band.
  c += (hash12(gl_PointCoord * 311.7 + vColor.rg * 17.0) - 0.5) * (1.0 / 255.0);
  gl_FragColor = vec4(max(c, 0.0), 1.0);
}
`;

// Background: NDC passthrough triangle, ignores the perspective camera.
export const BG_VERTEX = /* glsl */ `
varying vec2 vUv;
void main() {
  vUv = position.xy * 0.5 + 0.5;
  gl_Position = vec4(position.xy, 0.0, 1.0);
}
`;

export const BG_FRAGMENT = /* glsl */ `
uniform vec3 uDominant;
uniform float uBrightness;
uniform vec2 uAspect; // (aspect, 1)
varying vec2 vUv;
${HASH_12}
void main() {
  vec2 pos = (vUv - 0.5) * 2.0 * uAspect;
  float r = length(pos) / length(uAspect); // 0 center -> 1 corners
  float vig = 1.0 - smoothstep(0.15, 1.05, r);
  vec3 base = vec3(0.020, 0.024, 0.040); // near-black #05060a
  vec3 c = base * (0.55 + 0.45 * vig) + uDominant * (0.05 * vig);
  c *= uBrightness;
  c += (hash12(gl_FragCoord.xy) - 0.5) * (2.0 / 255.0); // anti-banding dither
  gl_FragColor = vec4(max(c, vec3(0.0)), 1.0);
}
`;
