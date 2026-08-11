using SleipnirCommon;
using SleipnirCommon.MessagePack;
using SleipnirCore.Attributes;
using SleipnirCore.Services;
using MessagePack;
using Microsoft.AspNetCore.RateLimiting;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.RateLimiting;

namespace SleipnirHub.Extensions
{
    public static class SleipnirServiceCollectionExtension
    {
        /// <summary>
        /// Registers Sleipnir with a configuration callback — this is the recommended
        /// entry point for a v1.0 server setup. WebSocket is the primary
        /// (default) channel; SignalR can be optionally enabled via
        /// <see cref="SleipnirOptions.UseSignalR"/>. The pipeline must subsequently call
        /// <c>app.UseWebSockets(); app.UseSleipnirWebSocket(); app.UseSleipnir();</c>
        /// (and <c>app.MapSleipnirEndpoints()</c> as needed).
        ///
        /// Usage:
        /// <code>
        /// builder.Services.AddSleipnir();                       // Defaults (WS primary)
        /// // or
        /// builder.Services.AddSleipnir(o =&gt;
        /// {
        ///     o.EnableDetailedErrors = builder.Environment.IsDevelopment();
        ///     o.RateLimitPermitLimit = 50;                  // only in prod
        ///     o.UseSignalR = true;                          // optional second channel
        /// });
        /// </code>
        /// </summary>
        public static IServiceCollection AddSleipnir(this IServiceCollection services,
            Action<SleipnirOptions>? configure = null)
        {
            var options = new SleipnirOptions();
            configure?.Invoke(options);
            return services.AddSleipnir(options);
        }

