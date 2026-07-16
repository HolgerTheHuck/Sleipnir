// ==============================================================================
// 02 — Batch Parallel (TypeScript/JS-Client)
// ==============================================================================
// Mehrere UNABHÄNGIGE Aufrufe in einer Roundtrip. Der Server führt sie per
// Task.WhenAll concurrently aus (ExecutionMode.Parallel). Ideal, um Latenz zu
// amortisieren, wenn Calls einander nicht brauchen.
//
// Wichtig: Parallel löst KEINE @alias-Abhängigkeiten auf — dafür siehe 03/04.
// ==============================================================================

import { TrameRestClient, TrameCall, ExecutionMode } from "trame-client";

type Customer = { id: number; name: string; email: string };

export async function run(rest: TrameRestClient): Promise<void> {
  // Erst einen Kunden garantieren, damit GetCustomerById Treffer hat.
  await rest.callJson<number>("Customer", "AddCustomer", {
    name: "Bob",
    email: "bob@x.com",
  });

  // TrameCall.batch(requests, mode) — Default mode ist Serial; hier explizit Parallel.
  const batch = TrameCall.batch(
    [
      // .named(id) setzt die Korrelations-Id (wichtig bei konkurrierenden Batches
      // über WebSocket, wo Responses an requests[0].id korrelieren).
      TrameCall.init("Customer", "GetAllCustomers").named("all").toRequest(),
      TrameCall.init("Customer", "GetCustomerById").with({ id: 1 }).named("c1").toRequest(),
      TrameCall.init("Customer", "GetCustomerById").with({ id: 2 }).named("c2").toRequest(),
    ],
    ExecutionMode.Parallel,
  );

  // callBatch liefert die Responses in Request-Reihenfolge. Seit dem Single-Pass-
  // Fix ist `data` ein strukturierter Wert (kein JSON-String mehr) — kein Parse nötig.
  const responses = await rest.callBatch(batch.requests, batch.mode);
  const [all, c1, c2] = responses;

  const list = all.data as Customer[] | null;
  const cust1 = c1.data as Customer | null;
  const cust2 = c2.data as Customer | null;

  console.log(`  [all] GetAllCustomers -> ${list?.length ?? 0} Kunde(n)`);
  console.log(`  [c1]  GetCustomerById(1) -> ${cust1?.name ?? "<fehl>"} (code ${c1.code})`);
  console.log(`  [c2]  GetCustomerById(2) -> ${cust2?.name ?? "<fehl>"} (code ${c2.code})`);
}