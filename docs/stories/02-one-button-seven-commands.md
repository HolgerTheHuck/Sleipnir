# Story 02 — One Button, Seven Commands

> One user action fans out to many business operations.
> The server runs them in one roundtrip and tells you **exactly which one failed**.

---

## The business problem

A "Place order" button. One click. Behind it, seven downstream services have to do
their piece before the screen can say "done":

1. **Order** — create the order, get back an `orderId`
2. **Inventory** — reserve the articles on the order
3. **Billing** — charge the customer
4. **Loyalty** — award points for the purchase
5. **Notification** — send the confirmation email (needs `orderId`)
6. **Audit** — write the `order.placed` event (needs `orderId`)
7. **Shipping** — schedule the pickup (needs `orderId`)

Three of the seven depend on the `orderId` that step 1 produces. The other four are
independent. This is the second most common shape of business-API pain: **one action,
many side-effects** — the write-side twin of the N+1 read.

There is also a hard business rule: a customer over their credit limit must not be
charged. Billing will refuse customer #7 with `402 Credit limit exceeded`. That
refusal is *one* of the seven; the question is what happens to the other six.

---

## The REST Way

With plain REST the client loops, `await`ing each call before the next. The common
default is to stop at the first failure — the loop throws, you surface the error, the
user retries. Here is what that actually does:

```csharp
var order    = await PostAsync<Order>("Order.Create",         (customerId, addressId, articleIds));   // ✓ ord-1042
var inv      = await PostAsync<Ack>("Inventory.Reserve",      articleIds);                            // ✓ reserved
var billing  = await PostAsync<Ack>("Billing.Charge",         (customerId, amount));                  // ✗ 402 — throws
// Loyalty, Notification, Audit, Shipping are NEVER CONTACTED.
```

The loop aborted at step 3. **Three of seven** commands ran. Four were never attempted —
including the three that don't even depend on Billing (Loyalty, Notification, Audit,
Shipping). You now have a created order and reserved inventory against an order that
will not be billed, and **no notification, no audit, no shipment, no loyalty points**.
The user sees "something went wrong" with no structured list of what actually happened.

### Pain points

- **A failure aborts unrelated work.** Billing's refusal prevented Loyalty, Notification,
  Audit, and Shipping from running — even though none of them depend on Billing. The
  loop's early exit cascades a single failure into four silently-skipped services.
- **Partial state, invisibly.** Order created, Inventory reserved, Billing failed. The
  caller has no ordered array of "what ran, what didn't, with what result" — it has one
  exception from one service and silence on the rest.
- **Seven serial roundtrips** (if nothing fails). Each `await` pays network latency in
  series, even for the four independent calls that could have run in parallel.
- **Hand-rolled parallelism is worse.** You *can* `Task.WhenAll` the independent four
  and then chain the three — but now the client owns the call graph again (Story 01's
  pain, on the write side), and you still hand-roll the per-call try/catch to keep one
  failure from aborting the rest.

---

## The Trame Way

Trame sends all seven as **one batch**. The server executes the dependency graph —
the four independent commands in parallel, the three `orderId`-dependent ones after
`Order.Create` exposes its `orderId`. **A failure on one command does not abort the
batch**: each request gets its own response, in order, with its own code. The caller
receives a structured array of seven results and decides what to do.

### The domain (code-first, no IDL)

```csharp
[TrameController("Checkout.Order")]
public class CheckoutOrderController
{
    // Provider: exposes orderId for the three downstream commands that need it.
    [TrameMethod("Create")]
    public Task<CommandAck> Create(int customerId, int addressId, List<int> articleIds) { … }
}

[TrameController("Checkout.Billing")]
public class CheckoutBillingController
{
    // Returns TrameResults.Error on a business rule — NEVER throws to set a code.
    [TrameMethod("Charge")]
    public Task<TrameResponse> Charge(int customerId, decimal amount)
    {
        if (OverCreditLimit.Contains(customerId))
            return TrameResults.Error(402, $"Credit limit exceeded for customer {customerId}.");
        return TrameResults.Ok(new CommandAck { Service = "Billing", … });
    }
}

// Inventory, Loyalty, Notification, Audit, Shipping — each one [TrameMethod].
// Notification/Audit/Shipping take an `int orderId` parameter (name-bind to @orderId).
```

The `Checkout.` prefix is demo hygiene: Story 01 already registers an `"Order"`
controller in the same process, and Trame controller names are app-wide unique. In a
real app you'd name it `"Order"`.

### The batch — one request, seven commands, declared dependencies

```csharp
var batch = new TrameMultiRequest
{
    Mode = ExecutionMode.Parallel,   // ignored once a DependencyMapping is present:
                                     // the server auto-detects → topological execution.
    Requests = new()
    {
        // Provider: Order.Create. Exposes orderId for the three consumers below.
        TrameCall.Init("Checkout.Order", "Create")
            .With(customerId, addressId, articleIds).Named("order")
            .Exposes("$.orderId", "orderId").ToRequest(),

        // Four independent commands — level 1, parallel with Order.
        TrameCall.Init("Checkout.Inventory", "Reserve").With(articleIds).Named("inventory").ToRequest(),
        TrameCall.Init("Checkout.Billing",  "Charge").With(customerId, amount).Named("billing").ToRequest(),
        TrameCall.Init("Checkout.Loyalty",  "AwardPoints").With(customerId, amount).Named("loyalty").ToRequest(),

        // Three consumers of @orderId — level 2, after Order succeeds.
        TrameCall.Init("Checkout.Notification", "SendConfirmation").With(customerId).WithAlias("@orderId").Named("notify").ToRequest(),
        TrameCall.Init("Checkout.Audit",        "Log").WithAlias("@orderId").With("order.placed").Named("audit").ToRequest(),
        TrameCall.Init("Checkout.Shipping",     "Schedule").WithAlias("@orderId").With(addressId).Named("shipping").ToRequest(),
    }
};

// ONE roundtrip. Seven responses, in request order, each with its own code.
var responses = (await client.Call(batch))!.ToList();
```