        public static IServiceCollection AddSleipnir(this IServiceCollection services,
            SleipnirOptions options)
        {
            // Store options as a singleton in DI so that the pipeline extensions
            // (UseSleipnirTransports/MapSleipnir) can read UseSignalR etc. without parameters.
            services.AddSingleton(options);

            if (options.UseSignalR)
            {
                // Add SignalR as the transport-layer.
                // Override the three HubOptions only when the caller has set them explicitly —
                // otherwise the SignalR default applies. In particular,
                // MaximumParallelInvocationsPerClient=0 (the SleipnirOptions int default) would
                // make SignalR throw when building the HubConnectionHandler (must be ≥ 1); in
                // that case we leave the SignalR default (1) in place instead of overriding it with 0.
                var fastHub = services.AddSignalR(
                    o =>
                    {
                        o.EnableDetailedErrors = options.EnableDetailedErrors;
                        if (options.MaximumReceiveMessageSize is { } maxMsg && maxMsg > 0)
                            o.MaximumReceiveMessageSize = maxMsg;
                        if (options.StreamBufferCapacity is { } cap && cap > 0)
                            o.StreamBufferCapacity = cap;
                        if (options.MaximumParallelInvocationsPerClient > 0)
                            o.MaximumParallelInvocationsPerClient = options.MaximumParallelInvocationsPerClient;
                    });

                if (options.UseMessagePack)
                {
                    // Custom Resolver: JsonElement (SleipnirResponse.Data since the single-pass
                    // fix) is serialized as native MessagePack tokens rather than as an
                    // escaped JSON string — no double-wrapping tax on the SignalR channel.
                    fastHub.AddMessagePackProtocol(o =>
                        o.SerializerOptions = MessagePackSerializerOptions.Standard
                            .WithResolver(JsonElementResolver.Instance));
                }
            }

            // Configure Minimal-API (REST) JSON options host-wide: camelCase +
            // relaxed encoder. Affects ALL Minimal-API endpoints of the host (Sleipnir is
            // the framework that provides the host) — Data (JsonElement) is then
            // serialized raw in a single pass, without a `"`-escape tax.
            // SleipnirResponseJsonConverter (write-only): writes DataBytes raw to the
            // wire via WriteRawValue → no JsonDocument tree on the server (single-pass optimization).
            services.ConfigureHttpJsonOptions(o =>
            {
                o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                o.SerializerOptions.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
                o.SerializerOptions.Converters.Add(new SleipnirResponseJsonConverter());
            });

            // Rate Limiting – always register to ensure UseRateLimiter works.
            services.AddRateLimiter(rateLimiterOptions =>
            {
                if (options.RateLimitPermitLimit > 0)
                {
                    rateLimiterOptions.AddFixedWindowLimiter("sleipnir", opt =>
                    {
                        opt.PermitLimit = options.RateLimitPermitLimit;
                        opt.Window = TimeSpan.FromSeconds(options.RateLimitWindowSeconds);
                        opt.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
                        opt.QueueLimit = 0;
                    });
                    rateLimiterOptions.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                }
            });

            // Add the SleipnirService
            // Singleton for performance-reasons.
            // The SleipnirService will create a scope for the controllers.
            services.AddSingleton<ISleipnirCore>(sp =>
            {
                var invoker = new SleipnirCore.Services.SleipnirInvoker(
                    sp.GetRequiredService<IServiceScopeFactory>(),
                    sp.GetService<Microsoft.Extensions.Logging.ILogger<SleipnirCore.Services.SleipnirInvoker>>()
                        ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<SleipnirCore.Services.SleipnirInvoker>.Instance,
                    sp.GetService<IEnumerable<SleipnirCore.Services.ISleipnirInterceptor>>());

                // Enable detailed errors when explicitly requested or in Development.
                var env = sp.GetService<Microsoft.Extensions.Hosting.IHostEnvironment>();
                invoker.EnableDetailedErrors = options.EnableDetailedErrors || (env?.IsDevelopment() ?? false);
                // Propagate the cardinality caps (default 1000/10000, 0 = unlimited).
                invoker.MaxParameterArrayLength = options.MaxParameterArrayLength;
                invoker.MaxResultElementCount = options.MaxResultElementCount;
                // Propagate the alias binding mode (default Weak; Strict = fragment must
                // fully cover the consumer type, otherwise 400 — see DEPENDENCY_BINDING.md).
                invoker.AliasBindingMode = options.AliasBindingMode;
                // North-bound hardening (default non-breaking, see SECURITY.md):
                // RequireAuthentication = default-deny for unattributed methods;
                // MaximumBatchSize = fan-out DoS cap; JsonPath limits = client-path DoS cap.
                invoker.RequireAuthentication = options.RequireAuthentication;
                invoker.MaximumBatchSize = options.MaximumBatchSize;
                invoker.MaxDependencyPathLength = options.MaxDependencyPathLength;
                invoker.AllowRecursiveDescent = options.AllowRecursiveDescent;

                // Hotfix 1.1.1: set the policy evaluator for the batch path if
                // IAuthorizationService is available. The delegate encapsulates the ASP.NET Core
                // Authorization dependency so that SleipnirCore stays free of it.
                // On the single-call path the SleipnirAuthorizationInterceptor handles
                // policy evaluation; in the batch pre-pass CheckAuthorisation uses this delegate.
                var authService = sp.GetService<Microsoft.AspNetCore.Authorization.IAuthorizationService>();
                if (authService != null)
                {
                    invoker.PolicyEvaluator = async (ctx, policy) =>
                    {
                        var result = await authService.AuthorizeAsync(ctx.User, resource: null, policyName: policy);
                        return result.Succeeded;
                    };
                }

                return invoker;
            });

            // Register built-in interceptors (Phase 1: fixed order Auth → Logging).
            // Auth *before* Logging → Auth runs on the outside, rejects unauthorized calls
            // before the logging interceptor measures them (otherwise we log unauthorized traffic).
            // User interceptors from options.Interceptors come *after* the built-ins
            // (inside, closer to the method invocation) — they can build on resolved InvokeInfo
            // and authorized requests.
            if (options.RegisterBuiltInInterceptors)
            {
                // Auth (outer) — IAuthorizationService is optional (south-bound without
                // ASP.NET Core Authorization); policies are only evaluated when
                // registered. RequireAuthentication is propagated via the closure.
                services.AddSingleton<SleipnirCore.Services.ISleipnirInterceptor>(
                    sp => new SleipnirHub.Interceptors.SleipnirAuthorizationInterceptor(
                        sp.GetService<Microsoft.AspNetCore.Authorization.IAuthorizationService>(),
                        sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SleipnirHub.Interceptors.SleipnirAuthorizationInterceptor>>(),
                        options.RequireAuthentication));

                // Telemetry (middle) — tracing + metrics + OTel logging conventions.
                // Runs after Auth (measures only authorized traffic) and before Logging
                // (outer, wraps the method invocation with the pipeline span).
                services.AddSingleton<SleipnirCore.Services.ISleipnirInterceptor>(
                    sp => new SleipnirHub.Interceptors.SleipnirTelemetryInterceptor(
                        sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SleipnirHub.Interceptors.SleipnirTelemetryInterceptor>>()));

                // Logging (inner, after Telemetry) — the existing built-in from v1.0
                // (duration logger, kept as a simple logger).
                services.AddSingleton<SleipnirCore.Services.ISleipnirInterceptor, SleipnirCore.Services.SleipnirLoggingInterceptor>();
            }

            // User interceptors (after built-ins → inner). The order in the
            // collection is preserved; IEnumerable<ISleipnirInterceptor> yields them
            // in DI registration order, and the pipeline is built reversed (last
            // runs first). Built-ins (above) are registered first → they run outer.
            foreach (var userInterceptor in options.Interceptors)
            {
                services.AddSingleton<SleipnirCore.Services.ISleipnirInterceptor>(_ => userInterceptor);
            }
            foreach (var userBatchInterceptor in options.BatchInterceptors)
            {
                services.AddSingleton<SleipnirCore.Services.ISleipnirBatchInterceptor>(_ => userBatchInterceptor);
            }

            // Register Fast-Controller as Scoped.
            // AutoDiscover=false controllers deliberately stay out of the bulk scan — they
            // are DI-registered only on explicit registration (Builder/Add<T> or Register<T>).
            // AutoDiscoverControllers=false (fluent builder path) disables the bulk scan; the
            // builder then registers its controllers with DI itself (see SleipnirControllerBuilder).
            if (options.AutoDiscoverControllers)
            {
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    foreach (Type type in TypeScanning.SafeGetTypes(assembly))
                    {
                        var attr = type.GetCustomAttributes(typeof(SleipnirControllerAttribute), true)
                            .OfType<SleipnirControllerAttribute>().FirstOrDefault();
                        if (attr != null && attr.AutoDiscover)
                        {
                            services.AddScoped(type);
                        }
                    }
                }
            }

