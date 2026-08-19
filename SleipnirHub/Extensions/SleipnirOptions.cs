namespace SleipnirHub.Extensions
{
    public class SleipnirOptions
    {
        public bool EnableDetailedErrors { get; set; }

        public long? MaximumReceiveMessageSize { get; set; }

        public int? StreamBufferCapacity { get; set; }

        /// <summary>
        /// Per-subscription buffer capacity for server-push events
        /// (<c>[SleipnirEvent]</c> + <c>IObservable&lt;T&gt;</c>). When <c>null</c>
        /// (the default) the framework falls back to 100. A positive value caps each
        /// subscription's buffer at that many events; the overflow behavior is governed
        /// by <see cref="EventBackpressureStrategy"/>. Ignored when the strategy is
        /// <see cref="SleipnirCommon.Models.EventBackpressureStrategy.Unbounded"/>.
        /// Overridable per event via <c>[SleipnirEvent(BufferCapacity = …)]</c>.
        /// Experimental (Phase 3) — see <c>STABILITY.md</c> §2.
        /// </summary>
        public int? EventBufferCapacity { get; set; }

        /// <summary>
        /// Overflow strategy for a full per-subscription event buffer (default
        /// <see cref="SleipnirCommon.Models.EventBackpressureStrategy.DropOldest"/> —
        /// evict the oldest event, increment <c>sleipnir.event.dropped</c>).
        /// <see cref="SleipnirCommon.Models.EventBackpressureStrategy.DropWrite"/>
        /// drops the newest; <see cref="SleipnirCommon.Models.EventBackpressureStrategy.Block"/>
        /// back-pressures the producer (risky — see the enum doc);
        /// <see cref="SleipnirCommon.Models.EventBackpressureStrategy.Unbounded"/>
        /// disables the cap (no DoS backstop). Overridable per event via
        /// <c>[SleipnirEvent(BackpressureStrategy = …)]</c>.
        /// Experimental (Phase 3) — see <c>STABILITY.md</c> §2.
        /// </summary>
        public EventBackpressureStrategy EventBackpressureStrategy { get; set; } = EventBackpressureStrategy.DropOldest;

        public int MaximumParallelInvocationsPerClient { get; set; }

        public bool UseMessagePack { get; set; }

        public bool UseSignalR { get; set; }

        /// <summary>
        /// When <c>true</c> (default), <c>AddSleipnir</c> auto-discovers all
        /// <c>[SleipnirController]</c> types across the loaded assemblies (skipping any
        /// with <c>AutoDiscover = false</c>) and registers them as scoped services,
        /// and <c>UseSleipnir</c> registers them with the invoker. When <c>false</c>,
        /// auto-discovery is off — controllers are registered only through the
        /// fluent <c>SleipnirControllerBuilder</c> (which sets this to <c>false</c> for
        /// its call) or an explicit <c>Register&lt;T&gt;()</c>. Additive (default
        /// keeps current behavior). See <c>STABILITY.md</c> §3.2.
        /// </summary>
        public bool AutoDiscoverControllers { get; set; } = true;

        /// <summary>
        /// Maximum number of RPC calls per client per second (0 = unlimited).
        /// </summary>
        public int RateLimitPermitLimit { get; set; } = 0;

        /// <summary>
        /// Time window for rate limiting in seconds.
        /// </summary>
        public int RateLimitWindowSeconds { get; set; } = 10;

        /// <summary>
        /// When <c>true</c>, the framework requires an authenticated user for every
        /// RPC call (north-bound default-deny). A method without
        /// <c>[SleipnirAuthorise]</c> is then only reachable when the user is
        /// authenticated; a method with <c>[SleipnirAnonymous]</c> remains explicitly
        /// open (opt-out, e.g. Health/Ping). <c>[SleipnirAuthorise]</c> still checks
        /// role/authentication as before. Default <c>false</c> (south-bound
        /// default-allow — non-breaking). Passed through to the invoker via
        /// <c>AddSleipnir</c> and additionally enforced as a transport gate at the
        /// WebSocket upgrade and the discovery endpoint.
        /// See <c>SECURITY.md</c>.
        /// </summary>
        public bool RequireAuthentication { get; set; } = false;

        /// <summary>
        /// Maximum number of requests in a batch (default 0 = unlimited,
        /// non-breaking). Protects the server against fan-out DoS: without a cap, a
        /// single 1-MB body can contain thousands of requests that fire
        /// simultaneously via Task.WhenAll. Enforced at the batch entry of the
        /// invoker (backstop) and at the multi-endpoints (REST /json/multi,
        /// WebSocket, JSON-RPC batch) as an early 400 gate. North-bound
        /// recommended &gt; 0.
        /// See <c>SECURITY.md</c>.
        /// </summary>
        public int MaximumBatchSize { get; set; } = 0;

        /// <summary>
        /// Maximum length of a client-controlled JsonPath in a
        /// <c>dependencyMapping</c> (default 256, 0 = unlimited). The client
        /// chooses the path and (via the provider choice) the JSON it is evaluated
        /// against — a long path can drive the JsonPath.Net evaluation (in
        /// particular <c>$..</c>) into a CPU stall. The cap is checked before
        /// parsing; an over-long path is dropped (the alias stays unset → the
        /// dependent receives the propagation 400). See
        /// <c>SECURITY.md</c>.
        /// </summary>
        public int MaxDependencyPathLength { get; set; } = 256;

        /// <summary>
        /// When <c>false</c>, recursive-descent paths (<c>$..foo</c>) in
        /// client-controlled <c>dependencyMapping</c> paths are rejected (before
        /// the expensive evaluation). Default <c>true</c> (non-breaking —
        /// <c>$..</c> is a legitimate JsonPath tool). North-bound hardening can
        /// set it to <c>false</c> to exclude the most expensive path type.
        /// See <c>SECURITY.md</c>.
        /// </summary>
        public bool AllowRecursiveDescent { get; set; } = true;

        /// <summary>
        /// Maximum element count of an array/collection parameter (default 1000, 0 = unlimited).
        /// Protects against cardinality blow-up in the @alias whole-collection passthrough, where
        /// the server constructs the array from an earlier result at runtime — body-size limits
        /// do not apply there because the cardinality is produced server-side. Enforced in the
        /// invoker before the method call (top-level parameter; string/byte[] excluded).
        /// </summary>
        public int MaxParameterArrayLength { get; set; } = 1000;

        /// <summary>
        /// Maximum element count of an array/collection return value (default 10000, 0 = unlimited).
        /// Prevents a single result from driving the server into memory exhaustion. Applies to
        /// materialized collections (List/Array/Dictionary) and IAsyncEnumerable streams
        /// (early-stop on consumption). Top-level result; string/byte[] excluded.
        /// </summary>
        public int MaxResultElementCount { get; set; } = 10000;

        /// <summary>
        /// How an extracted @alias fragment is bound to the consumer parameter type
        /// (default <see cref="SleipnirCommon.Models.AliasBindingMode.Weak"/>). Weak = STJ
        /// duck-typing with silent defaults (powerful; subset fan-out works).
        /// Strict = the fragment must fully cover the consumer type (every public
        /// read-write property must be present in the fragment), otherwise 400 — it
        /// only toggles the object→object silent-default case; cross-kind is 400 in
        /// both modes. Applies only to @alias-sourced parameters. See DEPENDENCY_BINDING.md.
        /// </summary>
        public AliasBindingMode AliasBindingMode { get; set; } = AliasBindingMode.Weak;

        /// <summary>
        /// Enables the JSON-RPC 2.0 compatibility endpoint (POST /api/sleipnir/jsonrpc).
        /// Default <c>false</c> — opt-in. Maps JSON-RPC requests onto the Sleipnir
        /// invoker (parallel mode, routing <c>Controller.Method</c>, named and
        /// positional params, batch, notifications). Chaining, execution-mode
        /// selection, and binary out-of-band remain reserved for the native Sleipnir
        /// wire. See <c>JSONRPC_COMPAT.md</c>.
        /// </summary>
        public bool EnableJsonRpcCompat { get; set; } = false;

        /// <summary>
        /// Enables the JSON observability snapshot endpoint
        /// <c>GET {prefix}/observability</c> (default <c>false</c> — opt-in, like
        /// <see cref="EnableJsonRpcCompat"/>). Returns a small JSON document with live
        /// transport/runtime state (active WebSocket connections, active event
        /// subscriptions, cumulative call/error/batch counters, dropped events) for the
        /// Developer UI Observability panel. RequireAuth-gated like <c>/discovery</c>:
        /// when <see cref="RequireAuthentication"/> is on, an unauthenticated caller
        /// receives <c>401</c>. For a Prometheus-text scrape endpoint instead, use
        /// <c>Sleipnir.Telemetry</c>'s <c>AddSleipnirPrometheusMetrics</c> +
        /// <c>UseSleipnirPrometheusScrapingEndpoint</c> (separate opt-in). Experimental —
        /// see <c>STABILITY.md</c>.
        /// </summary>
        public bool EnableObservability { get; set; } = false;

        // ───────────────────────────────────────────────────────────────────────
        // Interceptor pipeline (Phase 1, see docs/design/phase-1-interceptor-pipeline.md)
        // ───────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Interceptors that run around *every single* RPC invocation (single-call and
        /// per element in a batch). Default empty — the built-ins (RateLimit/Auth/
        /// Telemetry/Logging) are registered by <c>AddSleipnir</c> when
        /// <see cref="RegisterBuiltInInterceptors"/> is <c>true</c> (default). User
        /// interceptors are appended here *after* the built-ins, so they run
        /// *inside* (closer to the method invocation). See also
        /// <see cref="BatchInterceptors"/> for batch-level interceptors.
        ///
        /// Order: built-ins first (outer: RateLimit → Auth → Validation →
        /// Telemetry → Method), then user interceptors (inner). Anyone needing an
        /// interceptor *before* RateLimit/Auth must set
        /// <see cref="RegisterBuiltInInterceptors"/> to <c>false</c> and assemble
        /// the built-ins themselves.
        /// </summary>
        public List<SleipnirCore.Services.ISleipnirInterceptor> Interceptors { get; } = new();

        /// <summary>
        /// Interceptors that run around *a whole batch* (not per element) — for
        /// batch metrics (<c>sleipnir.batch.*</c>), batch logging, batch rate
        /// limiting. Default empty; built-ins analogous to <see cref="Interceptors"/>.
        /// </summary>
        public List<SleipnirCore.Services.ISleipnirBatchInterceptor> BatchInterceptors { get; } = new();

        /// <summary>
        /// When <c>true</c> (default), <c>AddSleipnir</c> registers the built-in
        /// interceptors (Logging in v1.0; Auth/Telemetry arrive with Phase 1) in the
        /// fixed order RateLimit → Auth → Validation → Telemetry. Set to
        /// <c>false</c> to assemble the built-ins yourself (e.g. to place your own
        /// RateLimit interceptor at the very front, or to replace Auth with your own
        /// implementation). User interceptors from <see cref="Interceptors"/>/
        /// <see cref="BatchInterceptors"/> are in any case appended *after* the
        /// built-ins (inner).
        /// </summary>
        public bool RegisterBuiltInInterceptors { get; set; } = true;
    }
}
