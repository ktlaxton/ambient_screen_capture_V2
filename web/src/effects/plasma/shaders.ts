// GLSL for the Plasma Flow effect: iq-style double domain-warped value-noise
// fbm (q/r warp), mapped through a 256x1 palette LUT built from the screen's
// edge colors. GLSL1-style source — three injects precision + built-in
// attributes/uniforms; OCTAVES comes from material.defines (5 full, 3 preview).

export const VERTEX_SHADER = /* glsl */ `
varying vec2 vUv;

void main() {
  vUv = position.xy * 0.5 + 0.5;
  gl_Position = vec4(position.xy, 0.0, 1.0);
}
`;

export const FRAGMENT_SHADER = /* glsl */ `
uniform vec2 uResolution;
uniform float uTime;        // wall-clock seconds — keeps breathing/dither alive even if frames stop
uniform float uFlow;        // CPU-accumulated flow time (speed already audio/intensity modulated)
uniform float uScale;       // field scale (param, eased)
uniform float uWarp;        // warp amplitude (param, eased, bass-boosted)
uniform float uShimmer;     // treble-driven fine-grain shimmer 0..1
uniform float uBrightness;  // global brightness, multiplies FINAL color
uniform sampler2D uPalette; // 256x1 gradient from screen edge colors

varying vec2 vUv;

// Dave Hoskins hash-without-sine: stable on ANGLE/D3D11, no precision cliffs.
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

float fbm(vec2 p) {
  float v = 0.0;
  float a = 0.5;
  mat2 rot = mat2(0.8, 0.6, -0.6, 0.8); // rotate octaves to hide axis alignment
  for (int i = 0; i < OCTAVES; i++) {
    v += a * vnoise(p);
    p = rot * p * 2.03;
    a *= 0.5;
  }
  return v;
}

void main() {
  float aspect = uResolution.x / max(uResolution.y, 1.0);

  // Slow breathing of the field scale (self-animates from wall clock).
  float breathe = 1.0 + 0.045 * sin(uTime * 0.11) + 0.02 * sin(uTime * 0.047 + 1.7);
  vec2 p = (vUv - 0.5) * vec2(aspect, 1.0) * uScale * breathe;

  float t = uFlow;
  // Very slow base advection.
  p += vec2(t * 0.040, -t * 0.026);

  // iq double domain warp: f( p + warp*f( p + warp*f(p) ) ).
  vec2 q = vec2(
    fbm(p + vec2(0.0, 0.0) + t * 0.18),
    fbm(p + vec2(5.2, 1.3) - t * 0.12)
  );
  vec2 r = vec2(
    fbm(p + 2.6 * uWarp * q + vec2(1.7, 9.2) + t * 0.21),
    fbm(p + 2.6 * uWarp * q + vec2(8.3, 2.8) - t * 0.16)
  );
  float f = fbm(p + 2.2 * uWarp * r);

  // Treble shimmer: one faint fine-grain octave, fades to zero in silence.
  f += uShimmer * 0.09 * (vnoise(p * 22.0 + vec2(t * 2.3, -t * 1.7)) - 0.5);

  // Shape the scalar, then look up the screen-palette gradient.
  float v = clamp(f * 1.35 - 0.10, 0.0, 1.0);
  v = v * v * (3.0 - 2.0 * v);
  vec3 col = texture2D(uPalette, vec2(v, 0.5)).rgb;

  // Luminous variation from the warp fields — clouds glow where flow converges.
  col *= 0.72 + 0.55 * clamp(dot(r, r) * 0.55, 0.0, 1.0);

  // Deep shadows tint toward #05070d, never gray.
  vec3 shadow = vec3(0.0196, 0.0275, 0.0510);
  col = mix(shadow, col, smoothstep(0.0, 0.42, v));

  col *= uBrightness;

  // Temporal hash dither (~1 LSB) to break banding in dark gradients.
  float dn = hash12(gl_FragCoord.xy + fract(uTime) * 31.7) - 0.5;
  col += dn * (1.0 / 255.0);

  gl_FragColor = vec4(max(col, 0.0), 1.0);
}
`;
