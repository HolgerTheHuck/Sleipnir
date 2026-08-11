import { defineConfig } from "vitest/config";

// E2E-Tests gegen eine laufende Sleipnir-Server-Instanz.
// Opt-in: SLEIPNIR_E2E=1 setzen, sonst werden die Tests übersprungen.
//   PowerShell:  $env:SLEIPNIR_E2E=1; npm run test:e2e
//   Bash:        SLEIPNIR_E2E=1 npm run test:e2e
// Server z.B.:   dotnet run --project Sleipnir
// Ziel-URL:      SLEIPNIR_URL (Default http://127.0.0.1:5001)
export default defineConfig({
  test: {
    include: ["test/e2e/**/*.test.ts"],
    environment: "node",
    globals: false,
    testTimeout: 20000,
  },
});