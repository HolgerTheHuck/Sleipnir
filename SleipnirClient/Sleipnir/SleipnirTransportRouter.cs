using System.Net.WebSockets;

namespace SleipnirClient.Sleipnir;

/// <summary>
/// Codegen <c>--transport</c> capability — which backends the generated <c>SleipnirClient</c>
/// bundles. The public client surface is identical across all capabilities; only the bundled
/// backends differ. Mirrors the TS <c>SleipnirBundleCapability</c>.
/// </summary>
public enum SleipnirBundleCapability
{
    /// <summary>REST calls + SSE events (HTTP-only, proxy-safe).</summary>
    Rest,
    /// <summary>WebSocket calls + WebSocket events.</summary>
    Ws,
    /// <summary>REST + WS + SSE (enables <c>auto</c>: WS → REST+SSE fallback). Default.</summary>
    All,
    /// <summary>REST + WS + SSE + SignalR (opt-in add-on; hub-streaming events).</summary>
    Signalr,
}

/// <summary>
/// User-facing transport selection — a *profile* mapping to a {call, event} backend pair, plus
/// <c>Auto</c> for negotiation. <c>Sse</c> is intentionally NOT a standalone profile (SSE cannot carry
/// calls); the HTTP-only profile is <c>Rest</c> (REST calls + SSE events). The raw SSE backend stays
/// reachable via the <see cref="SleipnirTransportRouter.Sse"/> escape hatch.
/// </summary>
public enum SleipnirTransport
{
    /// <summary>Probe WebSocket; success → ws profile, failure → rest profile. Default.</summary>
    Auto,
    /// <summary>REST calls + SSE events.</summary>
    Rest,
    /// <summary>WebSocket calls + WebSocket events.</summary>
    Ws,
    /// <summary>SignalR calls + SignalR hub-streaming events (opt-in).</summary>
    Signalr,
}

/// <summary>Options for <see cref="SleipnirTransportRouter"/>. Sub-objects are passed to each backend.</summary>
public sealed class SleipnirRouterOptions
{
    public required string BaseUrl { get; init; }
    public SleipnirBundleCapability Capability { get; init; } = SleipnirBundleCapability.All;
    public SleipnirTransport DefaultTransport { get; init; } = SleipnirTransport.Auto;
    /// <summary>Bearer applied to every bundled backend that accepts one (SSE / SignalR).</summary>
    public string? Bearer { get; init; }
    /// <summary>Call timeout applied to REST + WS.</summary>
    public TimeSpan? CallTimeout { get; init; }
    /// <summary>WS handshake probe timeout for <c>auto</c> negotiation. Default 1500ms.</summary>
    public TimeSpan? ProbeTimeout { get; init; }
    public string ApiPath { get; init; } = "api/sleipnir";
    public string WsPath { get; init; } = "sleipnirws";
    public string HubPath { get; init; } = "sleipnirhub";
    /// <summary>Reconnect backoff for WS + SSE (null → backend defaults).</summary>
    public TimeSpan[]? ReconnectDelays { get; init; }
    /// <summary>Client-wide event resume policy (WS + SSE).</summary>
    public ResumePolicy? ResumePolicy { get; init; }
    /// <summary>Optional injected HttpClient for the REST backend (shared-connection reuse).</summary>
    public HttpClient? RestHttpClient { get; init; }
}

/// <summary>
/// Unified transport router for the C# generated Sleipnir client. Holds the bundled backends, routes
/// <see cref="Call"/> to the active call backend and <see cref="SubscribeAsync{T}"/> to the active
/// event backend, and negotiates <c>auto</c> (try WebSocket, fall back to REST+SSE on failure). The
/// WS-vs-SSE subscribe signature mismatch is bridged once, here, so the generated client stays thin
/// and transport-identical. Mirrors the TS <c>SleipnirTransportRouter</c>.
/// </summary>
/// <remarks>
/// <b>Capability asymmetry</b> (no single native transport does both calls AND events except WS):
/// REST → calls only; SSE → events only; WS → calls + events; SignalR → calls + events. A
/// user-facing "transport" is therefore a PROFILE picking a {call, event} backend pair.
/// </remarks>
public class SleipnirTransportRouter : SleipnirClientBase, IAsyncDisposable
{
    public SleipnirBundleCapability Capability { get; }

    private readonly SleipnirRestJsonClient? _rest;
    private readonly SleipnirWebSocketClient? _ws;
    private readonly SleipnirSseClient? _sse;
    private readonly SleipnirSignalrClient? _signalr;
    private readonly TimeSpan _probeTimeout;

    private SleipnirTransport? _profile;
    private Task? _negotiateTask;
    private bool _disposed;
    private readonly SemaphoreSlim _negotiateLock = new(1, 1);

