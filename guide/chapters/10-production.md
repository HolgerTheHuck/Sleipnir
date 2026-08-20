# Chapter 10 — Production: interceptors, observability, tracing, binary

> **Goal:** take the 3-tier app from "runs" to "production-shaped." A custom **interceptor**
> logs every single call with a correlation id; the opt-in **`/observability`** endpoint exposes
> live transport/runtime counters; the always-on **`ActivitySource("Sleipnir")`** gives you
> distributed tracing for free (the guide wires a package-free listener so spans print to the
> console); and a **co-hosted plain `GET`** serves a browser-fetchable SVG logo — the blessed
> pattern for media, not an RPC method. Plus the hardening knobs you flip before going north-bound.

Chapters 1–9 built a working 3-tier app. This last chapter adds the four production surfaces
that turn "it works on my machine" into "it runs in prod": **a cross-cutting interceptor
pipeline**, **an observability endpoint**, **distributed tracing**, and **a deliberate answer
to "where do binary/media resources live?"** — plus the rate-limit and batch-cap knobs that
protect a north-bound deployment. None of it changes the RPC contract: interceptors, options,
and a plain `GET` are all server-side plumbing, so no client regen is needed.

## 1. Interceptors — a pipeline around every single call

Sleipnir's interceptor pipeline wraps every single RPC invocation. An interceptor is an
`ISleipnirInterceptor` with one method — call `next` to continue, read the context before and
the response after:

```csharp
public interface ISleipnirInterceptor
{
    Task<SleipnirResponse?> InvokeAsync(
        SleipnirInvocationContext context,
        SleipnirInvocationDelegate next);
}
```

`SleipnirInvocationContext` carries the `Request`, the `HttpContext` (non-null on the REST/WS
path), the resolved `InvokeInfo` (after the invoker resolves the controller/method), the
`Response` (populated after `next`), and the `Activity` (the `SleipnirCall` span, if a listener
is subscribed). The built-in `SleipnirLoggingInterceptor` is the reference shape — a stopwatch,
a `LogTrace` before, a `LogDebug` after, a `LogError` + rethrow on exception.

The guide adds a `CorrelationIdInterceptor` that propagates a correlation id (from the
`X-Correlation-Id` request header, or a fresh one) onto the HTTP response and logs every call:

```csharp
public sealed class CorrelationIdInterceptor(ILogger<CorrelationIdInterceptor> logger)
    : ISleipnirInterceptor
{
    public async Task<SleipnirResponse?> InvokeAsync(
        SleipnirInvocationContext context, SleipnirInvocationDelegate next)
    {
        var http = context.HttpContext;
        var incoming = http?.Request.Headers["X-Correlation-Id"].ToString();
        var correlationId = !string.IsNullOrWhiteSpace(incoming) ? incoming! : Guid.NewGuid().ToString("N")[..12];
        if (http is not null && !http.Response.HasStarted)
            http.Response.Headers["X-Correlation-Id"] = correlationId;

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var response = await next(context);
            stopwatch.Stop();
            logger.LogInformation("RPC {Controller}.{Method} [{CorrelationId}] -> {Code} in {Duration}ms",
                context.ControllerName, context.MethodName, correlationId, response?.Code, stopwatch.ElapsedMilliseconds);
            return response;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            logger.LogError(ex, "RPC {Controller}.{Method} [{CorrelationId}] threw after {Duration}ms",
                context.ControllerName, context.MethodName, correlationId, stopwatch.ElapsedMilliseconds);
            throw;
        }
    }
}
```

### Registration order = execution order, reversed

Register the interceptor **after** `AddSleipnir`, so DI appends it after the built-ins:

```csharp
builder.Services.AddSleipnir(options => { /* … */ });
// Built-ins are registered inside AddSleipnir: Auth → Telemetry → Logging.
// Append the custom one AFTER, so DI yields: Auth, Telemetry, Logging, Correlation.
builder.Services.AddSingleton<ISleipnirInterceptor, CorrelationIdInterceptor>();
```

