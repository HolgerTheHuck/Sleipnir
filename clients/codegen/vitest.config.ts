import { defineConfig } from "vitest/config";

// Unit tests (Node). E2E runs separately via vitest.e2e.config.ts.
export default defineConfig({
  test: {
    include: ["test/unit/**/*.test.ts"],
    environment: "node",
    globals: false,
  },
});