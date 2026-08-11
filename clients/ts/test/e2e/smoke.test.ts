import { describe, it, expect } from "vitest";
import { createClient, SleipnirCall } from "../../src/index.js";
import type { SleipnirRestClient, SleipnirWebSocketClient } from "../../src/index.js";

/**
 * End-to-End-Smoke-Test gegen die laufende Sample-App (Sleipnir).
 *
 * Opt-in: nur aktiv, wenn SLEIPNIR_E2E=1 gesetzt ist. Startet nichts selbst —
 * erwartet einen laufenden Server unter SLEIPNIR_BASE_URL (Default
 * http://localhost:5052, wie launchSettings). Node-seitiges fetch/`ws` lösen
 * CORS nicht aus (im Gegensatz zum Browser).
 *
 * Aufruf:
 *   dotnet run --project Sleipnir   # in einem separaten Terminal
 *   SLEIPNIR_E2E=1 npm run test:e2e
 */
const ENABLED = process.env.SLEIPNIR_E2E === "1";
const BASE = process.env.SLEIPNIR_BASE_URL ?? "http://localhost:5052";
const { rest, ws } = createClient(BASE);

const itIf = ENABLED ? it : it.skip;

interface AdresseX {
  Id: number;
  Name: string;
  Age: number;
  Greet: string;
}

describe("E2E gegen Sample-App", () => {
  itIf("REST: discovery listet TestService auf", async () => {
    const meta = await rest.discover();
    expect(meta.controllers.map((c) => c.name)).toContain("TestService");
  });

  itIf("REST: TestService.GetAdresse liefert die Echo-Werte", async () => {
    const a = await rest.callJson<AdresseX>("TestService", "GetAdresse", {
      id: 1,
      greet: "hi",
    });
    expect(a?.Id).toBe(1);
    expect(a?.Greet).toBe("hi");
  });

  itIf("REST: Batch mit zwei parallelen Calls", async () => {
    const req = (id: number) =>
      SleipnirCall.init("TestService", "GetAdresse")
        .with({ id, greet: "g" })
        .named(`e2e-${id}`)
        .toRequest();
    const arr = await rest.callBatch([req(1), req(2)]);
    expect(arr).toHaveLength(2);
    expect(arr.every((r) => r.isSuccess)).toBe(true);
  });

  itIf("WebSocket: TestService.GetAdresse über persistente Verbindung", async () => {
    await (ws as SleipnirWebSocketClient).connect();
    try {
      const a = await (ws as SleipnirWebSocketClient).callJson<AdresseX>(
        SleipnirCall.init("TestService", "GetAdresse").with({ id: 2, greet: "ws" }).toRequest(),
      );
      expect(a?.Id).toBe(2);
      expect(a?.Greet).toBe("ws");
    } finally {
      (ws as SleipnirWebSocketClient).close();
    }
  });
});

// Statische Referenz, damit ungenutzte Imports bei Skip kein Lint-Problem werden.
void (rest as SleipnirRestClient);
void (ws as SleipnirWebSocketClient);