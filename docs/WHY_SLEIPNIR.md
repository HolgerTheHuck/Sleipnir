# Why Sleipnir?

Sleipnir started from a simple observation:

**Not every useful interaction with an application is naturally a resource interaction.**

Sometimes we want a resource.

Sometimes we want to ask a question.

And sometimes we want the application to do something.

Those are different kinds of intent.

Good APIs should be able to express that difference.

---

## Resources are a great abstraction

REST gave us an extremely useful way to think about APIs.

Applications contain things:

```text
Customers
Orders
Articles
Documents
Invoices
```

Those things have identity and state.

So this feels natural:

```http
GET /customers/42
```

So does:

```http
PUT /customers/42/address
```

The resource is the natural abstraction.

There is no reason for Sleipnir to replace it.

**REST and Sleipnir should be best friends.**

---

## But applications don't only have things

Applications also answer questions:

```text
Which articles fit this vehicle?

Which customers have open orders?

Which invoices match these criteria?
```

And applications perform operations:

```text
CalculatePrice

ValidateOrder

ReserveStock

ApproveInvoice

GenerateDocument
```

Trying to express all of those interactions purely as resource manipulation can work.

But sometimes the abstraction starts fighting the intent.

The API no longer says what the caller actually wants to do.

---

## HTTP QUERY is an interesting example

HTTP itself now has another way to express this distinction.

A complex query is not necessarily a resource.

Consider:

```http
QUERY /articles
Content-Type: application/json

{
  "vehicleId": 12345,
  "diameter": {
    "min": 280,
    "max": 320
  },
  "available": true
}
```

The payload isn't an Article.

It describes what the caller wants to know about Articles.

It expresses **intent**.

Commands follow the same observation.

```text
CalculatePrice
ValidateOrder
ReserveStock
```

These aren't primarily things.

They describe what the caller wants the application to **do**.

Sleipnir makes those operations explicit.

---

# Command-oriented doesn't mean anti-REST

This distinction is fundamental to Sleipnir.

It isn't:

```text
REST
  versus
Sleipnir
```

It is:

```text
What kind of interaction is this?
              │
        ┌─────┴─────┐
        │           │
    Resource     Operation
        │           │
       REST       Sleipnir
```

And real applications contain both.

> **Resources have REST. Operations have Sleipnir. Good applications can have both.**

---

# The real problem starts with conversations

Calling an operation remotely isn't particularly difficult.

The interesting problems begin when operations depend on each other.

Imagine a screen that needs to:

1. search for customers,
2. retrieve their open orders,
3. extract the article IDs,
4. retrieve availability for those articles.

The individual operations are simple.

But the client now has to conduct a conversation:

```text
Client                     Server

   │── SearchCustomers ──────►│
   │                          │
   │◄──── customers ──────────│
   │                          │
   │ extract customerIds      │
   │                          │
   │── GetOpenOrders ─────────►
   │                          │
   │◄──── orders ─────────────│
   │                          │
   │ extract articleIds       │
   │                          │
   │── GetAvailability ───────►
   │                          │
   │◄──── availability ───────│
```

Every dependency becomes another network roundtrip.

And something else has happened almost unnoticed:

**The client has become an orchestrator.**

It knows:

* which operation depends on which other operation,
* how intermediate results are transformed,
* which calls can run in parallel,
* which calls must stop when another fails,
* how the final result is assembled.

None of that is presentation logic.

Yet it often ends up in the frontend.

---

# Move the conversation to where it belongs

Sleipnir allows the caller to describe the dependencies instead:

```text
Customer.Search
      │
      │ customerIds
      ▼
Order.GetOpen
      │
      │ articleIds
      ▼
Article.GetAvailability
```

The complete graph crosses the network boundary once.

The server executes it.

That changes several things at the same time.

There are fewer network roundtrips.

Independent operations can run concurrently.

Intermediate data doesn't have to travel to a remote client merely to become input for the next operation.

And the client no longer has to implement the execution workflow.

This is why performance and simpler application code aren't separate Sleipnir features.

They can be consequences of the **same architectural decision**.

> **Fewer roundtrips. Less orchestration. Clearer intent.**

---

# Network boundaries shouldn't destroy type safety

There's another problem at application boundaries.

Inside an application, we wouldn't normally write:

```text
Call method named "CalculatePrice"

Send this JSON

Expect some JSON back

Trust that both sides still agree
```

