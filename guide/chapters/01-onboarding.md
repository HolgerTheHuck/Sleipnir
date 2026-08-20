# Chapter 1 — Onboarding: first server, controller, DevUI

> **Goal:** stand up a Sleipnir API with one controller, call it from `curl`, and click
> around the Developer UI. Zero client SDK, no codegen yet — just the wire.

This chapter builds the first tier of the 3-tier app: **`Story.Api`**, a single ASP.NET
project that serves Sleipnir over REST (+ WebSocket) and hosts its own Developer UI. By
the end you have one method — `Market.GetQuote` — and you can reach it three ways.

```
guide/server/
  Story.Api.csproj      one ProjectReference → Sleipnir.Server (brings everything)
  Program.cs            AddSleipnir → UseSleipnirTransports → MapSleipnir
  Domain/Quote.cs       the contract type (the C# class IS the contract)
  Controllers/MarketController.cs   [SleipnirController] + [SleipnirMethod]
```

## The three-line wiring

`Program.cs` is the whole server. The Sleipnir work is three calls:

```csharp
using SleipnirHub.Extensions;     // AddSleipnir
using SleipnirServer;            // UseSleipnirTransports, MapSleipnir

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

builder.Services.AddSleipnir(options =>
{
    options.EnableDetailedErrors = builder.Environment.IsDevelopment();
});

builder.WebHost.UseUrls("https://localhost:5010");

var app = builder.Build();
app.UseCors();
app.UseRouting();
app.UseSleipnirTransports();   // WebSocket middleware + controller registration
app.MapSleipnir();              // REST (/api/sleipnir) + Developer UI (/Sleipnir) + hub
app.Run();
```

- **`AddSleipnir`** registers the invoker (singleton), the logging interceptor, and
  auto-discovers every `[SleipnirController]` in this assembly.
- **`UseSleipnirTransports`** registers the WebSocket transport and triggers controller
  registration.
- **`MapSleipnir`** maps the REST endpoints (`/api/sleipnir/json`, `/json/multi`,
  `/discovery`), the Developer UI at `/Sleipnir`, and (optionally) the SignalR hub.

> **One reference is enough.** `Story.Api.csproj` references `SleipnirServer.csproj` — the
> `Sleipnir.Server` meta-package, which transitively brings REST + WebSocket + SignalR +
> Developer UI + Core + Common. No NuGet restore of Sleipnir packages is needed; the
> guide rides on the in-tree source so it tracks the current (including unreleased) work.

## The contract type

The contract is a plain C# class. No IDL, no `.proto` — the class *is* the contract, and
discovery expands it into a JSON schema for the DevUI and (in chapter 2) the generated
clients.

```csharp
// guide/server/Domain/Quote.cs
public class Quote
{
    public string Symbol { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public decimal Change { get; set; }
    public DateTime Time { get; set; } = DateTime.UtcNow;
}
```

## The controller

```csharp
// guide/server/Controllers/MarketController.cs
[SleipnirController("Market")]
public class MarketController
{
    [SleipnirMethod("GetQuote")]
    [SleipnirDocumentation("Get a snapshot price quote for a single market symbol. Returns null if the symbol is unknown.")]
    public Quote? GetQuote(string symbol)
    {
        // ...tiny in-memory price table...
        return new Quote { Symbol = symbol.ToUpperInvariant(), Price = price, Change = change };
    }
}
```

Two attributes are all it takes: `[SleipnirController("Market")]` names the controller,
`[SleipnirMethod("GetQuote")]` names the method. The wire address is `Market.GetQuote`.
`[SleipnirDocumentation]` is optional; its text shows up in the DevUI.

## Run it

```bash
# One-time, for the https://localhost dev cert:
dotnet dev-certs https --trust

dotnet run --project guide/server
```

The console announces the endpoints:

```
REST          https://localhost:5010/api/sleipnir/json   (+ /multi, /discovery)
WebSocket     wss://localhost:5010/sleipnirws
Developer UI  https://localhost:5010/Sleipnir
```

