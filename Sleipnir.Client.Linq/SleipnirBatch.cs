using SleipnirCommon.Models;

namespace Sleipnir.Client.Linq;

/// <summary>
/// Collects several type-safe <see cref="SleipnirCallSpec"/> objects into a batch. Order is preserved;
/// <see cref="Dep{T}"/> wiring already lives in the specs (alias + dependencyMapping), so it is enough to
/// send them together as a multi-request. The server auto-selects the topological batch path from the
/// dependency mappings.
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
        if (spec is null) throw new ArgumentNullException(nameof(spec));
        _specs.Add(spec);
        return this;
    }

    public SleipnirMultiRequest ToMultiRequest() => new()
    {
        Mode = Mode,
        Requests = _specs.Select(s => s.ToRequest()).ToList()
    };
}