We use types and interfaces.

Cross a network boundary, however, and we often lose them.

---

## Code generation can help — but the direction matters

For straightforward resource APIs, OpenAPI-based client generation can work well.

CRUD maps naturally:

```text
GET    /customers/{id}
POST   /customers
PUT    /customers/{id}
DELETE /customers/{id}
```

As APIs become increasingly operation-oriented, reconstructing a good developer interface from an HTTP description becomes harder.

The generator starts with transport information:

```text
HTTP operation
      ↓
OpenAPI description
      ↓
generic generator
      ↓
developer interface
```

It has to infer a programming model from that description.

Sleipnir approaches the problem in the opposite direction:

```text
developer interface
      ↓
Sleipnir contract
      ↓
C# client
TypeScript client
transport
```

The developer interface already exists.

There is no need to rediscover it.

> **Code generation should preserve intent, not rediscover it.**

The generated client should look like an interface somebody deliberately designed — because somebody did.

---

# A network boundary shouldn't have to become a type-safety boundary

This becomes particularly useful when Sleipnir is used in both directions.

Northbound:

```text
Browser
   │
   │ generated TypeScript
   ▼
Application
```

Southbound:

```text
Application
   │
   │ generated C#
   ▼
Business Service
```

Or across several application layers:

```text
                    TypeScript
                        │
                        ▼
Browser ─────────► Application
                        │
                        │ C#
                        ▼
                  Business Service
                        │
                        │ C#
                        ▼
                   Backend Service
```

Each boundary remains a real network boundary.

Failures are still failures.

Latency still exists.

Distributed systems are still distributed systems.

Sleipnir doesn't pretend otherwise.

But the programming contract doesn't have to disappear simply because a network sits between two components.

---

# The transport isn't the application contract

An application operation might travel over REST today.

Tomorrow, a particular client might benefit from WebSocket or SignalR.

That shouldn't require redesigning the operation itself.

Sleipnir separates:

```text
What does the application do?
```

from:

```text
How does this client communicate with it?
```

The same command contract can be exposed through different transports.

This also means transport decisions can be made for architectural reasons rather than leaking into the application model.

---

# Use Sleipnir where it removes friction

Sleipnir isn't intended to replace every API.

If you have:

```http
GET /customers/42
```

and it expresses exactly what you mean, keep it.

If standard REST CRUD is simple, well understood and supported by your ecosystem, use it.

Sleipnir becomes interesting when you start seeing things like:

```text
POST /orders/42/calculate-price

POST /orders/42/validate

POST /orders/42/approve

POST /articles/search-compatible

POST /documents/generate
```

Or when your client starts doing this:

```text
call
wait
extract
call
wait
transform
call
wait
combine
```

Or when generated clients stop looking like interfaces you'd willingly design yourself.

Those are signals that the interaction may no longer be primarily resource-oriented.

It may be an operation.

Or a conversation between operations.

That's the space Sleipnir is designed for.

---

# What Sleipnir optimizes for

Sleipnir's design follows a few principles.

### Preserve intent

If something is an operation, let the API describe it as an operation.

### Preserve the programming contract

Don't reconstruct developer interfaces from transport descriptions if the interface already exists.

### Minimize unnecessary conversations

Don't send intermediate results across network boundaries only to feed them into another remote operation.

### Keep orchestration close to execution

Let the server resolve dependencies and parallelize work where appropriate.

### Separate contract from transport

Choose REST, WebSocket or SignalR based on communication requirements rather than application semantics.

### Work with existing architecture

Use REST for resources. Use Sleipnir for operations. Combine them when that produces the clearest system.

---

# What Sleipnir doesn't promise

Sleipnir doesn't make distributed systems local.

It doesn't eliminate network failures.

It doesn't make every workflow suitable for server-side dependency chaining.

It doesn't mean every method in your application should become remotely callable.

And it doesn't mean REST is obsolete.

Sleipnir is useful precisely because it has a narrower purpose:

**making application operations and the conversations between them easier to express across network boundaries.**

---

# The idea in four lines

> **Resources have REST. Operations have Sleipnir. Good applications can have both.**
>
> **Move conversations to where they belong.**
>
> **A network boundary shouldn't have to become a type-safety boundary.**
>
> **Code generation should preserve intent, not rediscover it.**

If those ideas match problems you're seeing in your application:

**[Get started with Sleipnir →](../GETTING_STARTED.md)**
