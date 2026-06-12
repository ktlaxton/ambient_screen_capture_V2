// Edge Glow shaders — single-pass fullscreen triangle, no postprocessing.
// Light enters from the side facing the source monitor and diffuses across the
// window like light through frosted glass; glow is faked in-shader (no bloom).

/** Shared fullscreen-triangle vertex shader (three injects the position attribute). */
export const FULLSCREEN_VS = /* glsl */ `
varying vec2 vUv;
void main() {
  vUv = position.xy * 0.5 + 0.5;
  gl_Position = vec4(position.xy, 0.0, 1.0);
}
`;

/**
 * Fragment shader. Requires define TAPS (blur taps per side: 2 preview, 3 full).
 * Edge colors arrive as a zones x 4 RGBA DataTexture (rows: top, bottom, left,
 * right), LinearFilter for free intra-zone blending.
 */
export const EDGE_GLOW_FS = /* glsl */ `
varying vec2 vUv;

uniform sampler2D uTex;
uniform int   uMode;     // 0 = directional spill, 1 = ambient halo (all 4 sides)
uniform int   uEntry;    // directional: 0=left 1=right 2=bottom 3=top (window side light enters from)
uniform float uRow;      // directional: texture v of the source edge row to sample
uniform float uZones;    // zone count (texture width)
uniform float uReach;    // 0.2..1 — how far light penetrates
uniform float uSpread;   // 0.1..1 — wedge softness / diffusion
uniform float uColorBoost;
uniform float uAudioPulse;
uniform float uAudio;    // smoothed audio intensity 0..1
uniform float uBass;     // smoothed low-band energy 0..1
uniform float uIntensity;  // global motion/reactivity scale
uniform float uBrightness; // global final-output multiplier
uniform float uTime;     // seconds

const vec3 LUMA = vec3(0.2126, 0.7152, 0.0722);
const vec3 BASE_SRGB = vec3(0.015686, 0.023529, 0.047059); // #04060c blue-black floor

float hash12(vec2 p) {
  vec3 p3 = fract(vec3(p.xyx) * 0.1031);
  p3 += dot(p3, p3.yzx + 33.33);
  return fract((p3.x + p3.y) * p3.z);
}

float vnoise(vec2 p) {
  vec2 i = floor(p);
  vec2 f = fract(p);
  vec2 u = f * f * (3.0 - 2.0 * f);
  float a = hash12(i);
  float b = hash12(i + vec2(1.0, 0.0));
  float c = hash12(i + vec2(0.0, 1.0));
  float d = hash12(i + vec2(1.0, 1.0));
  return mix(mix(a, b, u.x), mix(c, d, u.x), u.y);
}

// Cheap gamma-2.0 decode/encode keeps additive light math in (approx) linear
// space while preserving the source sRGB color at the entry edge.
vec3 srgbToLin(vec3 c) { return c * c; }

// Saturation lift: mix toward the chroma-normalized hue at constant luminance,
// so washed-out content still glows. Gated near gray to avoid amplifying noise.
vec3 boostColor(vec3 c) {
  float l = dot(c, LUMA);
  float mn = min(min(c.r, c.g), c.b);
  vec3 chroma = c - vec3(mn);
  float cl = dot(chroma, LUMA);
  vec3 vivid = chroma * (l / max(cl, 1e-5));
  float amt = uColorBoost * smoothstep(0.0, 0.02, cl);
  return mix(c, vivid, amt);
}

// Gaussian-weighted taps along the edge row; blur radius grows with travel
// distance so adjacent zones bleed softly (frosted-glass diffusion).
vec3 sampleEdge(float s, float rowV, float blur) {
  vec3 acc = vec3(0.0);
  float wsum = 0.0;
  for (int i = -TAPS; i <= TAPS; i++) {
    float o = float(i) / float(TAPS);
    float w = exp(-2.5 * o * o);
    float ss = clamp(s + o * blur, 0.0, 1.0);
    float u = (0.5 + ss * (uZones - 1.0)) / uZones; // map onto texel centers
    acc += srgbToLin(texture2D(uTex, vec2(u, rowV)).rgb) * w;
    wsum += w;
  }
  return acc / wsum;
}

// One entry side's light field. d: 0 at the entry edge -> 1 at the far side.
// s: 0..1 along the edge (matches zone ordering in the texture row).
vec3 wedge(float d, float s, float rowV) {
  // bass expands the reach slightly (classy, not strobing)
  float reach = clamp(uReach * (1.0 + uBass * uAudioPulse * 0.35 * uIntensity), 0.08, 1.5);

  // barely-perceptible falloff drift so a static screen still feels alive
  float n = vnoise(vec2(s * 3.0 + rowV * 17.0 + uTime * 0.013, d * 2.0 - uTime * 0.041));
  float amp = mix(0.03, 0.16, uIntensity);
  float x = (d / reach) * (1.0 + (n - 0.5) * amp);

  // hybrid exponential / inverse-square falloff, windowed so it terminates
  float fall = mix(1.0 / (1.0 + 8.0 * x * x), exp(-2.8 * x), 0.55);
  fall *= 1.0 - smoothstep(0.7, 1.45, x);
  if (fall < 0.001) return vec3(0.0);

  float blur = uSpread * (0.03 + 0.30 * d);
  return boostColor(sampleEdge(s, rowV, blur)) * fall;
}

void main() {
  vec3 light = vec3(0.0);

  if (uMode == 0) {
    float d; float s;
    if (uEntry == 0)      { d = vUv.x;       s = 1.0 - vUv.y; } // from left,   zones top->bottom
    else if (uEntry == 1) { d = 1.0 - vUv.x; s = 1.0 - vUv.y; } // from right,  zones top->bottom
    else if (uEntry == 2) { d = vUv.y;       s = vUv.x;       } // from bottom, zones left->right
    else                  { d = 1.0 - vUv.y; s = vUv.x;       } // from top,    zones left->right
    light = wedge(d, s, uRow);
  } else {
    // ambient halo: all four window sides glow inward with their source edges
    // (rows: 0 top, 1 bottom, 2 left, 3 right -> v centers .125/.375/.625/.875)
    light  = wedge(1.0 - vUv.y, vUv.x,       0.125) * 0.85;
    light += wedge(vUv.y,       vUv.x,       0.375) * 0.85;
    light += wedge(vUv.x,       1.0 - vUv.y, 0.625) * 0.85;
    light += wedge(1.0 - vUv.x, 1.0 - vUv.y, 0.875) * 0.85;
  }

  // gentle luminance breathing with overall audio intensity (~5-15%)
  light *= 1.0 + uAudio * uAudioPulse * 0.28 * uIntensity;

  // hue-preserving ceiling (halo corners can sum > 1)
  float m = max(max(light.r, light.g), light.b);
  light *= 1.0 / max(1.0, m);

  // far side settles into a very dark blue-black floor — never flat black
  vec3 col = srgbToLin(BASE_SRGB) + light;
  col = sqrt(max(col, 0.0)) * uBrightness; // encode back + final brightness

  // hash dither kills banding in the dark gradient
  float t = mod(uTime, 64.0);
  float dn = hash12(gl_FragCoord.xy + vec2(t * 7.31, t * 3.17));
  col += (dn - 0.5) * (1.5 / 255.0);

  gl_FragColor = vec4(clamp(col, 0.0, 1.0), 1.0);
}
`;
