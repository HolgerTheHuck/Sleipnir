using TrameCommon;
using TrameCommon.MessagePack;
using TrameCore.Attributes;
using TrameCore.Services;
using MessagePack;
using Microsoft.AspNetCore.RateLimiting;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.RateLimiting;

namespace TrameHub.Extensions
{
    public static class TrameServiceCollectionExtension
    {
        /// <summary>
        /// Registriert Trame mit Konfigurations-Callback — das ist der empfohlene
        /// Einstiegspunkt für eine v1.0-Server-Setup. WebSocket ist der primäre
        /// (Default-)Kanal; SignalR ist über <see cref="TrameOptions.UseSignalR"/>
        /// optional zuschaltbar. Die Pipeline muss anschließend
        /// <c>app.UseWebSockets(); app.UseTrameWebSocket(); app.UseTrame();</c>
        /// (und bei Bedarf <c>app.MapTrameEndpoints()</c>) aufrufen.
        ///
        /// Usage:
        /// <code>
        /// builder.Services.AddTrame();                       // Defaults (WS primär)
        /// // oder
        /// builder.Services.AddTrame(o =&gt;
        /// {
        ///     o.EnableDetailedErrors = builder.Environment.IsDevelopment();
        ///     o.RateLimitPermitLimit = 50;                  // nur in Prod
        ///     o.UseSignalR = true;                          // optionaler 2. Kanal
        /// });
        /// </code>
        /// </summary>
        public static IServiceCollection AddTrame(this IServiceCollection services,
            Action<TrameOptions>? configure = null)
        {
            var options = new TrameOptions();
            configure?.Invoke(options);
            return services.AddTrame(options);
        }

        public static IServiceCollection AddTrame(this IServiceCollection services,
            TrameOptions options)
        {
            // Options als Singleton in DI ablegen, damit die Pipeline-Extensions
            // (UseTrameTransports/MapTrame) UseSignalR etc. ohne Parameter lesen können.
            services.AddSingleton(options);

            if (options.UseSignalR)
            {
                // Add SignalR as the transport-layer.
                // Die drei HubOptions sind nur dann zu überschreiben, wenn der Caller sie
                // explizit gesetzt hat — sonst gilt jeweils der SignalR-Default. Insbesondere
                // MaximumParallelInvocationsPerClient=0 (TrameOptions-int-Default) würde SignalR
                // beim Build des HubConnectionHandler werfen (must be ≥ 1); wir lassen in dem
                // Fall den SignalR-Default (1) stehen, statt ihn mit 0 zu überschreiben.
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
                    // Custom Resolver: JsonElement (TrameResponse.Data seit dem Single-Pass-
                    // Fix) wird als native MessagePack-Tokens serialisiert, nicht als
                    // escapeter JSON-String — keine Double-Wrapping-Tax auf dem SignalR-Kanal.
                    fastHub.AddMessagePackProtocol(o =>
                        o.SerializerOptions = MessagePackSerializerOptions.Standard
                            .WithResolver(JsonElementResolver.Instance));
                }
            }

            // Minimal-API (REST) JSON-Options host-weit konfigurieren: camelCase +
            // relaxed Encoder. Wirkt auf ALLE Minimal-API-Endpoints des Hosts (Trame ist
            // das Framework, das den Host bereitstellt) — Data (JsonElement) wird damit
            // roh in einem Pass serialisiert, ohne `"`-Escape-Tax.
            // TrameResponseJsonConverter (Write-only): DataBytes via WriteRawValue roh in
            // den Wire → kein JsonDocument-Baum auf dem Server (Single-Pass-Optimierung).
            services.ConfigureHttpJsonOptions(o =>
            {
                o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                o.SerializerOptions.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
                o.SerializerOptions.Converters.Add(new TrameResponseJsonConverter());
            });

