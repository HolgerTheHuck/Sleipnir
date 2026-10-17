namespace SleipnirClient.Sleipnir;

/// <summary>
/// Contract identity on the wire (CONTRACT_EVOLUTION.md §7). A generated client passes its
/// embedded contract hash (<see cref="SleipnirRouterOptions.ContractHash"/>); the router sends it on
/// every request so the server can count which contracts its callers were built against: header
/// <see cref="HeaderName"/> on REST/SSE, query <see cref="QueryName"/> on the WebSocket/SignalR
/// connect (mirrors the TS client, where a browser cannot set upgrade headers).
/// </summary>
public static class SleipnirContract
{
    public const string HeaderName = "Sleipnir-Contract";
    public const string QueryName = "sleipnir-contract";

    /// <summary><paramref name="pathOrUrl"/> with the contract query parameter appended
    /// (unchanged without a hash).</summary>
    public static string WithQuery(string pathOrUrl, string? contractHash)
        => string.IsNullOrEmpty(contractHash)
            ? pathOrUrl
            : pathOrUrl + (pathOrUrl.Contains('?') ? "&" : "?") + QueryName + "=" + Uri.EscapeDataString(contractHash);
}