The invoker builds the pipeline by wrapping interceptors in **reverse registration order** —
the last-registered interceptor runs **first** (outermost). So with built-ins registered first
and the custom one appended last, the custom interceptor is **outermost**: it wraps Auth and
Logging, seeing unauthorized calls too. That is the right place for a request-level concern
(correlation, request logging); a method-level concern would register inner instead. The
built-in order is `Auth → Telemetry → Logging` deliberately: Auth rejects unauthorized calls
*before* Telemetry measures them, so you do not log unauthorized traffic as if it ran.

> You can also populate `SleipnirOptions.Interceptors` (a `List<ISleipnirInterceptor>`) inside the
> `AddSleipnir` callback — same effect, same ordering rule. The `services.AddSingleton` path
> shown here lets DI inject the logger. Either is fine; pick one.

### The one caveat that matters: single-call path only (1.1.x)

User interceptors run on the **single-call path only** — `POST /json` (single), a WebSocket
single-frame request. They do **not** run on the per-element invocations of a **batch**
(`/json/multi`, a WS multi-request, a JSON-RPC batch); the batch path bypasses the interceptor
pipeline (routing it through the pipeline is tracked for 1.2, `ROADMAP.md` R7). A startup
warning is logged once when `options.Interceptors` is non-empty.

**Authorization is unaffected** — `[SleipnirAuthorise]` is enforced structurally by the
invoker's serial auth pre-pass, not by user interceptors. So this is a **logging /
observability** seam, not a security seam. Do not build a tenant-isolation, rate-limit, or
audit control on this seam in 1.1.x; use `[SleipnirAuthorise]`/policies and the framework-level
gates (below). The verified outcome in "Try it" shows the difference: a single call gets the
`X-Correlation-Id` header and the `RPC …` log line; a batch gets neither — the interceptor was
bypassed.

## 2. Observability — the JSON snapshot endpoint

Flip one option and `MapSleipnir` maps a JSON snapshot endpoint:

```csharp
builder.Services.AddSleipnir(options =>
{
    options.EnableObservability = true;   // GET /api/sleipnir/observability
    // …
});
```

`GET /api/sleipnir/observability` returns live transport/runtime state straight from the
process-wide `SleipnirConnectionRegistry` — no OpenTelemetry SDK needed:

```json
{ "transports":{ "rest":true, "webSocket":true, "signalR":true, "sse":true },
  "activeConnections":0, "activeSubscriptions":0, "eventDroppedTotal":0,
  "callCount":1, "errorCount":0, "batchCount":0, "uptimeMs":5559 }
```

It is the same document the Developer UI's Observability panel renders. Like `/discovery`, it
is `RequireAuthentication`-gated: when north-bound default-deny is on, an unauthenticated caller
gets `401`.

### Prometheus `/metrics` — the production scrape path

The JSON snapshot is for ad-hoc checks and the DevUI. For a Prometheus scrape (the durable
pull contract a Grafana/Heimdall dashboard polls), use the `Sleipnir.Telemetry` package —
**not** a `SleipnirOptions` flag:

```csharp
// Add the Sleipnir.Telemetry package reference, then:
builder.Services.AddSleipnirPrometheusMetrics();                // subscribe the "Sleipnir" Meter
// …
app.UseSleipnirPrometheusScrapingEndpoint();                   // mount GET /api/sleipnir/metrics (text)
```

The guide server does **not** reference `Sleipnir.Telemetry`: that package pulls the
OpenTelemetry dependency graph, which the build-time contract-export tool (which reflects the
server assembly to generate `contract.sleipnir.json`) currently cannot resolve. So the guide
demonstrates the **same tracing surface with a package-free `ActivityListener`** (next section)
and documents the OTel-SDK path as the production opt-in. The JSON `/observability` endpoint,
which needs no package, is the runnable observability surface here.

## 3. Tracing — `ActivitySource("Sleipnir")`, always on, cost-neutral

Sleipnir instruments every call with an always-on `ActivitySource` named `"Sleipnir"`
(`SleipnirCore.Tracing.SleipnirTracing.ActivitySourceName`). Three instrumentation sites in
`SleipnirInvoker`:

- **Single-call** `InvokeDi(SleipnirRequest)` — a `SleipnirCall` span wrapping the whole
  interceptor pipeline (tags `rpc.system="sleipnir"`, `rpc.service`, `rpc.method`,
  `sleipnir.request_id`, `sleipnir.binary.length`).
