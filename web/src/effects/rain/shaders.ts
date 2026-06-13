// GLSL for Rain: two parallax layers of falling streaks on a hash-column
// grid. Each column gets a random phase/speed/length; streaks are vertical
// capsules with a bright head and fading tail. Column x position samples the
// palette LUT so streaks pick up the screen's colors (or a fixed palette).
// GLSL1-style source; LAYERS from material.defines (2 full, 1 preview).

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
uniform float uFall;        // CPU-accumulated fall time (speed audio/intensity modulated)
uniform float uDensity;     // streak probability per column 0..1
uniform float uLength;      // streak length 0..1
uniform float uGust;        // bass-driven brightness/speed surge 0..1
uniform float uBrightness;  // global brightness, multiplies FINAL color
uniform sampler2D uPalette; // 256x1 gradient: tints streaks by column position

varying vec2 vUv;

float hash12(vec2 p) {
  vec3 p3 = fract(vec3(p.xyx) * 0.1031);
  p3 += dot(p3, p3.yzx + 33.33);
  return fract((p3.x + p3.y) * p3.z);
}

// One rain layer: columns of falling capsule streaks. scale sets column width;
// speedMul gives parallax (far layers fall slower and dimmer).
vec3 rainLayer(vec2 uv, float aspect, float scale, float speedMul, float layerSeed) {
  float cols = 90.0 * scale;
  float x = uv.x * aspect * cols;
  float col = floor(x);
  float fx = fract(x);

  float h1 = hash12(vec2(col, layerSeed));
  float h2 = hash12(vec2(col, layerSeed + 37.0));
  float h3 = hash12(vec2(col, layerSeed + 91.0));

  // Density gate: a fraction of columns are active, re-rolled per column.
  if (h1 > uDensity) return vec3(0.0);

  float speed = (0.5 + 0.9 * h2) * speedMul;
  float len = 0.06 + 0.5 * uLength * (0.4 + 0.6 * h3);

  // Falling phase: head position wraps vertically; tail trails above the head.
  float y = fract(uv.y * 0.85 + uFall * speed + h2 * 7.31);
  float dist = y; // distance below the head (head at y==0 after fract)
  float tail = exp(-dist / max(len, 1e-3));
  // Streak core: thin in x, capsule profile.
  float core = exp(-fx * fx * 30.0 - (1.0 - fx) * (1.0 - fx) * 30.0);
  core = exp(-pow(abs(fx - 0.5) * 6.0, 2.0));
  float head = smoothstep(0.985, 1.0, 1.0 - dist) * 1.6; // bright head spark

  vec3 tint = texture2D(uPalette, vec2(fract(col / cols + 0.5 / cols), 0.5)).rgb;
  float lum = (tail * 0.8 + head) * core * speedMul;
  return tint * lum;
}

void main() {
  float aspect = uResolution.x / max(uResolution.y, 1.0);

  // Near-black backdrop with a faint vertical wash from the palette mid tone.
  vec3 wash = texture2D(uPalette, vec2(0.5, 0.5)).rgb;
  vec3 col = wash * 0.05 * (1.0 - vUv.y * 0.6);

  float gain = 0.75 + 0.65 * uGust;
  #if LAYERS >= 2
  col += rainLayer(vUv, aspect, 1.6, 0.55, 11.0) * 0.45 * gain; // far, slow, dim
  #endif
  col += rainLayer(vUv, aspect, 1.0, 1.0, 3.0) * gain;          // near, fast, bright

  col *= uBrightness;

  float dn = hash12(gl_FragCoord.xy + fract(uTime) * 31.7) - 0.5;
  col += dn * (1.0 / 255.0);

  gl_FragColor = vec4(max(col, 0.0), 1.0);
}
`;
