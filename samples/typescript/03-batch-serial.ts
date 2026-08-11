// ==============================================================================
// 03 — Batch Serial (TypeScript/JS-Client)
// ==============================================================================
// Mehrere Aufrufe nacheinander in einer Roundtrip (ExecutionMode.Serial). Der
// Server führt sie in Request-Reihenfolge sequenziell aus.
//
// Serial OHNE dependencyMapping/​@alias löst keine Aliase auf — es ist schlicht
// geordnete Ausführung. Wer Werte zwischen Calls weitergibt, braucht
// dependencyMapping (siehe 04).
// ==============================================================================

import { SleipnirRestClient, SleipnirCall, ExecutionMode } from "sleipnir-client";

export async function run(rest: SleipnirRestClient): Promise<void> {
  const batch = SleipnirCall.batch(
    [
      SleipnirCall.init("Customer", "GetCustomerById").with({ id: 1 }).named("a").toRequest(),
      SleipnirCall.init("Customer", "GetCustomerById").with({ id: 2 }).named("b").toRequest(),
    ],
    ExecutionMode.Serial,
  );

  const [a, b] = await rest.callBatch(batch.requests, batch.mode);

  console.log(`  [a] GetCustomerById(1) -> code ${a.code}, data=${JSON.stringify(a.data)}`);
  console.log(`  [b] GetCustomerById(2) -> code ${b.code}, data=${JSON.stringify(b.data)}`);

  // Gotcha: sobald IRGENDEIN Request ein dependencyMapping hat, schaltet der Server
  // automatisch auf topologische Batch-Ausführung und ignoriert mode. Für reine
  // Serial-Semantik ohne Aliase einfach kein dependencyMapping setzen (wie hier).
}