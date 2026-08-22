using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using SleipnirCommon.Models;
using SleipnirCore.Services;
using SleipnirCore.Services.Helper;
using SleipnirTests.Fixtures;
using Xunit;

namespace SleipnirTests.Unit.Core;

/// <summary>
/// Single alias grammar (audit 2026-08-22, D2 / finding F2).
///
/// Before D2 there were three detection sites with divergent semantics:
/// the invoker's <c>ContainsAlias</c> trimmed leading whitespace, the placeholder
/// substitution did not, and the graph builder used a third variant. A literal like
/// <c>" @x"</c> was detected as an alias but never substituted nor booked unresolved.
/// Literals starting with <c>@</c> were unusable entirely.
///
/// Contract now (<see cref="AliasGrammar"/>):
/// - trim-free: only a string starting with exactly one <c>@</c> + alias chars is an alias;
/// - <c>@@x</c> is the escape for the literal string <c>@x</c>;
/// - a lone <c>"@"</c>, <c>" @x"</c>, and <c>"@."</c> are literals everywhere;
/// - all three sites (detection, substitution, graph edges) share one implementation.
/// </summary>
public class AliasGrammarTests
{
    // === Grammatik-Unit-Tests ===================================================

    [Theory]
    [InlineData("@orderId", "orderId")]
    [InlineData("@a", "a")]
    [InlineData("@a_1", "a_1")]
    [InlineData("@a.b", "a")]      // delimiter ends the name
    [InlineData("@a-b", "a")]
    public void Classify_AliasReference_ParsesName(string value, string expected)
    {
        AliasGrammar.Classify(value, out var alias).Should().Be(AliasKind.AliasReference);
        alias.Should().Be(expected);
    }

    [Theory]
    [InlineData("orderId")]       // plain literal
    [InlineData("@")]             // lone @
    [InlineData(" @x")]           // leading whitespace → literal (trim-free!)
    [InlineData("@.")]            // no alias char after @
    [InlineData("@-")]            // ditto
    [InlineData("x@y")]           // @ not at start
    public void Classify_Literals_AreNotAliases(string value)
    {
        AliasGrammar.Classify(value, out _).Should().Be(AliasKind.Literal);
        AliasGrammar.IsAlias(value).Should().BeFalse();
    }

    [Fact]
    public void Classify_EscapedLiteral_DoubleAt()
    {
        AliasGrammar.Classify("@@order", out var text).Should().Be(AliasKind.EscapedLiteral);
        text.Should().Be("@order");
        AliasGrammar.IsEscapedLiteral("@@order").Should().BeTrue();
        AliasGrammar.Unescape("@@order").Should().Be("@order");
        // Unescape ist idempotent für Nicht-Escapes:
        AliasGrammar.Unescape("@order").Should().Be("@order");
        AliasGrammar.Unescape("plain").Should().Be("plain");
    }

    [Fact]
    public void Classify_NullAndEmpty_AreLiterals()
    {
        AliasGrammar.Classify(null, out _).Should().Be(AliasKind.Literal);
        AliasGrammar.Classify("", out _).Should().Be(AliasKind.Literal);
    }

    [Fact]
    public void ContainsAlias_NodeTree_FindsAlias_IgnoresEscapedLiteral()
    {
        var withAlias = JsonNode.Parse("""[{"parameterName":"v","data":"@id"}]""");
        AliasGrammar.ContainsAlias(withAlias).Should().BeTrue();

        var withEscape = JsonNode.Parse("""[{"parameterName":"v","data":"@@id"}]""");
        AliasGrammar.ContainsAlias(withEscape).Should().BeFalse();

        var withWhitespace = JsonNode.Parse("""[{"parameterName":"v","data":" @id"}]""");
        AliasGrammar.ContainsAlias(withWhitespace).Should().BeFalse();
    }

    // === End-to-end: Literal "@user" wird nicht mehr als Alias blockiert ========

    private readonly SleipnirInvoker _invoker;

