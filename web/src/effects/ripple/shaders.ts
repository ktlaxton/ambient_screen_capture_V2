// GLSL for Ripple: a dark water plane where audio peaks spawn expanding
// concentric rings. Ring slots ride in a vec4 array uniform (xy center,
// z wavefront radius, w amplitude); the shader sums ring crests, refracts a
// faint background gradient and maps crest energy through the palette LUT.
// RIPPLES comes from material.defines (8 full, 5 preview).

export const VERTEX_SHADER = /* glsl */ `
varying vec2 vUv;

void main() {
  vUv = position.xy * 0.5 + 0.5;
  gl_Position = vec4(position.xy, 0.0, 1.0);
}
`;

export const FRAGMENT_SHADER = /* glsl */ `
uniform vec2 uResolution;
uniform float uTime;        // wall-clock seconds (idle shimmer + dither)
uniform vec4 uRipples[RIPPLES]; // xy center (aspect units), z radius, w amplitude
uniform float uRingWidth;   // crest width 0..1
uniform float uSwell;       // smoothed loudness 0..1 (background luminance swell)
uniform float uBrightness;  // global brightness, multiplies FINAL color
uniform sampler2D uPalette; // 256x1 gradient (screen colors or fixed palette)

varying vec2 vUv;

float hash12(vec2 p) {
  vec3 p3 = fract(vec3(p.xyx) * 0.1031);
  p3 += dot(p3, p3.yzx + 33.33);
  return fract((p3.x + p3.y) * p3.z);
}

void main() {
  float aspect = uResolution.x / max(uResolution.y, 1.0);
  vec2 p = (vUv - 0.5) * vec2(aspect, 1.0);

  float width = 0.02 + 0.09 * uRingWidth;

  // Sum crest energy + a refraction-style gradient offset from every ring.
  float crest = 0.0;
  float bend = 0.0;
  for (int i = 0; i < RIPPLES; i++) {
    vec4 rp = uRipples[i];
    if (rp.w <= 0.001) continue;
    float d = length(p - rp.xy);
    float x = (d - rp.z) / width;
    float g = exp(-x * x);            // crest envelope
    float wave = g * sin(x * 3.2);    // oscillation across the crest
    crest += g * rp.w;
    bend += wave * rp.w;
  }
  crest = clamp(crest, 0.0, 1.5);

  // Idle micro-shimmer so still water never looks frozen.
  float shimmer = hash12(floor(p * 90.0) + floor(uTime * 3.0) * 0.37);
  float idle = 0.012 * shimmer;

  // Water base: palette's dark end, gently swelling with loudness; the
  // "refraction" bend shifts the palette sample so crests pull color bands.
  float v = clamp(0.12 + 0.25 * uSwell + bend * 0.35 + crest * 0.55 + idle, 0.0, 1.0);
  v = v * v * (3.0 - 2.0 * v);
  vec3 col = texture2D(uPalette, vec2(v, 0.5)).rgb;

  // Crest highlight: bright line along the wavefront.
  col += texture2D(uPalette, vec2(0.85, 0.5)).rgb * crest * 0.6;

  // Radial vignette keeps the edges calm and dark.
  float r = length(p) / max(aspect, 1.0);
  col *= 1.0 - 0.35 * smoothstep(0.55, 1.1, r);

  col *= uBrightness;

  float dn = hash12(gl_FragCoord.xy + fract(uTime) * 31.7) - 0.5;
  col += dn * (1.0 / 255.0);

  gl_FragColor = vec4(max(col, 0.0), 1.0);
}
`;
