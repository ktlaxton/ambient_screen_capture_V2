// GLSL for the Aurora effect: 2-6 procedural light curtains over a night sky.
// Each curtain is a horizontal lower-border path (fbm-drifted), with a sharp
// smoothstep base that fades exponentially upward, and high-frequency vertical
// "ray" filaments — the look of real aurora photography, not horizontal sines.
// GLSL1-style source — three injects precision + built-in attributes/uniforms
// and rewrites texture2D for WebGL2. PATH_OCTAVES/DETAIL come from
// material.defines (4/1 fullscreen, 2/0 preview).

export const VERTEX_SHADER = /* glsl */ `
varying vec2 vUv;

void main() {
  vUv = position.xy * 0.5 + 0.5;
  gl_Position = vec4(position.xy, 0.0, 1.0);
}
`;

export const FRAGMENT_SHADER = /* glsl */ `
uniform vec2 uResolution;
uniform float uTime;        // wall-clock seconds — twinkle/dither/idle motion never stop
uniform float uDrift;       // CPU-accumulated curtain motion time (intensity/bass modulated)
uniform int uRibbons;       // active curtain count (2..6)
uniform float uSway;        // effective sway amplitude (param * intensity * bass boost)
uniform float uTint;        // 0 = pure screen palette, 1 = classic aurora green/violet
uniform float uShimmer;     // treble-driven filament shimmer 0..1
uniform float uSurge;       // bass brightness surge strength 0..1
uniform float uSurgePhase;  // traveling position of the surge along the curtains
uniform float uBrightness;  // global brightness, multiplies FINAL color
uniform sampler2D uEdge;    // zonesPerEdge x 1 palette from the relevant source edge
uniform vec3 uDominant;     // eased dominant screen color 0..1

varying vec2 vUv;

#define MAX_RIBBONS 6

// Classic aurora palette (sRGB 0..1): teal-green #36e8a0, violet #7a4fd8.
#define AURORA_GREEN vec3(0.212, 0.910, 0.627)
#define AURORA_VIOLET vec3(0.478, 0.310, 0.847)

// Dave Hoskins hashes-without-sine: stable on ANGLE/D3D11.
float hash11(float p) {
  p = fract(p * 0.1031);
  p *= p + 33.33;
  p *= p + p;
  return fract(p);
}

float hash12(vec2 p) {
  vec3 p3 = fract(vec3(p.xyx) * 0.1031);
  p3 += dot(p3, p3.yzx + 33.33);
  return fract((p3.x + p3.y) * p3.z);
}

// Value noise with quintic interpolation (C2-continuous, no grid ridges).
float vnoise(vec2 p) {
  vec2 i = floor(p);
  vec2 f = fract(p);
  vec2 u = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);
  float a = hash12(i);
  float b = hash12(i + vec2(1.0, 0.0));
  float c = hash12(i + vec2(0.0, 1.0));
  float d = hash12(i + vec2(1.0, 1.0));
  return mix(mix(a, b, u.x), mix(c, d, u.x), u.y);
}

// fbm for the curtain border path (1D path sampled through 2D noise: x = along
// screen, y = slow time, so the border ripples and travels like a real curtain).
float fbm(vec2 p) {
  float v = 0.0;
  float a = 0.5;
  mat2 rot = mat2(0.8, 0.6, -0.6, 0.8);
  for (int i = 0; i < PATH_OCTAVES; i++) {
    v += a * vnoise(p);
    p = rot * p * 2.07 + 11.3;
    a *= 0.5;
  }
  return v;
}

void main() {
  float aspect = uResolution.x / max(uResolution.y, 1.0);
  float x = vUv.x * aspect; // aspect-corrected horizontal coordinate

  // --- Night sky: deep gradient (#020308 zenith -> #0a0f1e horizon) ---
  vec3 col = mix(
    vec3(0.0392, 0.0588, 0.1176),
    vec3(0.0078, 0.0118, 0.0314),
    smoothstep(0.0, 0.75, vUv.y)
  );

  // --- Ultra-dim starfield: hashed cells, very slow twinkle, upper sky only ---
  vec2 sg = vec2(x, vUv.y) * 70.0;
  vec2 cell = floor(sg);
  float sh = hash12(cell);
  if (sh > 0.978) {
    vec2 sp = vec2(hash12(cell + 7.1), hash12(cell + 13.7)) * 0.8 + 0.1;
    float sd = length(fract(sg) - sp);
    float tw = 0.6 + 0.4 * sin(uTime * (0.22 + sh * 0.5) + sh * 61.0);
    float star = smoothstep(0.10, 0.0, sd) * tw * smoothstep(0.22, 0.65, vUv.y);
    col += vec3(0.55, 0.65, 0.85) * star * 0.06;
  }

  // --- Curtains, additively layered ---
  vec3 accum = vec3(0.0);
  float n = float(uRibbons);
  for (int i = 0; i < MAX_RIBBONS; i++) {
    if (i >= uRibbons) break;
    float fi = float(i);
    float seed = fi * 17.39 + 3.1;

    // Lower-border path: stacked base heights + fbm drift. Sway controls the
    // ripple amplitude; an always-on wall-clock term keeps an idle breath.
    float baseY = mix(0.14, 0.52, (fi + 0.5) / n) + (hash11(seed) - 0.5) * 0.10;
    vec2 pp = vec2(
      x * (1.05 + 0.30 * hash11(seed + 4.2)) + seed,
      uDrift * 0.32 + seed * 1.7
    );
    float path = baseY
      + (fbm(pp) - 0.5) * (0.07 + 0.30 * uSway)
      + 0.012 * sin(uTime * 0.05 + seed * 2.7);

    float dy = vUv.y - path;
    if (dy < -0.10) continue; // pixel is well below this curtain's border

    // Vertical luminance profile: sharp lower border, long exponential fade
    // upward, plus a faint under-glow hugging the border from below.
    float h = 0.26 + 0.16 * hash11(seed + 8.8);
    float lower = smoothstep(-0.016, 0.014, dy);
    float lum = lower * exp(-max(dy, 0.0) / h)
              + (1.0 - lower) * 0.18 * exp(min(dy, 0.0) * 60.0);

    // Ray filaments: high-frequency noise along the curtain, slightly sheared
    // with height so the rays lean like real aurora; squared to carve distinct
    // bright columns out of darkness.
    float rc = x * (22.0 + 6.0 * hash11(seed + 2.2)) + seed * 31.0 + dy * 3.0;
    float rn = vnoise(vec2(rc, uDrift * (1.3 + 0.5 * hash11(seed + 6.6)) + seed));
    float rays = mix(0.45, 1.55, rn * rn);
#if DETAIL
    rays *= 1.0 + 0.35 * (vnoise(vec2(rc * 2.7, uDrift * 2.1 + seed * 1.3)) - 0.5);
#endif
    // Treble shimmer: fast fine-grain flicker, fades to zero in silence.
    rays *= 1.0 + uShimmer * 1.6 * (vnoise(vec2(rc * 3.3, uTime * 5.0 + seed)) - 0.5);

    // Bass surge: a bright pulse traveling along the curtain. (Squared by
    // hand — pow() with a negative base is undefined in GLSL.)
    float sd2 = (fract(vUv.x * 0.5 + fi * 0.31 - uSurgePhase * (0.8 + 0.3 * hash11(seed + 7.7))) - 0.5) * 3.6;
    float surge = uSurge * exp(-sd2 * sd2);

    float amp = 0.55 + 0.45 * hash11(seed + 12.0);
    float strength = lum * rays * amp * (1.0 + 1.5 * surge);

    // Color: screen edge palette sampled along x at the base, fading toward a
    // cooler tip; uTint crossfades the whole gradient to classic aurora colors.
    float px = clamp(vUv.x + (hash11(seed + 9.4) - 0.5) * 0.18, 0.0, 1.0);
    vec3 screenCol = texture2D(uEdge, vec2(px, 0.5)).rgb;
    vec3 screenTip = mix(screenCol, uDominant, 0.5) * vec3(0.55, 0.65, 0.95);
    vec3 classicBase = AURORA_GREEN * (0.85 + 0.30 * hash11(seed + 1.3));
    vec3 classicTip = mix(AURORA_VIOLET, AURORA_GREEN, 0.30 * hash11(seed + 5.5));
    float vg = clamp(dy / (h * 1.5), 0.0, 1.0);
    vec3 curtain = mix(
      mix(screenCol, classicBase, uTint),
      mix(screenTip, classicTip, uTint),
      vg
    );

    accum += curtain * strength;
  }

  // Soft-knee compression of the additive stack — fakes bloom around bright
  // rays without any postprocessing pass.
  col += vec3(1.0) - exp(-accum * 1.1);

  col *= uBrightness;

  // Temporal hash dither (~1 LSB) to break banding in the dark sky.
  float dn = hash12(gl_FragCoord.xy + fract(uTime) * 29.3) - 0.5;
  col += dn * (1.0 / 255.0);

  gl_FragColor = vec4(max(col, 0.0), 1.0);
}
`;
