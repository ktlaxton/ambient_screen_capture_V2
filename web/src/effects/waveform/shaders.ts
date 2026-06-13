// GLSL for Waveform: a glowing oscilloscope line. The curve rides in a 64x1
// R-float texture (CPU keeps attack/release smoothing); the fragment shader
// shades by vertical distance to the curve — sharp core + wide glow halo —
// with an optional mirrored ghost line and a faint center baseline.

export const VERTEX_SHADER = /* glsl */ `
varying vec2 vUv;

void main() {
  vUv = position.xy * 0.5 + 0.5;
  gl_Position = vec4(position.xy, 0.0, 1.0);
}
`;

export const FRAGMENT_SHADER = /* glsl */ `
uniform vec2 uResolution;
uniform float uTime;       // wall-clock seconds (dither + idle ripple)
uniform sampler2D uCurve;  // 64x1 R float, 0..1 normalized curve heights
uniform vec3 uColor;       // line color (fixed or eased screen dominant)
uniform float uThickness;  // core thickness 0..1
uniform float uGlow;       // halo strength 0..1
uniform float uAmp;        // amplitude 0..1 (audio/global-intensity scaled)
uniform float uMirror;     // 1 = mirrored ghost below
uniform float uBrightness; // global brightness, multiplies FINAL color

varying vec2 vUv;

float hash12(vec2 p) {
  vec3 p3 = fract(vec3(p.xyx) * 0.1031);
  p3 += dot(p3, p3.yzx + 33.33);
  return fract((p3.x + p3.y) * p3.z);
}

float curveAt(float x) {
  float v = texture2D(uCurve, vec2(x, 0.5)).r;
  // Idle ripple keeps the line alive in silence (tiny, wall-clock driven).
  v += 0.012 * sin(x * 21.0 + uTime * 1.3) * (1.0 - v);
  return v;
}

void main() {
  float aspect = uResolution.x / max(uResolution.y, 1.0);

  float c = curveAt(vUv.x);
  float yLine = 0.5 + (c - 0.5) * uAmp * 0.9;

  float d = abs(vUv.y - yLine);
  float core = exp(-pow(d / max(0.004 + 0.018 * uThickness, 1e-4), 2.0));
  float halo = exp(-d / max(0.02 + 0.16 * uGlow, 1e-3)) * (0.25 + 0.75 * uGlow);

  vec3 col = uColor * (core * 1.5 + halo * 0.8);

  // Mirrored ghost line (reflection style).
  if (uMirror > 0.5) {
    float yGhost = 0.5 - (c - 0.5) * uAmp * 0.9;
    float dg = abs(vUv.y - yGhost);
    float gcore = exp(-pow(dg / max(0.004 + 0.018 * uThickness, 1e-4), 2.0));
    float ghalo = exp(-dg / max(0.02 + 0.16 * uGlow, 1e-3)) * (0.25 + 0.75 * uGlow);
    col += uColor * (gcore * 0.5 + ghalo * 0.3);
  }

  // Faint baseline anchors the scope when the signal is quiet.
  float db = abs(vUv.y - 0.5);
  col += uColor * exp(-db / 0.0035) * 0.06;

  col *= uBrightness;

  float dn = hash12(gl_FragCoord.xy + fract(uTime) * 31.7) - 0.5;
  col += dn * (1.0 / 255.0);

  gl_FragColor = vec4(max(col, 0.0), 1.0);
}
`;