- **Batch dispatcher** `InvokeDi(IEnumerable<>)` — a `SleipnirBatch` span
  (`sleipnir.batch.mode`, `sleipnir.batch.count`).
- **Per-request** in the batch parallel path — one `SleipnirCall` per element.

It is **cost-neutral**: `ActivitySource.StartActivity` returns `null` when no listener is
subscribed, so the instrumentation is free until you opt in. And — crucially — **tracing lives
in the invoker, not in the interceptor pipeline**, so it is *not* subject to the
single-call-only caveat above: the `SleipnirBatch` span fires on every batch even though user
interceptors are bypassed. The "Try it" output shows exactly this.

### The guide's package-free listener

The raw `System.Diagnostics.ActivityListener` API is in the shared framework (no package) —
the same mechanism the Sleipnir tracing tests use. The guide subscribes one that prints each
`Sleipnir` span to the console:

```csharp
ActivityListener consoleTraceListener = new()
{
    ShouldListenTo = source => source.Name == SleipnirTracing.ActivitySourceName,
    Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
    ActivityStopped = activity =>
    {
        var svc = activity.GetTagItem("rpc.service") as string ?? "?";
        var mth = activity.GetTagItem("rpc.method") as string ?? "?";
        Console.WriteLine($"[trace] {activity.DisplayName} {svc}.{mth} -> {activity.Status}" +
                          (activity.Status == ActivityStatusCode.Error
                              ? $" ({activity.StatusDescription ?? "error"})"
                              : $" {activity.Duration.TotalMilliseconds:F1}ms"));
    },
};
ActivitySource.AddActivityListener(consoleTraceListener);
```

Run the server and make a call — the console shows `[trace] SleipnirCall Market.GetQuote -> Ok
14.6ms`. Make a batch — it shows `[trace] SleipnirBatch ?.? -> Unset 5.6ms` (the batch span has
no single `rpc.service`/`rpc.method` — those live on the per-element `SleipnirCall` spans). No
package, no Collector, no `dotnet-counters`: the instrumentation is provably live.

### The production opt-in: `AddSleipnirTelemetry`

Production swaps the hand-rolled listener for the `Sleipnir.Telemetry` package, which boots the
OpenTelemetry SDK and subscribes the `"Sleipnir"` source **and** metrics Meter in one call:

```csharp
// Sleipnir.Telemetry package reference, then:
builder.Services.AddSleipnirTelemetry(o =>
{
    o.ServiceName = "Story.Api";
    o.Exporter = SleipnirExporter.Otlp;        // Console for dev; Otlp → Collector → Grafana/Heimdall/Jaeger
    o.OtlpEndpoint = "http://otel-collector:4317";
});
```

`AddSleipnirTelemetry` registers `AddSource(SleipnirTracing.ActivitySourceName)` (the only
integration point — the source name `"Sleipnir"`) + `AddMeter("Sleipnir")`, with optional
ASP.NET Core / HttpClient instrumentation. You can also wire your own OTel pipeline directly:
`AddOpenTelemetry().WithTracing(b => b.AddSource("Sleipnir"))`. The `Sleipnir` `ActivitySource`
is `public` (so `ActivitySourceName` is reachable from `Sleipnir.Telemetry`); all other tracing
members are `internal`. `SleipnirServer` itself does **not** reference `Sleipnir.Telemetry` —
consumers opt in.

## 4. Binary & media — RPC for commands, co-hosted `GET` for resources

Sleipnir is **command-oriented** RPC (`CreateOrder`, `PlaceOrder`). Media is
**resource-oriented**: a browser-fetchable `GET` URL, cacheable, rangeable, CDN-friendly. The
framework draws the line deliberately — and the guide demonstrates both sides.

### RPC binary — small bytes inside a call

A `byte[]` parameter is bound from `SleipnirRequest.BinaryData` (the first `byte[]` param on
the method); a `byte[]` return lands in `SleipnirResponse.Content` (not `Data`), serialized as
base64 on the JSON wire. The test-fixture shape:

```csharp
[SleipnirMethod("UploadBlob")]
public string UploadBlob(byte[] data, string filename)
    => $"Received {data.Length} bytes for {filename}";   // data ← request.BinaryData

[SleipnirMethod("DownloadBlob")]
public byte[] DownloadBlob(string name)
    => Encoding.UTF8.GetBytes($"Blob content for {name}");  // return → response.Content (base64)
```