            return services;
        }

        public static IApplicationBuilder UseSleipnir(
            this IApplicationBuilder app)
        {
            var sleipnirService = app.ApplicationServices.GetService<SleipnirCore.Services.ISleipnirCore>();
            if (sleipnirService == null)
            {
                return app;
            }

            // R5: ISleipnirInterceptor/ISleipnirBatchInterceptor run on the single-call path only in
            // 1.1.x — batch elements bypass the interceptor seam (authorization is unaffected;
            // it is enforced structurally). Warn once at startup when a user registered custom
            // interceptors, so the bypass does not stay silent. The real fix (route the batch path
            // through the pipeline) is tracked for 1.2 (ROADMAP.md R7).
            var startupOptions = app.ApplicationServices.GetService<SleipnirOptions>();
            if (startupOptions?.Interceptors.Count > 0 || startupOptions?.BatchInterceptors.Count > 0)
            {
                var loggerFactory = app.ApplicationServices.GetService<Microsoft.Extensions.Logging.ILoggerFactory>();
                loggerFactory?.CreateLogger("Sleipnir")
                    .LogWarning(
                        "Sleipnir: user interceptors (Interceptors={InterceptorCount}, BatchInterceptors={BatchInterceptorCount}) " +
                        "currently run on the single-call path only, not on batch request elements " +
                        "(/json/multi, WebSocket multi, JSON-RPC batch). Authorization is unaffected (enforced structurally), " +
                        "but any custom interceptor logic is bypassed on batches. Batch-pipeline routing is tracked for 1.2. " +
                        "See ROADMAP.md R7 and SECURITY_GUIDE.md.",
                        startupOptions.Interceptors.Count, startupOptions.BatchInterceptors.Count);
            }

            // If a SleipnirControllerBuilder was registered (fluent API), use it
            var builder = app.ApplicationServices.GetService<SleipnirControllerBuilder>();
            if (builder != null)
            {
                builder.Apply(app.ApplicationServices, sleipnirService);
                return app;
            }

            // Fallback: register all auto-discovered [SleipnirController] types.
            // AutoDiscover=false controllers (e.g. deliberately invalid test fixtures)
            // are skipped here — they are registered only explicitly.
            // AutoDiscoverControllers=false without a fluent builder would be a setup error
            // (no controllers registered) — we still honor it so the
            // flag stays consistent across both layers (DI registration above, invoker registration here).
            var options = app.ApplicationServices.GetService<SleipnirOptions>();
            if (options is { AutoDiscoverControllers: false })
                return app;

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                foreach (Type type in TypeScanning.SafeGetTypes(assembly))
                {
                    var attr = type.GetCustomAttributes(typeof(SleipnirControllerAttribute), true)
                        .OfType<SleipnirControllerAttribute>().FirstOrDefault();
                    if (attr != null && attr.AutoDiscover)
                    {
                        sleipnirService.Register(type);
                    }
                }
            }

            return app;
        }
    }
}