### The graph the server executes

```
   Level 1 (parallel)                         Level 2 (parallel, after Order)
   ┌───────────────┬──────────────┬──────────────────┐        @orderId flows from Order
   │               │              │                  │
 Order.Create    Inventory      Billing ✗402      Loyalty      ┌──→ Notification.SendConfirmation
 exposes orderId  ✓             (credit limit)     ✓           ├──→ Audit.Log("order.placed")
   │                                                          └──→ Shipping.Schedule
   └────── @orderId ────────────────────────────────────────────────►
```

`Order.Create` succeeds and exposes `orderId`. `Billing` returns `402`. The three
`@orderId` consumers depend on **Order**, not on Billing — so Billing's failure does
not reach them. They run, because Order succeeded. Six commands succeed, one fails,
all seven attempted, in one roundtrip.

### Before / after

|                          | The REST Way (abort on first error) | The Trame Way                |
|--------------------------|--------------------------------------|------------------------------|
| Roundtrips               | 3 (then aborts) / 7 (if no failure)  | **1**                        |
| Commands attempted       | 3 of 7                               | **7 of 7**                   |
| Commands that succeeded  | 2                                    | **6**                        |
| Failure visibility       | one exception, silence on the rest   | **per-command code + message** |
| Unrelated work skipped   | yes — 4 services never contacted     | no — independent commands run |
| Latency (≈30ms/call)     | ~90 ms (then stops)                  | **~60 ms** (2 topological levels) |

The line that matters: **Trame attempted all seven and finished faster than REST did
three** — because independent commands run in parallel and a failure does not abort
the batch.

---

## Discussion

### Why is this simpler?

The client stopped being a transaction coordinator. It does not loop, it does not
abort, it does not hand-roll `Task.WhenAll` plus per-call try/catch. It declares the
seven commands and their one dependency (`orderId`), sends them as a batch, and gets
back an ordered array of seven results. "Which one failed and why?" is a property of
the response, not a debugging session.

The isolation is the part plain REST cannot do without client code: Billing's `402`
is one entry in the result array, not a loop-exiting throw. The three commands that
depend on `orderId` (not on Billing) still ran, because the server executes the
**declared** graph, not a sequential loop that aborts at the first non-2xx.

### The honest tradeoff: no saga, no rollback

This is the boundary, and it is deliberate. **Trame is a request-time fan-out, not a
saga engine.** In *both* columns above, Inventory was reserved before Billing refused.
Trame did not roll back Inventory. It told you Billing failed (`402`, named, with the
message); compensating the reservation is your job — refund inventory, retry billing
on a corrected card, queue for human review, whatever your domain says.

What Trame gives you over the REST loop is **visibility and isolation in one roundtrip**:
you see all seven outcomes at once, the unrelated work still ran, and you decide the
compensation with the full picture — instead of discovering, after a retry, that the
first attempt had already created an order and reserved stock that nobody told you
about. If you need automatic compensation / rollback / long-lived workflows, reach for
a real saga orchestrator (or a transactional outbox). Trame is the *dispatch* layer
that gets the seven commands out and their results back; it is not the *consistency*
layer.

### Where NOT to use this

- **When you need ACID across the seven.** If "all seven succeed or none do" is a hard
  requirement, a distributed transaction or a saga with compensation is the right tool.
  Trame will faithfully report "six worked, one failed" — which is the wrong semantics
  if you needed "zero worked."
- **When the seven are genuinely independent and fire-and-forget.** If you don't need
  the results and don't care which failed, a message bus with a consumer each is a
  better fit. Trame earns its keep when you want the *results back, structured, in one
  roundtrip*.
- **Long-running commands.** Dependency chaining is for bounded request-time work. A
  command that takes minutes should not hold a batch open — dispatch it asynchronously
  and report completion separately.

### One thing to know about failure here

`Billing.Charge` returns `TrameResults.Error(402, …)` — it does **not** throw. A throw
becomes a generic `500` with no message leak (Story 01's error contract). To set a
client-visible code and message, return a `TrameResponse` via `TrameResults`. The
invoker passes it through verbatim, so the `402` and the credit-limit message reach
the client's result array unchanged.

---

## Try it

**Standalone solution — open in Visual Studio, press F5:**

```
stories/02-one-button-seven-commands/Story02.sln
```

Boots a Trame server with the seven command controllers and an in-memory store (customer
#7 over credit limit), and the browser lands in the Developer UI at `/Trame` (port 5002).
The one-batch call from this story — seven of seven attempted, Billing's `402` isolated —
is in the story README (`stories/02-one-button-seven-commands/README.md`), ready to paste
into the DevUI batch sender. Source: `stories/02-one-button-seven-commands/Program.cs`
+ `Domain.cs`.

Next story: **The Same Contract, Three Wires** — the same controllers reached over
REST, WebSocket, and SignalR+MessagePack, and why the transport is the caller's
choice, not the server's burden.