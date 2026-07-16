using FluentAssertions;
using TrameCommon.Models;
using TrameCore.Services.Helper;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace TrameTests.Unit.Core;

/// <summary>
/// Unit tests for DependencyGraphBuilder – topological sorting and cycle detection.
/// </summary>
public class DependencyGraphBuilderTests
{
    private static TrameRequest CreateRequest(string id, string? stringData = "[]", Dictionary<string, string>? dependencyMapping = null)
    {
        return new TrameRequest
        {
            Controller = "Test",
            Method = "Method",
            Params = stringData == null ? null : JsonNode.Parse(stringData),
            Id = id,
            DependencyMapping = dependencyMapping
        };
    }

    [Fact]
    public void SortByDependencyBatches_NoDependencies_SingleBatch()
    {
        // Arrange
        var requests = new List<TrameRequest>
        {
            CreateRequest("a"),
            CreateRequest("b"),
            CreateRequest("c")
        };

        // Act
        var batches = DependencyGraphBuilder.SortByDependencyBatches(requests);

        // Assert
        batches.Should().HaveCount(1);
        batches[0].Should().HaveCount(3);
    }

    [Fact]
    public void SortByDependencyBatches_LinearChain_ThreeBatches()
    {
        // Arrange: a -> b -> c (b depends on a, c depends on b)
        var requests = new List<TrameRequest>
        {
            CreateRequest("a", dependencyMapping: new Dictionary<string, string> { { "valA", "$" } }),
            CreateRequest("b", stringData: "[{\"ParameterName\":\"x\",\"Data\":\"@valA\"}]", dependencyMapping: new Dictionary<string, string> { { "valB", "$" } }),
            CreateRequest("c", stringData: "[{\"ParameterName\":\"x\",\"Data\":\"@valB\"}]")
        };

        // Act
        var batches = DependencyGraphBuilder.SortByDependencyBatches(requests);

        // Assert
        batches.Should().HaveCount(3);
        batches[0].Should().ContainSingle(r => r.Id == "a");
        batches[1].Should().ContainSingle(r => r.Id == "b");
        batches[2].Should().ContainSingle(r => r.Id == "c");
    }

    [Fact]
    public void SortByDependencyBatches_Diamond_TwoBatches()
    {
        // Arrange: a provides valA, b and c both depend on valA, d depends on valB and valC
        // Batch 1: a
        // Batch 2: b, c (parallel)
        // Batch 3: d
        var requests = new List<TrameRequest>
        {
            CreateRequest("a", dependencyMapping: new Dictionary<string, string> { { "valA", "$" } }),
            CreateRequest("b", stringData: "[{\"ParameterName\":\"x\",\"Data\":\"@valA\"}]", dependencyMapping: new Dictionary<string, string> { { "valB", "$" } }),
            CreateRequest("c", stringData: "[{\"ParameterName\":\"x\",\"Data\":\"@valA\"}]", dependencyMapping: new Dictionary<string, string> { { "valC", "$" } }),
            CreateRequest("d", stringData: "[{\"ParameterName\":\"x\",\"Data\":\"@valB\"},{\"ParameterName\":\"y\",\"Data\":\"@valC\"}]")
        };

        // Act
        var batches = DependencyGraphBuilder.SortByDependencyBatches(requests);

        // Assert
        batches.Should().HaveCount(3);
        batches[0].Should().ContainSingle(r => r.Id == "a");
        batches[1].Should().HaveCount(2);
        batches[1].Select(r => r.Id).Should().Contain(new[] { "b", "c" });
        batches[2].Should().ContainSingle(r => r.Id == "d");
    }

    [Fact]
    public void SortByDependencyBatches_Cycle_ThrowsInvalidOperationException()
    {
        // Arrange: a depends on b, b depends on a
        var requests = new List<TrameRequest>
        {
            CreateRequest("a", stringData: "[{\"ParameterName\":\"x\",\"Data\":\"@valB\"}]", dependencyMapping: new Dictionary<string, string> { { "valA", "$" } }),
            CreateRequest("b", stringData: "[{\"ParameterName\":\"x\",\"Data\":\"@valA\"}]", dependencyMapping: new Dictionary<string, string> { { "valB", "$" } })
        };

        // Act
        var act = () => DependencyGraphBuilder.SortByDependencyBatches(requests);

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Zyklus*");
    }

    [Fact]
    public void SortByDependencyBatches_EmptyList_ReturnsEmpty()
    {
        // Act
        var batches = DependencyGraphBuilder.SortByDependencyBatches(new List<TrameRequest>());

        // Assert
        batches.Should().BeEmpty();
    }

    [Fact]
    public void SortByDependencyBatches_IndependentRequestsInFirstBatch()
    {
        // Arrange: 3 independent requests + 1 dependent
        var requests = new List<TrameRequest>
        {
            CreateRequest("a", dependencyMapping: new Dictionary<string, string> { { "valA", "$" } }),
            CreateRequest("indep1"),
            CreateRequest("indep2"),
            CreateRequest("dep", stringData: "[{\"ParameterName\":\"x\",\"Data\":\"@valA\"}]")
        };

        // Act
        var batches = DependencyGraphBuilder.SortByDependencyBatches(requests);

        // Assert
        batches.Should().HaveCount(2);
        batches[0].Should().HaveCount(3); // a, indep1, indep2
        batches[1].Should().ContainSingle(r => r.Id == "dep");
    }
}