This is the right tool for **small binary inside a call** — a thumbnail embedded in a result,
a signed hash, a small file returned from a command. Two limits to know: `byte[]` returns are
**not chainable** (`@alias` extraction reads `Data`, which is null for binary, so a dependent
gets a clean `400` instead of a 500); and the typed generated clients surface binary via the
raw `response.Content`, not the typed `Call<T>` (whose `T` deserializes `Data`) — the
"resource pillar" that would put `DownloadAsync` on the typed client is deferred
(`README_DETAILS.md` → "Serving Media & Non-RPC Resources"). For anything a browser fetches as
media, use the co-hosted `GET` below.

### The blessed pattern: a co-hosted plain `GET`

For a browser `<img src>`, a CDN, or anything with `Content-Type` / `ETag` / `Range` / `304`,
the production pattern is a **plain ASP.NET Minimal-API `GET` co-hosted next to the RPC
channel** — not an RPC method. One host, one DI container, one auth pipeline. The split:

> **Sleipnir = authority** — metadata, permission, business logic, and *which URL* the
> resource lives at. **HTTP / CDN = delivery** — the raw bytes, streamed, with the right
> `Content-Type` and cache headers.

The guide serves a deterministic SVG badge per symbol on a plain `GET`:

```csharp
app.MapGet("/logos/{symbol}.svg", (string symbol) =>
{
    var sym = symbol.ToUpperInvariant();
    var hue = Math.Abs(sym.GetHashCode()) % 360;   // stable color per symbol
    var svg = $"""
              <svg xmlns="http://www.w3.org/2000/svg" width="128" height="128" viewBox="0 0 128 128">
                <rect width="128" height="128" rx="24" fill="hsl({hue},55%,45%)"/>
                <text x="64" y="82" font-family="sans-serif" font-size="56" font-weight="700"
                      fill="white" text-anchor="middle">{sym}</text>
              </svg>
              """;
    return Results.Text(svg, "image/svg+xml", Encoding.UTF8);
});
```

It is anonymous here so the demo is curl/browser-able without a token; production gates it with
`.RequireAuthorization()` and lets a Sleipnir controller decide *which* URL a given user may
fetch (the controller returns the URL + checks permission in the RPC flow; the `GET` delivers).
A browser can now do `<img src="https://localhost:5010/logos/BTC.svg">` — cacheable, CDN-friendly,
no RPC envelope. That is the intended split, not a gap.

## 5. Hardening knobs (north-bound)

Before exposing the app to untrusted clients, flip these on `SleipnirOptions` (see
`SECURITY_GUIDE.md` for the full treatment):

| Knob | Default | What it does |
|------|---------|--------------|
| `RequireAuthentication` | `false` | North-bound default-deny: every call needs an authenticated user; `[SleipnirAnonymous]` opts out (e.g. Health/Ping). Also gates `/discovery`, `/observability`, `/metrics`, the SSE event endpoints, and the WS upgrade. |
| `MaximumBatchSize` | `0` (unlimited) | Caps requests per batch — protects against fan-out DoS (a 1-MB body of thousands of `Task.WhenAll` calls). Enforced at batch entry + the multi-endpoints as an early `400`. |
| `RateLimitPermitLimit` / `RateLimitWindowSeconds` | `0` (off) / `10` | Fixed-window rate limit per client. `0` = unlimited. |
| `MaxParameterArrayLength` | `1000` | Caps an array/collection parameter — protects against cardinality blow-up in the `@alias` whole-collection passthrough (server-side cardinality evades body-size limits). |
| `MaxResultElementCount` | `10000` | Caps an array/collection return — prevents one result exhausting memory. |
| `MaxDependencyPathLength` / `AllowRecursiveDescent` | `256` / `true` | Caps the client-controlled JsonPath in a `dependencyMapping` (a long `$..` can CPU-stall JsonPath.Net). |

`RequireAuthentication = false` stays in the guide (Market is public; per-method
`[SleipnirAuthorise]` gates the authed surface — chapter 8). A real north-bound deployment
flips it to `true` and adds `[SleipnirAnonymous]` to the handful of genuinely public methods.

## Try it

