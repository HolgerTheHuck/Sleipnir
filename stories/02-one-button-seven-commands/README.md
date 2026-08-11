# Story 02 — One Button, Seven Commands

> **One user click, seven downstream writes, one of them refuses. The REST way aborts the loop
> and never contacts the rest. The Sleipnir way runs all seven in one roundtrip with per-command
> isolation — the unrelated commands still ran, and the one failure is named.**

The write-side companion to Story 01. Where Story 01 was *reading* a screen with a dependency
graph, this is *acting* on one click — a fan-out of business operations where each command has
its own outcome, and a single failure must not silently kill the rest.

## Run it (F5 → DevUI)

1. Open **`Story02.sln`** in Visual Studio (or `dotnet build && dotnet run --project Story02.csproj`).
2. Press **F5**. The browser opens at **`http://localhost:5002/Sleipnir`** — the DevUI.
3. Seven controllers from `Domain.cs`: `Order`, `Inventory`, `Billing`, `Loyalty`,
   `Notification`, `Audit`, `Shipping`. Customer **7** is deliberately over its credit limit —
   `Billing.Charge(customerId=7)` returns `402` (a business error via `SleipnirResults.Error`, never
   a throw).

## The Sleipnir Way — one batch, seven commands, per-command isolation

Paste into the DevUI batch sender (`POST /api/sleipnir/json/multi`). Seven of seven attempted;
`Billing`'s 402 is isolated; the three `@orderId` consumers (Notification, Audit, Shipping) hang
off `Order.Create`, **not** off Billing, so they still run.

```jsonc
{
  "mode": 0,
  "requests": [
    { "controller": "Order",        "method": "Create",           "id": "order",
      "params": [{ "parameterName": "customerId", "data": 7 },
                  { "parameterName": "addressId",  "data": 101 },
                  { "parameterName": "articleIds", "data": [1001,1002,1003] }],
      "dependencyMapping": { "orderId": "$.orderId" } },

    { "controller": "Inventory",     "method": "Reserve",          "id": "inventory",
      "params": [{ "parameterName": "articleIds", "data": [1001,1002,1003] }] },

    { "controller": "Billing",      "method": "Charge",          "id": "billing",
      "params": [{ "parameterName": "customerId", "data": 7 },
                  { "parameterName": "amount",     "data": 52.42 }] },

    { "controller": "Loyalty",      "method": "AwardPoints",      "id": "loyalty",
      "params": [{ "parameterName": "customerId", "data": 7 },
                  { "parameterName": "amount",     "data": 52.42 }] },

    { "controller": "Notification",  "method": "SendConfirmation", "id": "notify",
      "params": [{ "parameterName": "customerId", "data": 7 },
                  { "parameterName": "orderId",    "data": "@orderId" }] },

    { "controller": "Audit",        "method": "Log",             "id": "audit",
      "params": [{ "parameterName": "orderId", "data": "@orderId" },
                  { "parameterName": "action",  "data": "order.placed" }] },

    { "controller": "Shipping",     "method": "Schedule",         "id": "shipping",
      "params": [{ "parameterName": "orderId",   "data": "@orderId" },
                  { "parameterName": "addressId", "data": 101 }] }
  ]
}
```

### What you get back

Seven responses, one per command, each carrying its own `code`. `Billing` is `402`; every other
command is `200`. The three `@orderId` consumers ran against the id `Order.Create` exposed —
they did **not** wait on Billing and were not aborted by it.

### The REST Way (for contrast)

Sequential loop, abort on the first `SleipnirException`. Billing's 402 throws and the loop jumps
to the catch — Notification, Audit, Shipping are never contacted:

```csharp
var o  = await client.Call<CommandAck>(SleipnirCall.Init("Order","Create").With(7,101,articleIds));
var iv = await client.Call<CommandAck>(SleipnirCall.Init("Inventory","Reserve").With(articleIds));
var b  = await client.Call<CommandAck>(SleipnirCall.Init("Billing","Charge").With(7,52.42));   // ← throws (402)
// Loyalty, Notification, Audit, Shipping never reached.
```

|                       | The REST Way            | The Sleipnir Way              |
|-----------------------|-------------------------|---------------------------|
| Commands attempted    | 3 of 7 (loop aborted)   | **7 of 7**                |
| Failure visibility    | exception, rest unknown | every outcome in one pass |
| Unrelated commands    | never contacted         | ran, isolated             |

## The boundary — Sleipnir is dispatch, not saga

Both ways reserved Inventory before Billing refused; **neither rolls back**. Sleipnir did not
compensate. It showed all seven outcomes in one roundtrip, kept the unrelated commands running,
and named the one failure. **Your job:** decide the compensation with the full picture.

This is deliberate. Sleipnir resolves **data dependencies within one request**. It does not run
long-lived workflows, it does not schedule, and it does not roll back. "Approve → debit → bill
→ notify" is a command fan-out; if one fails, Sleipnir tells you *which one* and *why* — it does not
undo the ones that already ran. A saga engine is a different tool.

## Files

- `Program.cs` — F5 wiring.
- `Domain.cs` — seven command controllers + in-memory store (Customer 7 over credit limit).
- Full narrative: `docs/stories/02-one-button-seven-commands.md`.

Next: **Story 03 — The Same Contract, Three Wires** (one domain, three transports, identical result).