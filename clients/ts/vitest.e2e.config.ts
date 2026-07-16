import { defineConfig } from "vitest/config";

// E2E-Tests gegen eine laufende Trame-Server-Instanz.
// Opt-in: TRAME_E2E=1 setzen, sonst werden die Tests übersprungen.
//   PowerShell:  $env:TRAME_E2E=1; npm run test:e2e
//   Bash:        TRAME_E2E=1 npm run test:e2e
// Server z.B.:   dotnet run --project Trame
// Ziel-URL:      TRAME_URL (Default http://127.0.0.1:5001)
export default defineConfig({
  test: {
    include: ["test/e2e/**/*.test.ts"],
    environment: "node",
    globals: false,
    testTimeout: 20000,
  },
});