            // Rate Limiting – always register to ensure UseRateLimiter works.
            services.AddRateLimiter(rateLimiterOptions =>
            {
                if (options.RateLimitPermitLimit > 0)
                {
                    rateLimiterOptions.AddFixedWindowLimiter("trame", opt =>
                    {
                        opt.PermitLimit = options.RateLimitPermitLimit;
                        opt.Window = TimeSpan.FromSeconds(options.RateLimitWindowSeconds);
                        opt.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
                        opt.QueueLimit = 0;
                    });
                    rateLimiterOptions.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                }
            });

            // Add the TrameService
            // Singleton for performance-reasons.
            // The TrameService will create a scope for the controllers.
            services.AddSingleton<ITrameCore>(sp =>
            {
                var invoker = new TrameCore.Services.TrameInvoker(
                    sp.GetRequiredService<IServiceScopeFactory>(),
                    sp.GetService<Microsoft.Extensions.Logging.ILogger<TrameCore.Services.TrameInvoker>>()
                        ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<TrameCore.Services.TrameInvoker>.Instance,
                    sp.GetService<IEnumerable<TrameCore.Services.ITrameInterceptor>>());

                // Detailed Errors aktivieren, wenn explizit gewünscht oder in Development.
                var env = sp.GetService<Microsoft.Extensions.Hosting.IHostEnvironment>();
                invoker.EnableDetailedErrors = options.EnableDetailedErrors || (env?.IsDevelopment() ?? false);
                // Kardinalitäts-Caps durchreichen (Default 1000/10000, 0 = unbegrenzt).
                invoker.MaxParameterArrayLength = options.MaxParameterArrayLength;
                invoker.MaxResultElementCount = options.MaxResultElementCount;
                // Alias-Binding-Modus durchreichen (Default Weak; Strict = Fragment muss
                // den Consumer-Typ vollständig decken, sonst 400 — siehe DEPENDENCY_BINDING.md).
                invoker.AliasBindingMode = options.AliasBindingMode;
                // North-Bound-Härtung (Default non-breaking, siehe SECURITY.md):
                // RequireAuthentication = Default-Deny für unbestückte Methoden;
                // MaximumBatchSize = Fan-Out-DoS-Cap; JsonPath-Limits = client-pfad-DoS-Cap.
                invoker.RequireAuthentication = options.RequireAuthentication;
                invoker.MaximumBatchSize = options.MaximumBatchSize;
                invoker.MaxDependencyPathLength = options.MaxDependencyPathLength;
                invoker.AllowRecursiveDescent = options.AllowRecursiveDescent;

                // Hotfix 1.1.1: Policy-Evaluator für den Batch-Pfad setzen, falls
                // IAuthorizationService verfügbar. Der Delegate kapselt die ASP.NET Core
                // Authorization-Abhängigkeit, so dass TrameCore frei davon bleibt.
                // Im Single-Call-Pfad übernimmt der TrameAuthorizationInterceptor die
                // Policy-Evaluation; im Batch-Pre-Pass nutzt CheckAuthorisation diesen Delegate.
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

            // Register built-in interceptors (Phase 1: feste Reihenfolge Auth → Logging).
            // Auth *vor* Logging → Auth läuft außen, lehnt unautorisierte Calls ab, bevor
            // der Logging-Interceptor sie misst (sonst loggen wir unautorisierten Traffic).
            // User-Interceptors aus options.Interceptors kommen *nach* den Built-ins
            // (innen, näher an der Method-Invocation) — sie können auf gelöste InvokeInfo
            // und autorisierte Requests aufsetzen.
            if (options.RegisterBuiltInInterceptors)
            {
                // Auth (außen) — IAuthorizationService ist optional (South-Bound ohne
                // ASP.NET Core Authorization); Policies werden nur ausgewertet, wenn
                // registriert. RequireAuthentication wird via Closure durchgereicht.
                services.AddSingleton<TrameCore.Services.ITrameInterceptor>(
                    sp => new TrameHub.Interceptors.TrameAuthorizationInterceptor(
                        sp.GetService<Microsoft.AspNetCore.Authorization.IAuthorizationService>(),
                        sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<TrameHub.Interceptors.TrameAuthorizationInterceptor>>(),
                        options.RequireAuthentication));

                // Telemetry (Mitte) — Tracing + Metrics + OTel-Logging-Conventions.
                // Läuft nach Auth (misst nur autorisierten Traffic) und vor Logging
                // (außen, umschließt die Method-Invocation mit der Pipeline-Span).
                services.AddSingleton<TrameCore.Services.ITrameInterceptor>(
                    sp => new TrameHub.Interceptors.TrameTelemetryInterceptor(
                        sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<TrameHub.Interceptors.TrameTelemetryInterceptor>>()));

                // Logging (innen, nach Telemetry) — der bestehende Built-in aus v1.0
                // (Dauer-Logger, bleibt als einfacher Logger erhalten).
                services.AddSingleton<TrameCore.Services.ITrameInterceptor, TrameCore.Services.TrameLoggingInterceptor>();
            }

            // User-Interceptors (nach Built-ins → innen). Die Reihenfolge in der
            // Collection bleibt erhalten; IEnumerable<ITrameInterceptor> liefert sie
            // in DI-Registrierungsreihenfolge, und die Pipeline baut reversed (letzter
            // läuft zuerst). Built-ins (oben) sind zuerst registriert → sie laufen außen.
            foreach (var userInterceptor in options.Interceptors)
            {
                services.AddSingleton<TrameCore.Services.ITrameInterceptor>(_ => userInterceptor);
            }
            foreach (var userBatchInterceptor in options.BatchInterceptors)
            {
                services.AddSingleton<TrameCore.Services.ITrameBatchInterceptor>(_ => userBatchInterceptor);
            }

            // Register Fast-Controller as Scoped.
            // AutoDiscover=false-Controller bleiben dem Bulk-Scan bewusst fern — sie
            // werden nur auf explizite Registrierung (Builder/Add<T> oder Register<T>) hin DI-registriert.
            // AutoDiscoverControllers=false (fluent Builder-Pfad) schaltet den Bulk-Scan ab; der
            // Builder registriert seine Controller dann selbst bei DI (siehe TrameControllerBuilder).
            if (options.AutoDiscoverControllers)
            {
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    foreach (Type type in TypeScanning.SafeGetTypes(assembly))
                    {
                        var attr = type.GetCustomAttributes(typeof(TrameControllerAttribute), true)
                            .OfType<TrameControllerAttribute>().FirstOrDefault();
                        if (attr != null && attr.AutoDiscover)
                        {
                            services.AddScoped(type);
                        }
                    }
                }
            }

            return services;
        }