    public SleipnirTransportRouter(SleipnirRouterOptions opts) : base()
    {
        if (string.IsNullOrWhiteSpace(opts.BaseUrl))
            throw new ArgumentException("SleipnirTransportRouter: BaseUrl is required.", nameof(opts));

        Capability = opts.Capability;
        _probeTimeout = opts.ProbeTimeout ?? TimeSpan.FromMilliseconds(1500);
        var bearer = opts.Bearer;

        if (HasBackend(Capability, "rest"))
            _rest = new SleipnirRestJsonClient(opts.BaseUrl, opts.RestHttpClient, opts.ApiPath, opts.CallTimeout);
        if (HasBackend(Capability, "ws"))
            _ws = new SleipnirWebSocketClient(opts.BaseUrl, webSocket: null, opts.WsPath,
                opts.CallTimeout, logger: null, autoReconnect: true, reconnectDelays: opts.ReconnectDelays,
                socketFactory: null, onStateChanged: null, resumePolicy: opts.ResumePolicy);
        if (HasBackend(Capability, "sse"))
            _sse = new SleipnirSseClient(opts.BaseUrl, httpClient: null, opts.ApiPath, bearer,
                reconnect: true, reconnectDelays: opts.ReconnectDelays, onResume: opts.ResumePolicy);
        if (HasBackend(Capability, "signalr"))
            _signalr = new SleipnirSignalrClient(opts.BaseUrl, bearer, opts.HubPath);

        // A non-auto default resolves immediately; "auto" is probed lazily on first use.
        if (opts.DefaultTransport != SleipnirTransport.Auto)
            _profile = ResolveProfile(opts.DefaultTransport);
    }

    // --- escape hatches (null if not bundled) ---

    public SleipnirRestJsonClient? Rest => _rest;
    public SleipnirWebSocketClient? Ws => _ws;
    public SleipnirSseClient? Sse => _sse;
    public SleipnirSignalrClient? Signalr => _signalr;

    /// <summary>The resolved transport profile (null until first use when <c>Auto</c>).</summary>
    public string? ActiveTransport => _profile?.ToString().ToLowerInvariant();

    // --- profile resolution ---

    private static bool HasBackend(SleipnirBundleCapability cap, string backend) => backend switch
    {
        "rest" => cap is SleipnirBundleCapability.Rest or SleipnirBundleCapability.All or SleipnirBundleCapability.Signalr,
        "ws" => cap is SleipnirBundleCapability.Ws or SleipnirBundleCapability.All or SleipnirBundleCapability.Signalr,
        "sse" => cap is SleipnirBundleCapability.Rest or SleipnirBundleCapability.All or SleipnirBundleCapability.Signalr,
        "signalr" => cap == SleipnirBundleCapability.Signalr,
        _ => false,
    };

    private SleipnirTransport ResolveProfile(SleipnirTransport t)
    {
        switch (t)
        {
            case SleipnirTransport.Rest:
                if (_rest == null || _sse == null) throw NotBundled(t);
                return SleipnirTransport.Rest;
            case SleipnirTransport.Ws:
                if (_ws == null) throw NotBundled(t);
                return SleipnirTransport.Ws;
            case SleipnirTransport.Signalr:
                if (_signalr == null) throw NotBundled(t);
                return SleipnirTransport.Signalr;
            default:
                throw new ArgumentException($"Sleipnir transport '{t}' is not a valid profile.");
        }
    }

    private SleipnirException NotBundled(SleipnirTransport t)
        => new($"Sleipnir transport '{t}' is not available: the client was generated with --transport {Capability}, which does not bundle the required backend. Regenerate with a capability that includes it (e.g. --transport all).");

