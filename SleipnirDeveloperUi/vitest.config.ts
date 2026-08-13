// Vitest config for the DevUI — covers ONLY the pure helper modules
// (canvasLayout/canvasGraph/canvasViewport), not the Svelte components.
// Deliberately separate from vite.config.ts so the Svelte plugin does not
// process test files. The `sleipnir-client` / `sleipnir-codegen` file: deps
// resolve via their package.json `main` (dist), same as `vite dev`.
import { defineConfig } from 'vitest/config';

export default defineConfig({
  test: {
    include: ['src/**/*.test.ts'],
    environment: 'node',
  },
});