using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace SleipnirCore.Tracing;

/// <summary>
/// Which contracts the callers in the field were built against. Generated clients send their
/// embedded <c>contractHash</c> (header <see cref="HeaderName"/>; on WebSocket/SignalR, where a
/// browser cannot set upgrade headers, the query parameter <see cref="QueryName"/>). The invoker
/// records every resolved call per contract and every call to a <c>[SleipnirDeprecated]</c>
/// method per route and contract; <c>/observability</c> lists both, the metrics
/// <c>sleipnir.client.contract.calls</c> / <c>sleipnir.deprecated.calls</c> count them. That
/// answers "are clients of the old contract still out there?" and "can I remove this method?"
/// with data. See CONTRACT_EVOLUTION.md §7.
/// </summary>
/// <remarks>
/// The value is client-controlled: it is validated (<c>sha256:</c> + 64 hex), and at most
/// <see cref="MaxTrackedContracts"/> distinct contracts are tracked — further ones count as
/// <see cref="Other"/>, malformed ones as <see cref="Invalid"/>, absent ones (hand-written
/// clients) as <see cref="Unknown"/>. Registered as a DI singleton by <c>AddSleipnir</c>.
/// </remarks>
public sealed class SleipnirClientContractRegistry
{
    public const string HeaderName = "Sleipnir-Contract";
    public const string QueryName = "sleipnir-contract";
    public const string Unknown = "unknown";
    public const string Invalid = "invalid";
    public const string Other = "other";
    public const int MaxTrackedContracts = 64;

    private static readonly Regex HashFormat = new("^sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly ConcurrentDictionary<string, Usage> _contracts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string Route, string Contract), Usage> _deprecated = new();
    private readonly ILogger? _logger;

    public SleipnirClientContractRegistry(ILogger<SleipnirClientContractRegistry>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>The caller's contract from the request (header first, then query) as a tracking
    /// key: the hash, or <see cref="Unknown"/> / <see cref="Invalid"/>. Reads the HttpContext —
    /// call it where the context may be touched (never inside a parallel fan-out).</summary>
    public static string Resolve(HttpContext? context)
    {
        if (context == null)
            return Unknown;
        string? value = context.Request.Headers[HeaderName];
        if (string.IsNullOrEmpty(value))
            value = context.Request.Query[QueryName];
        if (string.IsNullOrEmpty(value))
            return Unknown;
        return HashFormat.IsMatch(value) ? value : Invalid;
    }

    /// <summary>Record one resolved call from a client built against <paramref name="contract"/>.</summary>
    public void RecordCall(string contract, string? currentContract)
    {
        var key = Track(contract, currentContract);
        Touch(_contracts.GetOrAdd(key, _ => new Usage()));
        SleipnirMetrics.RecordClientContractCall(Short(key), key == currentContract);
    }

    /// <summary>Record one call to a deprecated method.</summary>
    public void RecordDeprecatedCall(string route, string contract, string? currentContract)
    {
        var key = _contracts.ContainsKey(contract) ? contract : Track(contract, currentContract);
        Touch(_deprecated.GetOrAdd((route, key), _ => new Usage()));
        SleipnirMetrics.RecordDeprecatedCall(route, Short(key));
    }

    /// <summary>Snapshot for <c>/observability</c>, most recently seen first.</summary>
    public ContractSnapshot GetSnapshot(string? currentContract) => new(
        currentContract,
        _contracts
            .Select(kv => new ClientContractUsage(kv.Key, kv.Key == currentContract, kv.Value.FirstSeen, kv.Value.LastSeen, Interlocked.Read(ref kv.Value.Calls)))
            .OrderByDescending(u => u.LastSeen)
            .ToList(),
        _deprecated
            .Select(kv => new DeprecatedCallUsage(kv.Key.Route, kv.Key.Contract, kv.Value.FirstSeen, kv.Value.LastSeen, Interlocked.Read(ref kv.Value.Calls)))
            .OrderBy(u => u.Route, StringComparer.Ordinal)
            .ThenByDescending(u => u.LastSeen)
            .ToList());

    // The key a contract is tracked under; logs the first call of a contract that is not current.
    private string Track(string contract, string? currentContract)
    {
        if (contract is Unknown or Invalid || _contracts.ContainsKey(contract))
            return contract;
        if (_contracts.Count(kv => kv.Key is not (Unknown or Invalid or Other)) >= MaxTrackedContracts)
            return Other;
        if (_contracts.TryAdd(contract, new Usage()) && contract != currentContract)
        {
            _logger?.LogInformation(
                "Sleipnir: first call from a client built against contract {ClientContract} (the server serves {CurrentContract}).",
                contract, currentContract ?? "(none)");
        }
        return contract;
    }

    private static void Touch(Usage usage)
    {
        usage.LastSeen = DateTimeOffset.UtcNow;
        Interlocked.Increment(ref usage.Calls);
    }

    // Metric tag: the hash prefix keeps the series readable; uniqueness is not needed there.
    private static string Short(string key) => key.StartsWith("sha256:", StringComparison.Ordinal) ? key.Substring(0, 19) : key;

    private sealed class Usage
    {
        public readonly DateTimeOffset FirstSeen = DateTimeOffset.UtcNow;
        public DateTimeOffset LastSeen = DateTimeOffset.UtcNow;
        public long Calls;
    }
}

public sealed record ContractSnapshot(
    string? Current,
    IReadOnlyList<ClientContractUsage> Clients,
    IReadOnlyList<DeprecatedCallUsage> DeprecatedCalls);

public sealed record ClientContractUsage(string Contract, bool Current, DateTimeOffset FirstSeen, DateTimeOffset LastSeen, long Calls);

public sealed record DeprecatedCallUsage(string Route, string Contract, DateTimeOffset FirstSeen, DateTimeOffset LastSeen, long Calls);
