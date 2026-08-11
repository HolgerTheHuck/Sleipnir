import { defineConfig } from "vitest/config";

// E2E tests against a running Sleipnir server (Story 01).
// Opt-in: set SLEIPNIR_E2E=1, otherwise tests are skipped.
//   PowerShell:  $env:SLEIPNIR_E2E=1; npm run test:e2e
//   Bash:       SLEIPNIR_E2E=1 npm run test:e2e
// Server:       dotnet run --project stories/01-n-plus-one-screen/Story01.csproj
// Target URL:   SLEIPNIR_URL (default http://127.0.0.1:5001)
export default defineConfig({
  test: {
    include: ["test/e2e/**/*.test.ts"],
    environment: "node",
    globals: false,
    testTimeout: 30000,
  },
});