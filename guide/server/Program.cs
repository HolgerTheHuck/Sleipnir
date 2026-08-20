// ==============================================================================
// Story.Api — the Sleipnir Guide API server (the backend tier of the 3-tier app).
//
// Three lines of Sleipnir wiring (AddSleipnir -> UseSleipnirTransports -> MapSleipnir)
// serve REST + WebSocket (+ optional SignalR) + the Developer UI. [SleipnirController]
// types in this project are auto-discovered at startup — no manual registration.
//
// Chapter 8 adds standard ASP.NET JWT bearer auth. Sleipnir reads ONLY HttpContext.User;
// ASP.NET auth populates it, and [SleipnirAuthorise] enforces per-method rules on top. The
// ordering is load-bearing: UseAuthentication/UseAuthorization must run BEFORE
// UseSleipnirTransports so HttpContext.User is set when the WS upgrade gate and the
// invoker's CheckAuthorisation read it.
//
//   dotnet run --project guide/server
//     REST          https://localhost:5010/api/sleipnir/json   (+ /multi, /discovery)
//     WebSocket     wss://localhost:5010/sleipnirws
//     Developer UI  https://localhost:5010/Sleipnir
//
// (HTTPS needs a one-time `dotnet dev-certs https --trust`.)
// ==============================================================================

using System.Diagnostics;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Sleipnir.Guide.Api.Interceptors;
using Sleipnir.Guide.Api.Services;
using SleipnirCore.Services;     // ISleipnirInterceptor (custom interceptor registration)
using SleipnirCore.Tracing;      // SleipnirTracing.ActivitySourceName (chapter 10)
using SleipnirHub.Extensions;     // AddSleipnir
using SleipnirServer;            // UseSleipnirTransports, MapSleipnir

var builder = WebApplication.CreateBuilder(args);

// Serve the built Developer UI static assets in Development (production injects them
// automatically via MapSleipnir).
if (builder.Environment.IsDevelopment())
{
    builder.WebHost.UseStaticWebAssets();
}

// CORS open for the browser clients (the Svelte portal on :5173 and the plain HTML page).
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .AllowAnyOrigin()
    .AllowAnyHeader()
    .AllowAnyMethod()));

builder.Services.AddSleipnir(options =>
{
    options.EnableDetailedErrors = builder.Environment.IsDevelopment();
    // RequireAuthentication stays false: Market.GetQuote/GetQuotes/Search are public (the
    // portal shows quotes before login). Per-method [SleipnirAuthorise] on Account/Portfolio
    // gates the authed surface instead. (Setting this true would also turn on the
    // connection-level WS/SSE default-deny gate — not what we want for a public Market.)

    // Chapter 9: turn on the SignalR event transport (opt-in, default false) so the portal can
    // `useTransport("signalr")` and the admin can exercise the hub-streaming path. MessagePack
    // gives the hub a binary wire (smaller frames); JSON is the fallback protocol either way.
    options.UseSignalR = true;
    options.UseMessagePack = true;

    // Chapter 10: opt-in JSON observability snapshot — GET /api/sleipnir/observability returns
    // live transport/runtime counters (active connections/subscriptions, call/error/batch
    // totals, dropped events, uptime) for the DevUI Observability panel and ad-hoc curl checks.
    // (The Prometheus-text /metrics scrape endpoint is wired separately below — it is NOT a
    // SleipnirOptions flag; it lives in the Sleipnir.Telemetry package.)
    options.EnableObservability = true;
});

// --- Chapter 10: tracing + custom interceptor --------------------------------------
// Sleipnir instruments every call with an always-on ActivitySource named "Sleipnir"
// (SleipnirCore.Tracing.SleipnirTracing.ActivitySourceName) — single-call, batch dispatcher, and
// per-element spans named SleipnirCall/SleipnirBatch. It is cost-neutral: StartActivity returns
// null when no listener is subscribed, so the instrumentation is free until you opt in.
//
// Production opts in via the Sleipnir.Telemetry package: `AddSleipnirTelemetry(o => o.Exporter =
// Otlp)` boots the OpenTelemetry SDK, subscribes the "Sleipnir" source + meter, and exports to a
// Collector (→ Grafana / Heimdall / Jaeger); `AddSleipnirPrometheusMetrics()` +
// `UseSleipnirPrometheusScrapingEndpoint()` mount a Prometheus-text /api/sleipnir/metrics scrape
// endpoint. (The guide server does NOT reference Sleipnir.Telemetry — that package pulls the
// OpenTelemetry deps, which the build-time contract-export tool reflects against and currently
// cannot resolve. So the guide demonstrates the SAME tracing surface with a package-free
// ActivityListener instead, and documents the OTel-SDK path below as the production opt-in.)
//
// This tiny listener subscribes to the "Sleipnir" source and writes each span to the console —
// visible in `dotnet run` output. It is the raw System.Diagnostics API (no package); the same
// mechanism the Sleipnir tracing tests use. It proves the instrumentation is live without an OTel SDK.
ActivityListener consoleTraceListener = new()
{
    ShouldListenTo = source => source.Name == SleipnirTracing.ActivitySourceName,
    Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
    ActivityStopped = activity =>
    {
        var svc = activity.GetTagItem("rpc.service") as string ?? "?";
        var mth = activity.GetTagItem("rpc.method") as string ?? "?";
        var status = activity.Status;
        Console.WriteLine($"[trace] {activity.DisplayName} {svc}.{mth} → {status}" +
                          (status == ActivityStatusCode.Error
                              ? $" ({activity.StatusDescription ?? "error"})"
                              : $" {activity.Duration.TotalMilliseconds:F1}ms"));
    },
};
ActivitySource.AddActivityListener(consoleTraceListener);

