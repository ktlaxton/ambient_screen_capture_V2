import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';
import { resolve } from 'node:path';

// Built output is served by the engine via WebView2 virtual host mapping
// (https://app.local/ -> src/Engine/wwwroot), so paths must be relative.
export default defineConfig({
  plugins: [react()],
  base: './',
  build: {
    outDir: resolve(import.meta.dirname, '../src/Engine/wwwroot'),
    emptyOutDir: true,
    rolldownOptions: {
      input: {
        control: resolve(import.meta.dirname, 'control.html'),
        effects: resolve(import.meta.dirname, 'effects.html'),
      },
    },
  },
  test: {
    environment: 'happy-dom',
  },
});
