using System.Text.Json.Nodes;
using FluentAssertions;
using Sleipnir.Codegen.Core;
using Xunit;

namespace SleipnirTests.Unit.Codegen;

/// <summary>
/// The C# engine against the shared executable spec — the same cases the TS port runs
/// (clients/codegen/test/unit/compat.test.ts), so both engines classify every change identically:
/// id, severity, site, position, direction and message.
/// </summary>
public class ContractCompatibilityTests
{
    private static readonly JsonArray Cases = LoadCases();

    private static JsonArray LoadCases()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Sleipnir.sln")))
            dir = dir.Parent;
        var path = Path.Combine(dir!.FullName, "clients", "codegen", "test", "fixtures", "compat", "cases.json");
        return JsonNode.Parse(File.ReadAllText(path))!["cases"]!.AsArray();
    }

    public static IEnumerable<object[]> CaseNames()
        => Cases.Select(c => new object[] { c!["name"]!.GetValue<string>() });

    private static string Line(JsonNode c)
        => string.Join(" | ", c["severity"], c["id"], c["site"], c["position"]?.ToString() ?? "null", c["direction"], c["message"]);

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void Case(string name)
    {
        var @case = Cases.Single(c => c!["name"]!.GetValue<string>() == name)!;

        var report = ContractCompatibility.Compare(
            @case["base"]!.ToJsonString(), @case["head"]!.ToJsonString(),
            includeRollback: @case["rollback"]!.GetValue<bool>());

        var actual = JsonNode.Parse(report.ToJson())!["changes"]!.AsArray().Select(c => Line(c!)).ToList();
        var expected = @case["expected"]!.AsArray().Select(c => Line(c!)).ToList();
        actual.Should().Equal(expected);
    }

    [Fact]
    public void Summary_CountsBySeverity()
    {
        var @case = Cases.Single(c => c!["name"]!.GetValue<string>().StartsWith("parameters: required added"))!;
        var report = ContractCompatibility.Compare(@case["base"]!.ToJsonString(), @case["head"]!.ToJsonString());

        (report.Breaking, report.Soft, report.Safe).Should().Be((2, 2, 1));
        report.ToText().Should().StartWith("Contract compatibility: 2 breaking, 2 soft, 1 safe");
        JsonNode.Parse(report.ToJson())!["summary"]!["breaking"]!.GetValue<int>().Should().Be(2);
    }

    [Fact]
    public void MalformedContract_IsRejected()
    {
        var act = () => ContractCompatibility.Compare("{\"discoveryVersion\":\"9\",\"controllers\":[],\"types\":{}}", "{}");
        act.Should().Throw<DiscoveryShapeException>();
    }
}