```bash
# terminal 1 — the API (now with interceptor + /observability + tracing + /logos)
dotnet run --project guide/server
# console shows: [trace] SleipnirCall … per call, and RPC … [correlationId] … per single call
```

```bash
# 1. Interceptor — single call: the X-Correlation-Id header is echoed back + a console log line
curl -sk -i -X POST https://localhost:5010/api/sleipnir/json -H "Content-Type: application/json" \
  -d '{"controller":"Market","method":"GetQuote","params":[{"parameterName":"symbol","data":"BTC"}],"id":"1"}' \
  | grep -i "X-Correlation-Id"
# X-Correlation-Id: 2a4384400217
# (console: RPC Market.GetQuote [2a4384400217] -> 200 in 10ms)

# Send your own correlation id — it is echoed, not replaced:
curl -sk -i -X POST https://localhost:5010/api/sleipnir/json -H "Content-Type: application/json" \
  -H "X-Correlation-Id: trace-42" \
  -d '{"controller":"Market","method":"GetQuote","params":[{"parameterName":"symbol","data":"ETH"}],"id":"1"}' \
  | grep -i "X-Correlation-Id"
# X-Correlation-Id: trace-42

# 2. Observability JSON snapshot
curl -sk https://localhost:5010/api/sleipnir/observability
# {"transports":{"rest":true,"webSocket":true,"signalR":true,"sse":true},
#  "activeConnections":0,"activeSubscriptions":0,"eventDroppedTotal":0,
#  "callCount":1,"errorCount":0,"batchCount":0,"uptimeMs":5559}

# 3. Tracing — make a call, watch the server console for:
#    [trace] SleipnirCall Market.GetQuote -> Ok 14.6ms

# 4. The batch caveat — a batch gets NO X-Correlation-Id (interceptor bypassed),
#    but the SleipnirBatch span STILL fires (tracing is in the invoker, not the pipeline):
curl -sk -i -X POST https://localhost:5010/api/sleipnir/json/multi -H "Content-Type: application/json" \
  -d '{"requests":[{"controller":"Market","method":"GetQuote","params":[{"parameterName":"symbol","data":"BTC"}],"id":"a"}],"mode":1}' \
  | grep -i "X-Correlation-Id" || echo "(no header — interceptor is single-call only)"
# (console still shows: [trace] SleipnirBatch ?.? -> Unset 5.6ms)

# 5. Media — the co-hosted GET (plain ASP.NET, not RPC). Browser-fetchable, right Content-Type:
curl -sk -i https://localhost:5010/logos/BTC.svg | grep -i "Content-Type"
# Content-Type: image/svg+xml; charset=utf-8
curl -sk https://localhost:5010/logos/ETH.svg
# <svg xmlns="http://www.w3.org/2000/svg" …><rect … fill="hsl(160,55%,45%)"/>…<text …>ETH</text></svg>
```

## Where this leaves you

The 3-tier app is now production-shaped: an interceptor logs every single call with a
correlation id; `/observability` reports live state; `ActivitySource("Sleipnir")` traces every
call (single and batch) and the guide makes it visible with no package; media rides a
co-hosted `GET`; and the hardening knobs are documented for the north-bound flip. You started
with one `Market.GetQuote` in chapter 1 and ended with a real multi-tier Sleipnir app — API,
Blazor Pflege-Backend, Svelte Endkunden-Portal — covering codegen, batching, `@alias`
chaining, LINQ, JWT auth, and a live resumable event feed.

From here, the feature references go deeper:

- **Full feature reference** — [`README_DETAILS.md`](../README_DETAILS.md) (incl. "Serving Media
  & Non-RPC Resources", the authority/delivery split).
- **Wire format + casing** — [`PROTOCOL.md`](../PROTOCOL.md).
- **Auth + hardening + north-bound** — [`SECURITY_GUIDE.md`](../SECURITY_GUIDE.md).
- **Alias binding, failure propagation, binding modes** — [`DEPENDENCY_BINDING.md`](../DEPENDENCY_BINDING.md).
- **Build-time contract + typed clients** — [`CODEGEN_ONBOARDING.md`](../CODEGEN_ONBOARDING.md).
- **LINQ provider** — [`LINQ_QUERY.md`](../LINQ_QUERY.md).

This was the final chapter. The whole story starts at [`Chapter 1`](01-onboarding.md).