        public static IApplicationBuilder UseTrame(
            this IApplicationBuilder app)
        {
            var trameService = app.ApplicationServices.GetService<TrameCore.Services.ITrameCore>();
            if (trameService == null)
            {
                return app;
            }

            // If a TrameControllerBuilder was registered (fluent API), use it
            var builder = app.ApplicationServices.GetService<TrameControllerBuilder>();
            if (builder != null)
            {
                builder.Apply(app.ApplicationServices, trameService);
                return app;
            }

            // Fallback: register all auto-discovered [TrameController] types.
            // AutoDiscover=false-Controller (z. B. bewusst invalide Test-Fixtures)
            // werden hier übersprungen — sie registriert man nur explizit.
            // AutoDiscoverControllers=false ohne fluent Builder wäre ein Setup-Fehler
            // (keine Controller registriert) — wir respektieren es trotzdem, damit der
            // Flag in beiden Schichten (DI-Registrierung oben, Invoker-Registrierung hier) konsistent ist.
            var options = app.ApplicationServices.GetService<TrameOptions>();
            if (options is { AutoDiscoverControllers: false })
                return app;

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                foreach (Type type in TypeScanning.SafeGetTypes(assembly))
                {
                    var attr = type.GetCustomAttributes(typeof(TrameControllerAttribute), true)
                        .OfType<TrameControllerAttribute>().FirstOrDefault();
                    if (attr != null && attr.AutoDiscover)
                    {
                        trameService.Register(type);
                    }
                }
            }

            return app;
        }
    }
}
