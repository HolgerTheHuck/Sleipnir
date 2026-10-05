using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Primitives;
using SleipnirCore.Services;
using SleipnirHub.Extensions;

namespace SleipnirHub.Auth;

/// <summary>
/// Opt-in <c>?access_token=</c> fallback (<see cref="SleipnirOptions.AcceptAccessTokenQuery"/>).
/// A browser cannot set an <c>Authorization</c> header on a WebSocket upgrade or on a native
/// <c>EventSource</c>, so the Sleipnir browser clients put a bearer into the query string instead.
/// This middleware promotes that query value to an <c>Authorization: Bearer …</c> request header
/// — <b>only</b> on the Sleipnir WebSocket upgrade and SSE event requests — so whatever bearer
/// authentication the host configured (JwtBearer, a custom handler, …) validates it unchanged in
/// <c>UseAuthentication</c>. It is the scheme-agnostic equivalent of the
/// <c>JwtBearerEvents.OnMessageReceived</c> pattern, scoped to the two transports.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Narrow (decision 6.3):</b> only a <c>GET</c> WebSocket upgrade (or an HTTP/2 extended
/// CONNECT for <c>websocket</c>) on a registered WebSocket path, and a <c>GET</c> under a
/// registered SSE prefix (<see cref="SleipnirAccessTokenQueryPaths"/>). Every other request —
/// including REST calls that happen to carry <c>?access_token=</c> — is left untouched and the
/// query value is never read.</item>
/// <item><b>Header wins:</b> an existing <c>Authorization</c> header is never overwritten.</item>
/// <item><b>Log hygiene:</b> on eligible requests the <c>access_token</c> parameter is removed from
/// <see cref="HttpRequest.QueryString"/> before anything downstream runs, so SSE parameter
/// binding, request logging, the hosting "Request finished" log and exception pages never see
/// it. (The hosting "Request starting" log is written before any middleware runs; keep
/// <c>Microsoft.AspNetCore.Hosting.Diagnostics</c> below <c>Information</c> in production —
/// the ASP.NET default — or the token can appear there.)</item>
/// </list>
/// Installed at the very start of the pipeline by <see cref="SleipnirAccessTokenQueryStartupFilter"/>
/// so it always runs before the host's <c>UseAuthentication</c>, independent of the order the host
/// writes its pipeline in.
/// </remarks>
internal sealed class SleipnirAccessTokenQueryMiddleware
{
    /// <summary>The query parameter name (the SignalR/ASP.NET convention).</summary>
    public const string QueryParameterName = "access_token";

    private readonly RequestDelegate _next;
    private readonly SleipnirAccessTokenQueryPaths _paths;

    public SleipnirAccessTokenQueryMiddleware(RequestDelegate next, SleipnirAccessTokenQueryPaths paths)
    {
        _next = next;
        _paths = paths;
    }

    public Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        if (request.QueryString.HasValue && IsEligible(context)
            && request.Query.TryGetValue(QueryParameterName, out var values))
        {
            // Strip first, so nothing downstream sees the token — even when it is not used.
            request.QueryString = RemoveParameter(request.QueryString, QueryParameterName);

            // Exactly one non-empty value; an ambiguous duplicate is ignored (not authenticated).
            var token = values.Count == 1 ? values[0] : null;
            if (!string.IsNullOrWhiteSpace(token) && StringValues.IsNullOrEmpty(request.Headers.Authorization))
                request.Headers.Authorization = "Bearer " + token;
        }
        return _next(context);
    }

    /// <summary>WebSocket upgrade on a WS path, or a GET under an SSE prefix — nothing else.</summary>
    private bool IsEligible(HttpContext context)
    {
        var request = context.Request;
        if (_paths.IsWebSocketPath(request.Path) && IsWebSocketUpgrade(context))
            return true;
        return HttpMethods.IsGet(request.Method) && _paths.IsSsePath(request.Path);
    }

    private static bool IsWebSocketUpgrade(HttpContext context)
    {
        var request = context.Request;
        if (HttpMethods.IsGet(request.Method)
            && string.Equals(request.Headers.Upgrade.ToString(), "websocket", StringComparison.OrdinalIgnoreCase))
            return true;
        // HTTP/2 WebSockets (RFC 8441): extended CONNECT with :protocol = websocket.
        var connect = context.Features.Get<IHttpExtendedConnectFeature>();
        return connect is { IsExtendedConnect: true }
            && string.Equals(connect.Protocol, "websocket", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Removes every occurrence of <paramref name="name"/> from the query string, preserving the
    /// other parameters byte-for-byte (no re-encoding).
    /// </summary>
    internal static QueryString RemoveParameter(QueryString query, string name)
    {
        var raw = query.Value;
        if (string.IsNullOrEmpty(raw)) return query;
        var sb = new StringBuilder(raw.Length);
        foreach (var pair in raw.TrimStart('?').Split('&'))
        {
            if (pair.Length == 0) continue;
            var eq = pair.IndexOf('=');
            var key = Uri.UnescapeDataString((eq < 0 ? pair : pair[..eq]).Replace('+', ' '));
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) continue;
            sb.Append(sb.Length == 0 ? '?' : '&').Append(pair);
        }
        return sb.Length == 0 ? QueryString.Empty : new QueryString(sb.ToString());
    }
}

/// <summary>
/// Puts <see cref="SleipnirAccessTokenQueryMiddleware"/> at the very front of the pipeline when
/// <see cref="SleipnirOptions.AcceptAccessTokenQuery"/> is on (registered unconditionally by
/// <c>AddSleipnir</c>; a no-op when the option is off).
/// </summary>
internal sealed class SleipnirAccessTokenQueryStartupFilter : IStartupFilter
{
    private readonly SleipnirOptions _options;

    public SleipnirAccessTokenQueryStartupFilter(SleipnirOptions options) => _options = options;

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        if (_options.AcceptAccessTokenQuery)
            app.UseMiddleware<SleipnirAccessTokenQueryMiddleware>();
        next(app);
    };
}