    public AliasGrammarTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTransient<DependencyChainController>();
        var sp = services.BuildServiceProvider();
        _invoker = new SleipnirInvoker(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ILogger<SleipnirInvoker>>());
        _invoker.Register<DependencyChainController>();
    }

    private static SleipnirRequest Req(
        string id, string controller, string method,
        Dictionary<string, string>? mapping = null,
        params (string name, string jsonValue)[] parameters)
    {
        var paramList = parameters
            .Select(p => new SleipnirParameter
            {
                ParameterName = p.name,
                // @-Präfix → roher String-Wert (Alias-Platzhalter oder @@-Escape);
                // sonst JSON-kodierter Wert (z. B. "\" @x\"" für den String " @x").
                Data = p.jsonValue.StartsWith("@") ? JsonValue.Create(p.jsonValue) : JsonNode.Parse(p.jsonValue)
            })
            .ToList();
        return new SleipnirRequest
        {
            Id = id,
            Controller = controller,
            Method = method,
            Params = JsonSerializer.SerializeToNode(paramList),
            DependencyMapping = mapping,
        };
    }

    /// <summary>Consumer-Parameter mit rohem String-Wert (Alias ODER Literal).</summary>
    private static (string name, string jsonValue) Raw(string paramName, string value) =>
        (paramName, value);

    [Fact]
    public async Task LiteralAtString_NoProvider_DoesNotBlockAsUnavailable()
    {
        // Vor D2: "@handle" wurde als Alias interpretiert → 400 "no provider exposes".
        // Jetzt: Literal → der Controller bekommt den String "@handle" und antwortet 2xx.
        var request = Req("r", "DepChain", "EchoString", null, Raw("value", "@handle"));

        var responses = (await _invoker.InvokeDi(
            new List<SleipnirRequest> { request }, null, ExecutionMode.Parallel)).ToList();

        responses.Should().HaveCount(1);
        responses[0]!.Code.Should().Be((int)HttpStatusCode.OK);
        responses[0]!.Data.Value.Deserialize<string>().Should().Be("@handle");
    }

    [Fact]
    public async Task LiteralWithLeadingWhitespace_IsNeverDetectedNorSubstituted()
    {
        // Der alte Trim-Mismatch: ContainsAlias erkannte " @x", der Replacer substituierte
        // nicht und buchte ihn auch nicht unresolved — stummer Fehlzustand. Jetzt ist
        // " @x" überall Literal; ohne Provider im Batch läuft es einfach durch.
        var request = Req("r", "DepChain", "EchoString", null, Raw("value", "\" @x\""));

        var responses = (await _invoker.InvokeDi(
            new List<SleipnirRequest> { request }, null, ExecutionMode.Parallel)).ToList();

        responses[0]!.Code.Should().Be((int)HttpStatusCode.OK);
        responses[0]!.Data.Value.Deserialize<string>().Should().Be(" @x");
    }

    [Fact]
    public async Task EscapedLiteral_ReachesControllerUnescaped()
    {
        // "@@mention" ist das Escape für das Literal "@mention" — der Controller sieht
        // das einfache @, nie das doppelte.
        var request = Req("r", "DepChain", "EchoString", null, Raw("value", "@@mention"));

        var responses = (await _invoker.InvokeDi(
            new List<SleipnirRequest> { request }, null, ExecutionMode.Parallel)).ToList();

        responses[0]!.Code.Should().Be((int)HttpStatusCode.OK);
        responses[0]!.Data.Value.Deserialize<string>().Should().Be("@mention");
    }

    [Fact]
    public async Task EscapedLiteral_InTopologicalBatch_AlsoUnescaped()
    {
        // Auch im topologischen Pfad (DependencyMapping vorhanden → Auto-Detect) wird
        // @@ unescaped — der Escape gilt pfadübergreifend.
        var provider = Req("p", "DepChain", "MakeDto",
            mapping: new() { ["cust"] = "$.id" }, ("id", "5"), ("name", "\"e\""));
        var consumer = Req("c", "DepChain", "EchoString", null, Raw("value", "@@not-an-alias"));

        var responses = (await _invoker.InvokeDi(
            new List<SleipnirRequest> { provider, consumer }, null, ExecutionMode.Parallel)).ToList();

        responses.Should().OnlyContain(r => r!.Code == (int)HttpStatusCode.OK);
        responses.Single(r => r!.Id == "c")!.Data.Value.Deserialize<string>().Should().Be("@not-an-alias");
    }

    [Fact]
    public async Task RealAlias_StillResolves_AfterGrammarChange()
    {
        // Regression-Guard: die Grammatik-Umstellung darf echte Aliase nicht brechen.
        var provider = Req("p", "DepChain", "MakeDto",
            mapping: new() { ["cid"] = "$.id" }, ("id", "7"), ("name", "\"alice\""));
        var consumer = Req("c", "DepChain", "EchoInt", null, Raw("value", "@cid"));

        var responses = (await _invoker.InvokeDi(
            new List<SleipnirRequest> { provider, consumer }, null, ExecutionMode.Parallel)).ToList();

        responses.Should().OnlyContain(r => r!.Code == (int)HttpStatusCode.OK);
        responses.Single(r => r!.Id == "c")!.Data.Value.Deserialize<int>().Should().Be(7);
    }

    [Fact]
    public async Task UnresolvedRealAlias_StillGets400_AfterGrammarChange()
    {
        // Regression-Guard: ein echter, unversorgter Alias bleibt eine saubere 400 —
        // jetzt aber NUR für echte Aliase, nicht mehr für @-Literale. Der Provider sorgt
        // dafür, dass der Batch ins topologische Pfad-Routing geht (Auto-Detect), wo die
        // Verfügbarkeits-Propagierung greift.
        var provider = Req("p", "DepChain", "MakeDto",
            mapping: new() { ["other"] = "$.id" }, ("id", "1"), ("name", "\"x\""));
        var consumer = Req("c", "DepChain", "EchoInt", null, Raw("value", "@missing"));

        var responses = (await _invoker.InvokeDi(
            new List<SleipnirRequest> { provider, consumer }, null, ExecutionMode.Parallel)).ToList();

        var consumerResponse = responses.Single(r => r!.Id == "c")!;
        consumerResponse.Code.Should().Be((int)HttpStatusCode.BadRequest);
        consumerResponse.Error!.Message.Should().Contain("no provider exposes '@missing'");
    }
}
