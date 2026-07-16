// ==============================================================================
// Trame TypeScript-Client-Samples — Runner.
//
// Startet die Szenarien 1–4 gegen den laufenden Sample-Server (samples/server).
// Läuft per Node Type-Stripping (kein Build, kein tsx nötig — Node >= 22.6).
//
//   npm start                  # alle Szenarien nacheinander
//   npm run start:1            # nur Szenario 1
//   node --experimental-strip-types run.ts 4   # direkter Aufruf
//
// Voraussetzung:
//   npm install                 # einmalig (installiert lokalen trame-client + ws)
//   Der Sample-Server läuft auf https://localhost:5001
//   dotnet run --project samples/server/SampleServer.csproj
// ==============================================================================

// Der Sample-Server nutzt das ASP.NET Core HTTPS-Dev-Cert (selbstsigniert für
// "localhost"). Node verwendet einen EIGENEN CA-Store und vertraut diesem Cert
// nicht -> fetch würde mit "unable to verify the first certificate" fehlschlagen.
// Für die Dev-Samples schalten wir die TLS-Verifikation prozessweit ab. In
// Produktion niemals verwenden — dort ein echtes Zertifikat / eine eigene CA.
process.env.NODE_TLS_REJECT_UNAUTHORIZED ??= "0";

import { TrameRestClient } from "trame-client";
import { run as scenario1 } from "./01-single-call.ts";
import { run as scenario2 } from "./02-batch-parallel.ts";
import { run as scenario3 } from "./03-batch-serial.ts";
import { run as scenario4 } from "./04-dependency-chain.ts";

const baseUrl = "https://localhost:5001";
const which = process.argv[2] ?? "all";

const scenarios: Record<string, { title: string; run: (rest: TrameRestClient) => Promise<void> }> = {
  "1": { title: "01 — Single Call", run: scenario1 },
  "2": { title: "02 — Batch Parallel", run: scenario2 },
  "3": { title: "03 — Batch Serial", run: scenario3 },
  "4": { title: "04 — Dependency Chain", run: scenario4 },
};

const keys = which === "all" ? ["1", "2", "3", "4"] : [which];

for (const key of keys) {
  const s = scenarios[key];
  if (!s) {
    console.log(`Unbekanntes Szenario '${key}'. Erlaubt: 1, 2, 3, 4, all.`);
    continue;
  }
  console.log("\n" + "=".repeat(78));
  console.log(`  ${s.title}`);
  console.log("=".repeat(78));
  const rest = new TrameRestClient(baseUrl);
  try {
    await s.run(rest);
    console.log(`  -> Szenario '${key}' OK`);
  } catch (e) {
    console.log(`  -> Szenario '${key}' FEHLER: ${(e as Error).message}`);
  }
}