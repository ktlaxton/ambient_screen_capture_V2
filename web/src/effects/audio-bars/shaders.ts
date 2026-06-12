// Spectrum Bars — GLSL sources. One fullscreen pass does everything:
// SDF rounded bars + in-shader glow + mirrored reflection + peak caps,
// in either linear ("bars") or polar ("radial") layout.

export const VERTEX_SHADER = /* glsl */ `
varying vec2 vUv;
void main() {
  vUv = position.xy * 0.5 + 0.5;
  gl_Position = vec4(position.xy, 0.0, 1.0);
}
`;

// NEIGHBORS is injected via material.defines (2 in preview, 3 fullscreen).
export const FRAGMENT_SHADER = /* glsl */ `
#define TAU 6.28318530718
#define BASELINE 0.32
#define MAXH 0.52

uniform sampler2D uBars;      // barCount x 1, R = eased value, G = peak hold (0..1)
uniform sampler2D uGrad;      // zonesPerEdge x 1, horizontal color gradient (top edge colors)
uniform float uBarCount;
uniform float uTime;          // seconds (wrapped)
uniform vec2  uRes;           // drawing-buffer pixels
uniform float uAspect;        // css width / height
uniform float uIntensity;     // global 0..1 — scales motion/reactivity
uniform float uBrightness;    // global 0..1 — multiplies final color
uniform float uGlow;          // param 0..1
uniform float uReflect;       // param 0..1
uniform float uStyle;         // 0 = bars, 1 = radial
uniform float uKick;          // bass attack envelope 0..1
uniform float uPulse;         // eased overall audio intensity 0..1
uniform vec3  uDominant;      // dominant screen color 0..1

varying vec2 vUv;

float hash12(vec2 p) {
  vec3 p3 = fract(vec3(p.xyx) * 0.1031);
  p3 += dot(p3, p3.yzx + 33.33);
  return fract((p3.x + p3.y) * p3.z);
}

vec2 barData(float idx) {
  return texture2D(uBars, vec2((idx + 0.5) / uBarCount, 0.5)).rg;
}

vec3 gradColor(float f) {
  return texture2D(uGrad, vec2(f, 0.5)).rgb;
}

// Accumulated color of the spectrum at point pf (x along the strip, y above the
// baseline, isotropic units). Evaluates the nearest bar and NEIGHBORS on each
// side so glow bleeds across bars. wrapped > 0.5 => indices wrap (radial mode).
vec3 spectrumField(vec2 pf, float x0, float cellW, float soften, float wrapped) {
  vec3 acc = vec3(0.0);
  float n = uBarCount;
  float halfW = cellW * 0.33;
  float aa = soften * 1.6 / uRes.y;
  float fi = floor((pf.x - x0) / cellW);
  float widen = clamp(uGlow * (1.0 + uKick * 0.7), 0.0, 1.2);
  float glowK = mix(80.0, 15.0, widen / 1.2);
  float amp = mix(0.2, 1.0, uIntensity);

  for (int j = -NEIGHBORS; j <= NEIGHBORS; j++) {
    float idx = fi + float(j);
    float idxW = idx;
    if (wrapped > 0.5) idxW = mod(idx, n);
    if (idxW < 0.0 || idxW > n - 0.5) continue;

    vec2 data = barData(idxW);
    float fNorm = (idxW + 0.5) / n;
    // Idle "breathing" baseline so silence still looks alive (pure uTime).
    float breathe = 0.030 + 0.018 * sin(uTime * 1.25 + idxW * 0.63 + sin(uTime * 0.41 + idxW * 0.17) * 2.0);
    float v = max(data.r * amp, breathe);
    float h = v * MAXH;
    float cx = x0 + (idx + 0.5) * cellW;
    vec2 lp = vec2(pf.x - cx, pf.y);

    // Capsule SDF: flat through the baseline, rounded at the top (radius = halfW).
    vec2 a = vec2(0.0, -halfW);
    vec2 b = vec2(0.0, max(h - halfW, -halfW + 1e-5));
    vec2 pa = lp - a;
    vec2 ba = b - a;
    float t = clamp(dot(pa, ba) / max(dot(ba, ba), 1e-8), 0.0, 1.0);
    float d = length(pa - ba * t) - halfW;

    vec3 col = gradColor(fNorm);
    float core = smoothstep(aa, -aa, d);
    // Glassy body: brighter toward the tip + a soft center sheen + rim light.
    float insideY = clamp(pf.y / max(h, 1e-4), 0.0, 1.0);
    vec3 body = col * mix(0.42, 1.25, insideY * insideY);
    body += col * 0.22 * (1.0 - smoothstep(0.0, halfW, abs(lp.x)));
    float rim = smoothstep(aa * 3.0, 0.0, abs(d)) * 0.20;
    // Distance-falloff bloom (the "fake glow" — no postprocessing).
    float glow = exp(-max(d, 0.0) * glowK) * (0.18 + 0.85 * uGlow) * (0.35 + 0.65 * v);
    acc += core * body + (rim + glow) * col;

    // Peak-hold cap (thin rounded tick that decays on the CPU).
    float capLevel = min(data.g * amp, 1.0) * MAXH;
    float capOn = smoothstep(0.035, 0.07, data.g);
    vec2 cq = abs(vec2(lp.x, pf.y - capLevel)) - vec2(halfW * 0.85, 0.0030);
    float dc = length(max(cq, 0.0)) + min(max(cq.x, cq.y), 0.0) - 0.0026;
    float cap = smoothstep(aa, -aa, dc) * capOn;
    acc += cap * (col * 0.45 + vec3(0.50, 0.52, 0.56));
    acc += exp(-max(dc, 0.0) * 95.0) * col * (0.20 * uGlow) * capOn;
  }
  return acc;
}

vec3 renderBars(vec2 p) {
  float x0 = uAspect * 0.05;
  float cellW = (uAspect * 0.90) / uBarCount;
  vec2 pf = vec2(p.x, p.y - BASELINE);
  vec3 col;
  if (pf.y >= 0.0) {
    col = spectrumField(pf, x0, cellW, 1.0, 0.0);
  } else {
    // Mirrored reflection: vertical flip, slight stretch, softened AA = blur feel.
    vec2 pm = vec2(pf.x, -pf.y * 1.12);
    float fade = exp(pf.y * 7.5);
    col = spectrumField(pm, x0, cellW, 3.4, 0.0) * (uReflect * 0.55 * fade);
  }
  // Baseline hairline pulsing gently with the music.
  col += uDominant * exp(-abs(pf.y) * 240.0) * (0.10 + 0.25 * uPulse);
  // Background: near-black vertical vignette + a whisper of dominant at the horizon.
  float horizon = exp(-abs(pf.y) * 7.0);
  float vig = 1.0 - 0.55 * pow(abs(vUv.y - 0.5) * 2.0, 1.7);
  col += (uDominant * (0.035 * horizon) + vec3(0.0045, 0.005, 0.0075)) * vig;
  return col;
}

vec3 renderRadial(vec2 p) {
  vec2 q = p - vec2(uAspect * 0.5, 0.5);
  float r = length(q);
  float ang = atan(q.y, q.x);
  float f = ang / TAU + 0.5;
  float pulse = uPulse * (0.25 + 0.75 * uIntensity);
  float R0 = 0.145 + 0.045 * pulse + 0.018 * uKick * uIntensity + 0.004 * sin(uTime * 0.8);
  float circ = TAU * R0;
  float cellW = circ / uBarCount;
  vec2 pf = vec2(f * circ, r - R0);
  vec3 col;
  if (pf.y >= 0.0) {
    col = spectrumField(pf, 0.0, cellW, 1.0, 1.0);
  } else {
    // Faint mirrored spectrum inside the circle.
    vec2 pm = vec2(pf.x, -pf.y * 1.3);
    float fade = exp(pf.y * 15.0);
    col = spectrumField(pm, 0.0, cellW, 3.4, 1.0) * (uReflect * 0.5 * fade);
  }
  // The pulsing center ring.
  float aa = 1.6 / uRes.y;
  float dRing = abs(r - R0) - 0.0032;
  vec3 ringCol = mix(uDominant, vec3(1.0), 0.35);
  col += smoothstep(aa, -aa, dRing) * ringCol * (0.35 + 0.65 * uPulse);
  col += exp(-max(dRing, 0.0) * 55.0) * uDominant * (0.15 + 0.35 * uGlow) * (0.35 + 0.65 * uPulse);
  col += uDominant * 0.05 * smoothstep(R0, 0.0, r) * (0.4 + 0.6 * uPulse);
  // Near-black radial vignette floor.
  col += vec3(0.004, 0.0045, 0.007) * (1.0 - smoothstep(0.2, 0.9, r));
  return col;
}

void main() {
  vec2 p = vec2(vUv.x * uAspect, vUv.y);
  // Bass thump: the whole field scales ~2% on kick, scaled by global intensity.
  float s = 1.0 + uKick * 0.02 * uIntensity;
  vec2 c = uStyle < 0.5 ? vec2(uAspect * 0.5, BASELINE) : vec2(uAspect * 0.5, 0.5);
  p = (p - c) / s + c;

  vec3 col;
  if (uStyle < 0.5) col = renderBars(p);
  else col = renderRadial(p);

  // Tiny temporal hash dither against banding in the dark gradients.
  col += (hash12(gl_FragCoord.xy + vec2(fract(uTime * 0.37) * 61.0)) - 0.5) * (1.6 / 255.0);
  gl_FragColor = vec4(max(col, vec3(0.0)) * uBrightness, 1.0);
}
`;
