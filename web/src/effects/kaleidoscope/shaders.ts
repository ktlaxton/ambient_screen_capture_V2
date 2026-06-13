// GLSL for Kaleidoscope: N-fold polar mirror of a domain-warped fbm pattern
// mapped through the palette LUT. The fold count is a float uniform (snapped
// CPU-side); spin/zoom accumulate on the CPU so audio can modulate speed.
// GLSL1-style source; OCTAVES from material.defines (4 full, 3 preview).

export const VERTEX_SHADER = /* glsl */ `
varying vec2 vUv;

void main() {
  vUv = position.xy * 0.5 + 0.5;
  gl_Position = vec4(position.xy, 0.0, 1.0);
}
`;

export const FRAGMENT_SHADER = /* glsl */ `
uniform vec2 uResolution;
uniform float uTime;        // wall-clock seconds (dither)
uniform float uSpin;        // CPU-accumulated rotation (audio/intensity modulated)
uniform float uFlow;        // CPU-accumulated pattern flow time
uniform float uSegments;    // fold count (3..12, snapped on the CPU)
uniform float uZoom;        // zoom factor (eased; bass pumps it)
uniform float uPulse;       // bass pulse 0..1 — brightens the mandala core
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

void main() {
  float aspect = uResolution.x / max(uResolution.y, 1.0);
  vec2 p = (vUv - 0.5) * vec2(aspect, 1.0);

  // Polar fold: mirror the angle into one segment wedge -> N-fold symmetry.
  float r = length(p);
  float theta = atan(p.y, p.x) + uSpin;
  float wedge = 6.2831853 / max(uSegments, 2.0);
  theta = mod(theta, wedge);
  theta = abs(theta - wedge * 0.5);

  vec2 q = vec2(cos(theta), sin(theta)) * r * (1.2 + uZoom);

  // Domain-warped pattern inside the wedge; flows on its own clock.
  float t = uFlow;
  vec2 w = vec2(
    fbm(q * 2.2 + vec2(0.0, t * 0.31)),
    fbm(q * 2.2 + vec2(4.3, -t * 0.23))
  );
  float v = fbm(q * 2.6 + 1.7 * w + vec2(t * 0.12, 0.0));

  // Ring modulation gives concentric mandala structure.
  v += 0.18 * sin(r * (9.0 + 5.0 * uZoom) - t * 0.9);
  v = clamp(v * 1.3 - 0.1, 0.0, 1.0);
  v = v * v * (3.0 - 2.0 * v);

  vec3 col = texture2D(uPalette, vec2(v, 0.5)).rgb;

  // Core glow pumps with bass; edges vignette gently.
  float core = exp(-r * r * 2.2) * (0.25 + 0.9 * uPulse);
  col += col * core;
  col *= 1.0 - smoothstep(0.75, 1.25, r) * 0.55;

  // Shadow floor: deep violet-black, never gray.
  vec3 shadow = vec3(0.015, 0.012, 0.03);
  col = mix(shadow, col, smoothstep(0.0, 0.4, v + core * 0.3));

  col *= uBrightness;

  float dn = hash12(gl_FragCoord.xy + fract(uTime) * 31.7) - 0.5;
  col += dn * (1.0 / 255.0);

  gl_FragColor = vec4(max(col, 0.0), 1.0);
}
`;