// Custom interceptor — registered AFTER AddSleipnir so DI appends it after the built-in
// interceptors (Auth → Telemetry → Logging). The pipeline runs in reverse registration order, so
// this is the OUTERMOST interceptor: it wraps Auth and Logging, seeing every single call. Single-
// call path only (batches bypass user interceptors in 1.1.x — see CorrelationIdInterceptor docs).
builder.Services.AddSingleton<ISleipnirInterceptor, CorrelationIdInterceptor>();

// --- Chapter 8: standard ASP.NET JWT bearer auth -------------------------------------
// Sleipnir does NOT register IHttpContextAccessor; controllers that read the caller's
// claims (Account.Me, Portfolio.GetHoldings/PlaceOrder) need it, so we add it here.
builder.Services.AddHttpContextAccessor();

// The AccountService (issuer) and FeedControlService (the admin-gated feed toggle chapter 9
// reads) are singletons — resolved per-call into controllers via the invoker's DI scope.
builder.Services.AddSingleton<AccountService>();
builder.Services.AddSingleton<FeedControlService>();

// Chapter 9: the live price feed owns the HotObservable<PriceTick> streams + the random-walk
// timer. Registered as a singleton so PriceFeedController can inject it, AND as a hosted service
// (resolving the SAME singleton) so it starts/stops with the host. AddHostedService<T>() alone
// does not register T as an injectable type — the dual registration makes one instance both the
// long-lived feed source the controller yields and the IHostedService the host runs.
builder.Services.AddSingleton<PriceFeedService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PriceFeedService>());

// JWT validation. Issuance uses the SAME symmetric key (AccountService.SecurityKey), issuer
// and audience — the two sides must agree. RoleClaimType = ClaimTypes.Role so
// ClaimsPrincipal.IsInRole("Admin") matches the role claim AccountService issues (that is what
// [SleipnirAuthorise(Role = "Admin")] checks).
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = AccountService.Issuer,
            ValidateAudience = true,
            ValidAudience = AccountService.Audience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = AccountService.SecurityKey,
            RoleClaimType = System.Security.Claims.ClaimTypes.Role,
        };
    });
builder.Services.AddAuthorization();

// Fixed URL so every chapter's snippets (https://localhost:5010 / wss://.../sleipnirws)
// work out of the box.
builder.WebHost.UseUrls("https://localhost:5010");

var app = builder.Build();

app.UseCors();
app.UseRouting();

// Auth BEFORE the Sleipnir transports: populates HttpContext.User for the invoker
// (CheckAuthorisation) and the transport gates (WS upgrade / SSE / discovery).
app.UseAuthentication();
app.UseAuthorization();

// Sleipnir transport middleware (WebSocket primary) + controller registration.
app.UseSleipnirTransports();

// REST (/api/sleipnir) + Developer UI (/Sleipnir) + SignalR hub (/sleipnirhub) when enabled.
// (Chapter 10: the JSON /observability snapshot endpoint is mapped here when
//  EnableObservability is true above. The Prometheus-text /api/sleipnir/metrics scrape endpoint
//  is the production path — wired by the Sleipnir.Telemetry package's
//  UseSleipnirPrometheusScrapingEndpoint(); see the chapter for the opt-in snippet.)
app.MapSleipnir();

// --- Chapter 10: the blessed media pattern — a co-hosted plain HTTP GET -------------------
// Sleipnir is command-oriented RPC; media is resource-oriented. The production pattern for a
// browser-fetchable resource is NOT a [SleipnirMethod] returning byte[] (binary in the RPC
// envelope is fine for SMALL in-call bytes — a thumbnail, a hash — but a browser <img>/CDN wants
// a plain GET URL with the right Content-Type, caching, and Range). The split: Sleipnir is the
// AUTHORITY (metadata, permission, "which URL"); a co-hosted Minimal-API GET is the DELIVERY.
// One host, one DI container, one auth pipeline — not two frameworks. See README_DETAILS.md →
// "Serving Media & Non-RPC Resources". This endpoint renders a deterministic SVG badge per
// symbol; it is anonymous here so the demo is curl/browser-able without a token — production
// would gate it with .RequireAuthorization() and let a Sleipnir controller decide the URL.
app.MapGet("/logos/{symbol}.svg", (string symbol) =>
{
    var sym = symbol.ToUpperInvariant();
    // A deterministic color from the symbol so each coin has a stable badge.
    var hue = Math.Abs(sym.GetHashCode()) % 360;
    var svg = $"""
              <svg xmlns="http://www.w3.org/2000/svg" width="128" height="128" viewBox="0 0 128 128">
                <rect width="128" height="128" rx="24" fill="hsl({hue},55%,45%)"/>
                <text x="64" y="82" font-family="sans-serif" font-size="56" font-weight="700"
                      fill="white" text-anchor="middle">{sym}</text>
              </svg>
              """;
    return Results.Text(svg, "image/svg+xml", Encoding.UTF8);
});

app.Run();