    /// <summary>
    /// Resolve the active profile. For <c>Auto</c> this runs the WS handshake probe once (lazy,
    /// concurrent-safe): success → ws profile; failure/timeout → rest profile (REST calls + SSE events).
    /// </summary>
    public async Task NegotiateAsync(CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SleipnirTransportRouter));
        if (_profile.HasValue) return;
        await _negotiateLock.WaitAsync(ct);
        try
        {
            if (_profile.HasValue) return;
            _negotiateTask ??= RunAutoNegotiationAsync(ct);
            await _negotiateTask;
        }
        finally
        {
            _negotiateLock.Release();
        }
    }

    private async Task RunAutoNegotiationAsync(CancellationToken ct)
    {
        // auto needs WS to probe; without WS bundled, fall back to the rest profile immediately.
        if (_ws == null)
        {
            if (_rest == null || _sse == null) throw NotBundled(SleipnirTransport.Auto);
            _profile = SleipnirTransport.Rest;
            return;
        }
        bool ok = false;
        try
        {
            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probeCts.CancelAfter(_probeTimeout);
            await _ws.ConnectAsync(probeCts.Token);
            ok = true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            ok = false; // probe timeout
        }
        catch
        {
            ok = false;
        }
        _profile = ok ? SleipnirTransport.Ws : SleipnirTransport.Rest;
        if (!ok && (_rest == null || _sse == null))
            throw NotBundled(SleipnirTransport.Auto);
    }

    /// <summary>Switch the active transport profile at runtime. <c>Auto</c> re-runs negotiation.</summary>
    public async Task UseTransportAsync(SleipnirTransport t, CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SleipnirTransportRouter));
        if (t == SleipnirTransport.Auto)
        {
            _profile = null;
            _negotiateTask = null;
            await NegotiateAsync(ct);
            return;
        }
        _profile = ResolveProfile(t);
    }

    private async Task EnsureProfileAsync(CancellationToken ct)
    {
        if (_profile.HasValue) return;
        await NegotiateAsync(ct);
    }

    private SleipnirTransport CallBackend()
    {
        if (_profile == SleipnirTransport.Ws) return SleipnirTransport.Ws;
        if (_profile == SleipnirTransport.Signalr) return SleipnirTransport.Signalr;
        return SleipnirTransport.Rest;
    }

    private SleipnirTransport EventBackend()
    {
        if (_profile == SleipnirTransport.Ws) return SleipnirTransport.Ws;
        if (_profile == SleipnirTransport.Signalr) return SleipnirTransport.Signalr;
        return SleipnirTransport.Rest; // rest profile → SSE events
    }

    // --- call routing ---

    public override async Task<SleipnirResponse?> Call(SleipnirRequest? request, CancellationToken ct = default)
    {
        if (request == null) return null;
        await EnsureProfileAsync(ct);
        var backend = CallBackend();
        if (backend == SleipnirTransport.Ws) return await _ws!.Call(request, ct);
        if (backend == SleipnirTransport.Signalr) return await _signalr!.Call(request, ct);
        return await _rest!.Call(request, ct);
    }

    public override async Task<IEnumerable<SleipnirResponse?>?> Call(SleipnirMultiRequest? request, CancellationToken ct = default)
    {
        if (request == null) return null;
        await EnsureProfileAsync(ct);
        var backend = CallBackend();
        if (backend == SleipnirTransport.Ws) return await _ws!.Call(request, ct);
        if (backend == SleipnirTransport.Signalr) return await _signalr!.Call(request, ct);
        return await _rest!.Call(request, ct);
    }

    // --- subscribe routing (the WS-vs-SSE mismatch bridged here) ---

    /// <summary>
    /// Subscribe over the active event backend. WS / SignalR receive the pre-built
    /// <paramref name="request"/> straight through; SSE unpacks it (controller/method/params) because
    /// SSE carries method args as URL query params (no body). Only named params are expressible over
    /// SSE; positional/binary params are a WS/SignalR-only capability.
    /// </summary>
    public override async Task<SleipnirSubscription<T>> SubscribeAsync<T>(
        SleipnirRequest? request, ResumePolicy? resumePolicy = null, CancellationToken ct = default)
    {
        if (request == null)
            throw new ArgumentNullException(nameof(request));
        await EnsureProfileAsync(ct);
        var backend = EventBackend();
        if (backend == SleipnirTransport.Ws) return await _ws!.SubscribeAsync<T>(request, resumePolicy, ct);
        if (backend == SleipnirTransport.Signalr) return await _signalr!.SubscribeAsync<T>(request, resumePolicy, ct);
        return await _sse!.SubscribeAsync<T>(request, resumePolicy, ct);
    }

    /// <summary>
    /// Resume a durable subscription over the active event backend — the cross-transport bridge used
    /// after a transport switch (e.g. <c>auto</c> WS→REST+SSE fallback). SSE / SignalR resume by id;
    /// resuming INTO WebSocket is not supported (the WS resume frame needs the original
    /// controller/method) — switch to <c>rest</c>/<c>auto</c> to resume over SSE.
    /// </summary>
    public override async Task<SleipnirSubscription<T>> ResumeAsync<T>(
        string subscriptionId, long lastEventId, ResumePolicy? resumePolicy = null, CancellationToken ct = default)
    {
        await EnsureProfileAsync(ct);
        var backend = EventBackend();
        if (backend == SleipnirTransport.Ws)
            throw new NotSupportedException(
                "Sleipnir cross-transport resume into WebSocket is not supported (the WS resume frame needs the original controller/method). " +
                "Switch to the rest/auto profile via UseTransportAsync(Rest) to resume over SSE.");
        if (backend == SleipnirTransport.Signalr) return await _signalr!.ResumeAsync<T>(subscriptionId, lastEventId, resumePolicy, ct);
        return await _sse!.ResumeAsync<T>(subscriptionId, lastEventId, resumePolicy, ct);
    }

    // --- shared concerns ---

    /// <summary>Fan a bearer swap out to every bundled backend that accepts one.</summary>
    public void SetBearer(string? bearer)
    {
        _sse?.SetBearer(bearer);
        _signalr?.SetBearer(bearer);
        // REST/WS do not yet support a runtime bearer swap in this client; regenerate is not required
        // for the bearer to apply to event backends (SSE / SignalR).
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ws != null) await _ws.DisposeAsync();
        if (_signalr != null) await _signalr.DisposeAsync();
        _sse?.Dispose();
        _rest?.Dispose();
        _negotiateLock.Dispose();
    }
}