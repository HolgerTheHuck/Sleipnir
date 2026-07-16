import { defineConfig } from "vitest/config";

// Unit-Tests (Node-Umgebung). E2E läuft separat via vitest.e2e.config.ts.
export default defineConfig({
  test: {
    include: ["test/unit/**/*.test.ts"],
    environment: "node",
    globals: false,
  },
});