## Sleipnir & REST — best friends

Your very first call is a plain HTTP POST. No SDK, no codegen — `curl` and the wire. This
is the theme of the whole guide: REST is the friend you can always reach for, and `curl`
is how you prove a Sleipnir API is alive.

**Discovery** is a `GET`:

```bash
curl -sk https://localhost:5010/api/sleipnir/discovery
```

It returns a versioned JSON document — controllers, methods, parameter types, and the
expanded `Quote` schema. This single payload is the contract; every generated client in
later chapters is derived from it.

**A call** is a `POST` to `/api/sleipnir/json`. Parameters are matched **by name**:

```bash
curl -sk -X POST https://localhost:5010/api/sleipnir/json \
  -H "Content-Type: application/json" \
  -d '{"controller":"Market","method":"GetQuote","params":[{"parameterName":"symbol","data":"BTC"}]}'
```

```json
{
  "code": 200,
  "data": { "symbol": "BTC", "price": 60000, "change": -1, "time": "2026-08-20T15:20:51Z" },
  "content": null,
  "id": "",
  "exposedDependencies": null,
  "error": null
}
```

An unknown symbol returns a `200` with `data: null` (see the callout below):

```bash
curl -sk -X POST https://localhost:5010/api/sleipnir/json \
  -H "Content-Type: application/json" \
  -d '{"controller":"Market","method":"GetQuote","params":[{"parameterName":"symbol","data":"NOPE"}]}'
# {"code":200,"data":null,...}
```

The wire request shape (`PROTOCOL.md`): the array is **`params`**, each entry has
**`parameterName`** + **`data`** (a native JSON value, not a JSON string). Binding is by
`parameterName` first, falling back to the positional `num` index.

## The Developer UI

Open `https://localhost:5010/Sleipnir` in a browser. (Bare `/` does not redirect —
bookmark `/Sleipnir`.) You get:

- A **discovery** tree of every controller and method, with the `Quote` schema expanded.
- A **call playground**: fill in `symbol` = `BTC`, send, see the response envelope.
- The **dependency builder** and **codegen** tabs — you'll meet those in chapters 6 and 2.

The DevUI talks to the same `/api/sleipnir/json` endpoint you just `curl`ed. It is the
fastest way to explore the contract without writing a client.

## Return types vs error codes (a design call)

You'll notice `GetQuote` returns `Quote?` and signals "not found" with `null` — a `200`
with `data: null`, not a `404`. That is deliberate, and it matches the canonical Story-01
controllers (`stories/01-n-plus-one-screen/Domain.cs`):

> **The tradeoff.** A method that returns the **domain type** (`Quote?`) has a typed
> return in discovery — so the generated clients in chapter 2+ see `Quote`, not an
> envelope. A method that returns `SleipnirResponse` (via `SleipnirResults.NotFound(...)`,
> `BadRequest(...)`) gets rich HTTP-style error codes (`400`/`404`/`409`), but discovery
> types its return as `opaque`, so generated clients lose the type.

The guide uses the **domain-typed** style for the read paths (so the typed-client story is
clean) and reserves rich error codes for the places that need them — chiefly **auth**
(chapter 7), where `[SleipnirAuthorise]` makes the framework return `401`/`403` without
the method returning `SleipnirResponse` at all. See `CLAUDE.md` → *Error Handling* for the
full `SleipnirResults` factory when you do need a business error code.

## Try it

```bash
dotnet run --project guide/server                       # terminal 1
curl -sk https://localhost:5010/api/sleipnir/discovery   # see the contract
# then open https://localhost:5010/Sleipnir             # click around the DevUI
```

You should see the `Market.GetQuote` method, the expanded `Quote` schema, and a `200`
response with a live BTC quote.

---

**Next:** [Chapter 2 — a Blazor Pflege-Backend with a generated typed C# client](02-blazor-client.md).
The same `Market.GetQuote`, but called as `client.Market.GetQuote("BTC")` with full
compile-time types — generated from the discovery you just `curl`ed.