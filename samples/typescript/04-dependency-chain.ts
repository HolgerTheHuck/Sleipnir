// ==============================================================================
// 04 — Dependency Chaining (TypeScript/JS-Client)
// ==============================================================================
// Mehrere Aufrufe in EINER Roundtrip, wobei spätere Aufrufe Werte aus früheren
// nutzen — ohne Client-seitiges Zusammenfügen.
//
// Mechanik:
//   • Request A deklariert dependencyMapping: { alias: "$.JsonPath" }.
//     JsonPath ist ergebnisrelativ: "$" = gesamte serialisierte Rückgabe;
//     "$.Id" = Eigenschaft; "$[0].Id" = erstes Listenelement. KEIN "$.data"-Envelope.
//   • Der Server extrahiert den Wert → exposedDependencies.
//   • Request B nutzt "@alias" als Parameterwert (data-String mit @-Präfix).
//     Der Server ersetzt es vor der Ausführung.
//   • mode = Serial (sobald ein dependencyMapping existiert, schaltet der Server
//     ohnehin auf topologische Batch-Ausführung — mode wird dann ignoriert).
// ==============================================================================

import { SleipnirRestClient, SleipnirCall, ExecutionMode, type SleipnirRequest } from "sleipnir-client";

type Customer = { id: number; name: string; email: string };
type Order = { id: number; customerId: number; total: number; createdAt: string };

export async function run(rest: SleipnirRestClient): Promise<void> {
  // -----------------------------------------------------------------------------
  // Variante A — Fluent Builder (einfach, 2-Step, Ein-Parameter-@alias)
  // -----------------------------------------------------------------------------
  // AddCustomer → liefert neue Id (int) → weiter als @newId an GetCustomerById.
  const batch = SleipnirCall.batch(
    [
      SleipnirCall.init("Customer", "AddCustomer")
        .named("step1")
        .with({ name: "Carol", email: "carol@x.com" })
        .exposes("$", "newId") // ganzer int-Rückgabewert → Alias "newId"
        .toRequest(),

      SleipnirCall.init("Customer", "GetCustomerById")
        .named("step2")
        .withAlias("@newId") // data: "@newId", parameterName: "newId"
        .toRequest(),
      // Hinweis: withAlias setzt parameterName auf den Alias-Namen ("newId").
      // GetCustomerById hat nur EINEN echten Parameter ("id") → Server bindet
      // positional (num=0). Bei MEHRPARAMETRIGEN Methoden mit @alias siehe Variante B.
    ],
    ExecutionMode.Serial,
  );

  const [first, second] = await rest.callBatch(batch.requests, batch.mode);
  const newId = first.data as number;
  const chainedCustomer = second.data as Customer;
  console.log(`  [A] AddCustomer -> Id ${newId}; GetCustomerById(@newId) -> ${chainedCustomer?.name}`);

  // -----------------------------------------------------------------------------
  // Variante B — Raw-Form (robust, 3-Step, mehrparametrige @alias-Bindung)
  // -----------------------------------------------------------------------------
  // AddCustomer → @custId → CreateOrder(customerId=@custId, total=99.90) → @orderId
  // → GetOrder(@orderId). CreateOrder hat ZWEI Parameter, davon einer @alias —
  // deshalb setzen wir parameterName auf den echten Parameternamen ("customerId"),
  // damit der Server nach Name bindet (sicherer als positional).
  const chain: SleipnirRequest[] = [
    {
      controller: "Customer",
      method: "AddCustomer",
      id: "step1",
      params: [
        { parameterName: "name", num: 0, data: "Dave" },
        { parameterName: "email", num: 1, data: "dave@x.com" },
      ],
      dependencyMapping: { custId: "$" }, // neue CustomerId weitergeben
    },
    {
      controller: "Order",
      method: "CreateOrder",
      id: "step2",
      params: [
        // data: "@custId" → Server erkennt @-Präfix und substituiert.
        { parameterName: "customerId", num: 0, data: "@custId" },
        { parameterName: "total", num: 1, data: 99.9 },
      ],
      dependencyMapping: { orderId: "$" }, // neue OrderId weitergeben
    },
    {
      controller: "Order",
      method: "GetOrderById",
      id: "step3",
      params: [{ parameterName: "id", num: 0, data: "@orderId" }],
    },
  ];

  const [c, o, loaded] = await rest.callBatch(chain, ExecutionMode.Serial);
  const custId = c.data as number;
  const orderId = o.data as number;
  const loadedOrder = loaded.data as Order;
  console.log(
    `  [B] custId=${custId}, orderId=${orderId}; GetOrderById(@orderId) -> Total=${loadedOrder?.total}`,
  );

  // -----------------------------------------------------------------------------
  // Gotchas
  // -----------------------------------------------------------------------------
  // • JsonPath ist ergebnisrelativ (kein $.data-Envelope): "$", "$.Id", "$[0].Id".
  // • Ein UNAUFGELÖSTES @alias → Server antwortet 400 "Unresolved dependencies".
  //   Jeder @alias muss VOR seiner Nutzung deklariert sein (dependencyMapping eines
  //   früheren Requests in derselben Batch).
  // • Zirkuläre Abhängigkeiten → 400 für ALLE Requests der Batch.
}