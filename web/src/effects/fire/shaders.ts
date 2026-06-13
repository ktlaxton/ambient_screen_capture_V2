// GLSL for Fire: classic upward-scrolling fbm flames shaped by a vertical
// falloff, plus a sparse rising ember layer. Bass raises the flame height and
// heat; the palette LUT maps heat -> color (ember ramp or screen colors).
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
uniform float uTime;        // wall-clock seconds (dither/ember twinkle)
uniform float uRise;        // CPU-accumulated rise time (speed audio/intensity modulated)
uniform float uHeight;      // flame height 0..1 (param, eased, bass-boosted)
uniform float uTurbulence;  // lateral churn 0..1
uniform float uHeat;        // bass-driven heat surge 0..1 (brightens + lifts)
uniform float uEmbers;      // ember layer amount 0..1
uniform float uBrightness;  // global brightness, multiplies FINAL color
uniform sampler2D uPalette; // 256x1 heat ramp

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

void main() {
  float aspect = uResolution.x / max(uResolution.y, 1.0);
  // x in flame-field units, y 0 at the bottom.
  vec2 p = vec2(vUv.x * aspect * 3.0, vUv.y * 3.0);

  float t = uRise;

  // Lateral churn warps the column positions; stronger higher up.
  float sway = fbm(vec2(p.x * 0.6 + t * 0.15, p.y * 0.5 - t * 0.8));
  p.x += (sway - 0.5) * uTurbulence * 1.6 * vUv.y;

  // Upward-scrolling noise: two octaves of motion at different speeds.
  float n = fbm(vec2(p.x, p.y - t * 2.2));
  n = 0.65 * n + 0.35 * fbm(vec2(p.x * 1.7 + 5.2, p.y * 1.6 - t * 3.1));

  // Heat: noise minus a vertical falloff — taller flames when uHeight/uHeat rise.
  float ceilY = 0.22 + 0.78 * uHeight * (0.75 + 0.45 * uHeat);
  float falloff = vUv.y / max(ceilY, 1e-3);
  float heat = clamp(n * 1.55 - falloff * falloff * 1.05, 0.0, 1.0);
  heat = heat * heat * (3.0 - 2.0 * heat);
  heat = min(heat * (1.0 + 0.35 * uHeat), 1.0);

  vec3 col = texture2D(uPalette, vec2(heat, 0.5)).rgb;
  // The hottest core overdrives toward white for bloom headroom.
  col += vec3(1.0, 0.9, 0.6) * smoothstep(0.82, 1.0, heat) * (0.35 + 0.45 * uHeat);

  // Ember layer: sparse hash-grid sparks rising on their own clock, fading high.
  if (uEmbers > 0.001) {
    vec2 ep = vec2(vUv.x * aspect * 26.0, vUv.y * 18.0 - t * 5.5);
    vec2 cell = floor(ep);
    float e = hash12(cell);
    float thresh = 1.0 - 0.05 * uEmbers * (0.6 + 0.8 * uHeat);
    if (e > thresh) {
      vec2 pos = fract(ep) - 0.5 -
        (vec2(hash12(cell + 3.7), hash12(cell + 9.1)) - 0.5) * 0.7;
      float d = dot(pos, pos);
      float life = 1.0 - smoothstep(0.15, 0.95, vUv.y);
      float tw = 0.6 + 0.4 * sin(uTime * (3.0 + 5.0 * hash12(cell + 1.3)) + e * 50.0);
      col += vec3(1.0, 0.55, 0.18) * exp(-d * 90.0) * life * tw * 1.4;
    }
  }

  // Dark floor tint keeps the unlit area warm-black, never gray.
  vec3 shadow = vec3(0.02, 0.008, 0.004);
  col = mix(shadow, col, smoothstep(0.0, 0.25, heat + 0.08));

  col *= uBrightness;

  float dn = hash12(gl_FragCoord.xy + fract(uTime) * 31.7) - 0.5;
  col += dn * (1.0 / 255.0);

  gl_FragColor = vec4(max(col, 0.0), 1.0);
}
`;
