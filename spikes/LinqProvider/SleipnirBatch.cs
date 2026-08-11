using SleipnirCommon.Models;

namespace Sleipnir.Spike.LinqProvider;

/// <summary>
/// Sammelt mehrere typsichere <see cref="SleipnirCallSpec"/>-Objekte zu einem Batch.
/// Die Reihenfolge bleibt erhalten; <see cref="Dep{T}"/>-Verdrahtungen stecken
/// bereits in den Specs (Alias + dependencyMapping), daher reicht es, sie
/// gesammelt als Multi-Request zu senden. Der Server wählt den topologischen
/// Batch-Pfad automatisch (anhand der dependencyMappings).
/// </summary>
public sealed class SleipnirBatch
{
    private readonly List<SleipnirCallSpec> _specs = new();
    public IReadOnlyList<SleipnirCallSpec> Specs => _specs;

    public ExecutionMode Mode { get; set; } = ExecutionMode.Serial;

    public SleipnirBatch() { }

    public SleipnirBatch(params SleipnirCallSpec[] specs) => _specs.AddRange(specs);

    public SleipnirBatch Add(SleipnirCallSpec spec)
    {
        if (spec == null) throw new ArgumentNullException(nameof(spec));
        _specs.Add(spec);
        return this;
    }

    public SleipnirMultiRequest ToMultiRequest() => new()
    {
        Mode = Mode,
        Requests = _specs.Select(s => s.ToRequest()).ToList()
    };
}