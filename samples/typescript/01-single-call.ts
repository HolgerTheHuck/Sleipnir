// ==============================================================================
// 01 — Single Call (TypeScript/JS-Client)
// ==============================================================================
// Ein einzelner RPC-Aufruf. Gezeigt für REST und WebSocket — wähle je nach
// Anforderung. WebSocket ist der empfohlene primäre Kanal (persistent, geringe
// Latenz); REST ist zustandslos und am einfachsten.
//
// Hinweis: Der TS-WebSocket-Client nimmt die Basis-URL (https://…) + einen
// separaten wsPath (Default "tramews"); er hebt intern auf wss://…/tramews ab.
// ==============================================================================

import { TrameRestClient, TrameCall, TrameWebSocketClient } from "trame-client";

type Customer = { id: number; name: string; email: string };

export async function run(rest: TrameRestClient): Promise<void> {
  // --- REST: Kunden anlegen (skalarer int kommt zurück) ------------------------
  const newId = await rest.callJson<number>("Customer", "AddCustomer", {
    name: "Alice",
    email: "alice@x.com",
  });
  console.log(`  [REST]    AddCustomer -> neue Id = ${newId}`);

  // --- REST: Kunden laden (callJson<T> deserialisiert data direkt nach T) ------
  const c = await rest.callJson<Customer>("Customer", "GetCustomerById", { id: newId });
  console.log(`  [REST]    GetCustomerById(${newId}) -> ${c?.name} <${c?.email}>`);

  // --- REST: alle Kunden (Liste) ----------------------------------------------
  const all = await rest.callJson<Customer[]>("Customer", "GetAllCustomers");
  console.log(`  [REST]    GetAllCustomers -> ${all?.length ?? 0} Kunde(n)`);

  // --- WebSocket: derselbe Aufruf über den persistenten Kanal -----------------
  // baseUrl + Default-wsPath "tramews" -> wss://localhost:5001/tramews.
  const ws = new TrameWebSocketClient("https://localhost:5001");
  await ws.connect();
  try {
    const c2 = await ws.callJson<Customer>(
      TrameCall.init("Customer", "GetCustomerById").with({ id: newId }).toRequest(),
    );
    console.log(`  [WebSocket] GetCustomerById(${newId}) -> ${c2?.name} <${c2?.email}>`);
  } finally {
    ws.close();
  }

  // --- Raw-Form (ohne Fluent Builder) — gelegentlich nützlich -----------------
  // params = TrameParameter[]; data ist der native JSON-Wert. GetAllCustomers hat
  // keine Parameter -> leeres Array.
  const rawList = await rest.callJson<Customer[]>({
    controller: "Customer",
    method: "GetAllCustomers",
    id: "Customer.GetAllCustomers",
    params: [],
  });
  console.log(`  [REST raw] GetAllCustomers -> ${rawList?.length ?? 0} Kunde(n)`);
}