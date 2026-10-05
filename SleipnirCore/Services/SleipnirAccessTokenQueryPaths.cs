using Microsoft.AspNetCore.Http;

namespace SleipnirCore.Services;

/// <summary>
/// The request paths on which the opt-in <c>?access_token=</c> query fallback
/// (<c>SleipnirOptions.AcceptAccessTokenQuery</c>) may be honored: the WebSocket upgrade path(s)
/// and the SSE event prefix(es). Populated by the transports themselves when they are mapped
/// (<c>UseSleipnirWebSocket(path)</c> registers its path, <c>MapSleipnirEndpoints(prefix)</c>
/// registers <c>{prefix}/events</c> when SSE is on), so custom paths are picked up without
/// duplicating them in the options. Registered as a DI singleton by <c>AddSleipnir</c>.
/// </summary>
/// <remarks>
/// Writes happen during pipeline configuration (startup); reads happen per request. The arrays
/// are replaced copy-on-write under a lock, so a read never observes a partially built list.
/// </remarks>
internal sealed class SleipnirAccessTokenQueryPaths
{
    private readonly object _gate = new();
    private PathString[] _webSocketPaths = Array.Empty<PathString>();
    private PathString[] _ssePrefixes = Array.Empty<PathString>();

    /// <summary>Registers a WebSocket upgrade path (e.g. <c>/sleipnirws</c>).</summary>
    public void AddWebSocketPath(PathString path) => Add(ref _webSocketPaths, path);

    /// <summary>Registers an SSE event prefix (e.g. <c>/api/sleipnir/events</c>).</summary>
    public void AddSsePrefix(PathString prefix) => Add(ref _ssePrefixes, prefix);

    /// <summary>True when <paramref name="path"/> is (under) a registered WebSocket path.</summary>
    public bool IsWebSocketPath(PathString path) => Matches(Volatile.Read(ref _webSocketPaths), path);

    /// <summary>True when <paramref name="path"/> is under a registered SSE event prefix.</summary>
    public bool IsSsePath(PathString path) => Matches(Volatile.Read(ref _ssePrefixes), path);

    private void Add(ref PathString[] target, PathString path)
    {
        if (!path.HasValue) return;
        lock (_gate)
        {
            foreach (var existing in target)
                if (existing.Equals(path, StringComparison.OrdinalIgnoreCase)) return;
            var next = new PathString[target.Length + 1];
            target.CopyTo(next, 0);
            next[^1] = path;
            Volatile.Write(ref target, next);
        }
    }

    private static bool Matches(PathString[] candidates, PathString path)
    {
        foreach (var candidate in candidates)
            if (path.StartsWithSegments(candidate, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
