using TrameCommon.Models;

namespace Trame.Spike.LinqProvider;

/// <summary>
/// Sammelt mehrere typsichere <see cref="TrameCallSpec"/>-Objekte zu einem Batch.
/// Die Reihenfolge bleibt erhalten; <see cref="Dep{T}"/>-Verdrahtungen stecken
/// bereits in den Specs (Alias + dependencyMapping), daher reicht es, sie
/// gesammelt als Multi-Request zu senden. Der Server wählt den topologischen
/// Batch-Pfad automatisch (anhand der dependencyMappings).
/// </summary>
public sealed class TrameBatch
{
    private readonly List<TrameCallSpec> _specs = new();
    public IReadOnlyList<TrameCallSpec> Specs => _specs;

    public ExecutionMode Mode { get; set; } = ExecutionMode.Serial;

    public TrameBatch() { }

    public TrameBatch(params TrameCallSpec[] specs) => _specs.AddRange(specs);

    public TrameBatch Add(TrameCallSpec spec)
    {
        if (spec == null) throw new ArgumentNullException(nameof(spec));
        _specs.Add(spec);
        return this;
    }

    public TrameMultiRequest ToMultiRequest() => new()
    {
        Mode = Mode,
        Requests = _specs.Select(s => s.ToRequest()).ToList()
    };
}