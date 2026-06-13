// GLSL for Nebula: ridged + standard fbm domain-warped color clouds with a
// hash-grid starfield. Wispier and slower than plasma — clouds tear into
// filaments via the ridged octave, stars twinkle from wall-clock time and
// flare with treble. Colors come from the shared 256x1 palette LUT.
// GLSL1-style source; OCTAVES from material.defines (5 full, 3 preview).

export const VERTEX_SHADER = /* glsl */ `
varying vec2 vUv;

void main() {
  vUv = position.xy * 0.5 + 0.5;
  gl_Position = vec4(position.xy, 0.0, 1.0);
}
`;

export const FRAGMENT_SHADER = /* glsl */ `
uniform vec2 uResolution;
uniform float uTime;        // wall-clock seconds (twinkle/dither stay alive if frames stop)
uniform float uDrift;       // CPU-accumulated drift time (speed already audio/intensity modulated)
uniform float uScale;       // field scale (param, eased)
uniform float uStars;       // star density/brightness 0..1
uniform float uShimmer;     // treble-driven star flare 0..1
uniform float uBrightness;  // global brightness, multiplies FINAL color
uniform sampler2D uPalette; // 256x1 gradient (screen colors or fixed palette)

varying vec2 vUv;

float hash12(vec2 p) {
  vec3 p3 = fract(vec3(p.xyx) * 0.1031);
  p3 += dot(p3, p3.yzx + 33.33);
  return fract((p3.x + p3.y) * p3.z);
}

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

float fbm(vec2 p) {
  float v = 0.0;
  float a = 0.5;
  mat2 rot = mat2(0.8, 0.6, -0.6, 0.8);
  for (int i = 0; i < OCTAVES; i++) {
    v += a * vnoise(p);
    p = rot * p * 2.03;
    a *= 0.5;
  }
  return v;
}

// Ridged variant: |2n-1| inverted — produces filament tears in the clouds.
float ridged(vec2 p) {
  float v = 0.0;
  float a = 0.55;
  mat2 rot = mat2(0.8, 0.6, -0.6, 0.8);
  for (int i = 0; i < OCTAVES; i++) {
    v += a * (1.0 - abs(2.0 * vnoise(p) - 1.0));
    p = rot * p * 2.13;
    a *= 0.5;
  }
  return v;
}

void main() {
  float aspect = uResolution.x / max(uResolution.y, 1.0);
  vec2 p = (vUv - 0.5) * vec2(aspect, 1.0) * uScale;

  float t = uDrift;
  p += vec2(t * 0.020, -t * 0.013); // very slow base advection

  // Domain warp: cloud body from fbm warped by a ridged field (filaments).
  vec2 q = vec2(
    fbm(p + vec2(0.0, 0.0) + t * 0.09),
    fbm(p + vec2(4.7, 2.3) - t * 0.07)
  );
  float body = fbm(p + 1.9 * q + vec2(1.2, 8.1) + t * 0.05);
  float fil = ridged(p * 1.4 + 2.4 * q - t * 0.04);

  // Combine: soft cloud mass, brightened along filaments.
  float v = clamp(body * 1.15 - 0.12 + fil * 0.28 * body, 0.0, 1.0);
  v = v * v * (3.0 - 2.0 * v);
  vec3 col = texture2D(uPalette, vec2(v, 0.5)).rgb;

  // Filament glow lifts the palette's bright end where the field converges.
  col *= 0.62 + 0.7 * clamp(fil * fil * 0.6 + dot(q, q) * 0.3, 0.0, 1.0);

  // Deep space floor: shadows sink toward near-black blue, never gray.
  vec3 shadow = vec3(0.012, 0.016, 0.035);
  col = mix(shadow, col, smoothstep(0.0, 0.5, v));

  // Starfield: sparse hash grid; each star twinkles on its own phase and
  // flares with treble (uShimmer). Stars dim where clouds are thick.
  vec2 sp = (vUv - 0.5) * vec2(aspect, 1.0) * 42.0;
  vec2 cell = floor(sp);
  float star = hash12(cell);
  float thresh = 1.0 - 0.06 * uStars;
  if (star > thresh) {
    vec2 pos = fract(sp) - 0.5 - (vec2(hash12(cell + 7.1), hash12(cell + 13.7)) - 0.5) * 0.8;
    float d = length(pos);
    float tw = 0.55 + 0.45 * sin(uTime * (1.5 + 4.0 * hash12(cell + 3.3)) + star * 40.0);
    float bright = (star - thresh) / max(1.0 - thresh, 1e-4);
    float glow = exp(-d * d * 60.0) * bright * tw * (0.7 + 1.6 * uShimmer);
    col += vec3(0.9, 0.95, 1.0) * glow * (1.0 - 0.75 * v);
  }

  col *= uBrightness;

  float dn = hash12(gl_FragCoord.xy + fract(uTime) * 31.7) - 0.5;
  col += dn * (1.0 / 255.0);

  gl_FragColor = vec4(max(col, 0.0), 1.0);
}
`;
