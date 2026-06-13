// Per-effect smoke test (Story 7.1 AC: create + frame + render + param/global
// churn + dispose never throws). `three` is mocked with inert stand-ins, so
// every module's CPU-side logic runs (easing, LUT baking, onset detection,
// param clamping, color-source switching) without a WebGL context.
import { describe, it, vi } from 'vitest';
import type { FramePayload, RGB } from '../shared/bridge';

vi.mock('three', () => {
  class Vec {
    x = 0; y = 0; z = 0; w = 0;
    constructor(x = 0, y = 0, z = 0, w = 0) { this.x = x; this.y = y; this.z = z; this.w = w; }
    set(x = 0, y = 0, z = 0, w = 0) { this.x = x; this.y = y; this.z = z; this.w = w; return this; }
    lerp() { return this; }
    copy() { return this; }
  }
  class Color {
    r = 1; g = 1; b = 1;
    constructor(r = 1, g = 1, b = 1) { this.setRGB(r, g, b); }
    setRGB(r: number, g: number, b: number) { this.r = r; this.g = g; this.b = b; return this; }
    set() { return this; }
    copy(c: Color) { this.r = c.r; this.g = c.g; this.b = c.b; return this; }
    lerp() { return this; }
    offsetHSL() { return this; }
  }
  class DataTexture {
    needsUpdate = false;
    magFilter = 0; minFilter = 0; wrapS = 0; wrapT = 0; generateMipmaps = false;
    constructor(public data?: unknown) {}
    dispose() {}
  }
  class BufferAttribute { constructor(public array?: unknown, public itemSize?: number) {} }
  class BufferGeometry {
    boundingSphere: unknown = null;
    setAttribute() { return this; }
    dispose() {}
  }
  class Material {
    needsUpdate = false;
    blending = 0; blendEquation = 0; blendSrc = 0; blendDst = 0;
    dispose() {}
  }
  class ShaderMaterial extends Material {
    uniforms: Record<string, { value: unknown }>;
    constructor(opts: { uniforms?: Record<string, { value: unknown }> } = {}) {
      super();
      this.uniforms = opts.uniforms ?? {};
    }
  }
  class Object3D {
    frustumCulled = true;
    renderOrder = 0;
    position = new Vec();
    rotation = new Vec();
    add() { return this; }
  }
  class Mesh extends Object3D { constructor(public geometry?: unknown, public material?: unknown) { super(); } }
  class Points extends Mesh {}
  class Scene extends Object3D {}
  class Camera extends Object3D {
    aspect = 1;
    updateProjectionMatrix() {}
  }
  class WebGLRenderer {
    constructor(public opts?: unknown) {}
    setPixelRatio() {}
    setSize() {}
    setClearColor() {}
    render() {}
    dispose() {}
    forceContextLoss() {}
  }
  class Sphere { constructor(public center?: unknown, public radius?: number) {} }
  return {
    Vector2: Vec,
    Vector3: Vec,
    Vector4: Vec,
    Color,
    DataTexture,
    BufferAttribute,
    BufferGeometry,
    Material,
    ShaderMaterial,
    Mesh,
    Points,
    Scene,
    OrthographicCamera: Camera,
    PerspectiveCamera: Camera,
    WebGLRenderer,
    Sphere,
    RGBAFormat: 1023,
    RGFormat: 1030,
    RedFormat: 1028,
    UnsignedByteType: 1009,
    FloatType: 1015,
    LinearFilter: 1006,
    NearestFilter: 1003,
    ClampToEdgeWrapping: 1001,
    NormalBlending: 1,
    AdditiveBlending: 2,
    CustomBlending: 5,
    AddEquation: 100,
    OneFactor: 201,
    OneMinusSrcColorFactor: 203,
    IUniform: undefined,
  };
});

// Imported AFTER the three mock so every module picks up the stubs.
const { effects } = await import('./registry');
const { defaultParamsOf } = await import('./types');

function edge(n: number): RGB[] {
  return Array.from({ length: n }, (_, i) => [40 + i * 10, 80, 200 - i * 12] as RGB);
}

function makeFrame(loudBass = false): FramePayload {
  return {
    t: 1234,
    edges: { top: edge(8), bottom: edge(8), left: edge(8), right: edge(8) },
    dominant: [120, 80, 200],
    audio: {
      intensity: loudBass ? 0.9 : 0.3,
      bands: Array.from({ length: 12 }, (_, i) => (loudBass && i < 3 ? 0.95 : 0.15)),
    },
  };
}

function makeCanvas(): HTMLCanvasElement {
  const canvas = document.createElement('canvas');
  canvas.width = 640;
  canvas.height = 360;
  return canvas;
}

describe('effect smoke (every registered module, no WebGL)', () => {
  for (const module of effects) {
    it(`${module.id}: full lifecycle does not throw`, () => {
      const instance = module.create({
        canvas: makeCanvas(),
        windowConfig: null,
        preview: true,
      });

      // Quiet frame, then a loud-bass frame (exercises onset/kick paths).
      instance.onFrame(makeFrame(false));
      instance.render(16.7, 16.7);
      instance.onFrame(makeFrame(true));
      instance.render(33.4, 16.7);

      // Defaults, then flipped color source (exercises fixed-palette fills).
      instance.setParams(defaultParamsOf(module));
      instance.setParams({ ...defaultParamsOf(module), screenColors: false });
      instance.setGlobals({ intensity: 0.5, brightness: 0.7 });
      instance.render(50.1, 16.7);

      instance.resize(800, 450);
      instance.resize(0, 0); // degenerate size must not blow up
      instance.render(66.8, 16.7);

      instance.dispose();
    });

    it(`${module.id}: create with a layout-aware windowConfig does not throw`, () => {
      const instance = module.create({
        canvas: makeCanvas(),
        windowConfig: {
          monitorId: 'MON2',
          effectId: module.id,
          monitor: null,
          source: null,
          relation: 'left',
        },
        preview: false,
      });
      instance.onFrame(makeFrame(true));
      instance.render(16.7, 16.7);
      instance.dispose();
    });
  }
});
