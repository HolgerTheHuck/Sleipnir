using FluentAssertions;
using SleipnirCore.Services.Helper;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace SleipnirTests.Unit.Core;

/// <summary>
/// Unit tests for DependencyResolver – JsonPath-based value extraction.
/// </summary>
public class DependencyResolverTests
{
    [Fact]
    public void ExtractValue_SimplePath_ReturnsValue()
    {
        // Arrange
        var json = "{\"Id\":42,\"Name\":\"Test\"}";

        // Act
        var result = DependencyResolver.ExtractValue(JsonDocument.Parse(json).RootElement.Clone(), "$.Id");

        // Assert
        result.Should().NotBeNull();
        result!.GetValue<int>().Should().Be(42);
    }

    [Fact]
    public void ExtractValue_NestedPath_ReturnsValue()
    {
        // Arrange
        var json = "{\"Customer\":{\"Id\":7,\"Name\":\"Alice\"}}";

        // Act
        var result = DependencyResolver.ExtractValue(JsonDocument.Parse(json).RootElement.Clone(), "$.Customer.Id");

        // Assert
        result.Should().NotBeNull();
        result!.GetValue<int>().Should().Be(7);
    }

    [Fact]
    public void ExtractValue_ArrayIndex_ReturnsValue()
    {
        // Arrange
        var json = "[10,20,30]";

        // Act
        var result = DependencyResolver.ExtractValue(JsonDocument.Parse(json).RootElement.Clone(), "$[1]");

        // Assert
        result.Should().NotBeNull();
        result!.GetValue<int>().Should().Be(20);
    }

    [Fact]
    public void ExtractValue_StringValue_ReturnsString()
    {
        // Arrange
        var json = "{\"Name\":\"Hello\"}";

        // Act
        var result = DependencyResolver.ExtractValue(JsonDocument.Parse(json).RootElement.Clone(), "$.Name");

        // Assert
        result.Should().NotBeNull();
        result!.GetValue<string>().Should().Be("Hello");
    }

    [Fact]
    public void ExtractValue_NonExistentPath_ReturnsNull()
    {
        // Arrange
        var json = "{\"Id\":1}";

        // Act
        var result = DependencyResolver.ExtractValue(JsonDocument.Parse(json).RootElement.Clone(), "$.NonExistent");

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void ExtractValue_RootPath_ReturnsEntireDocument()
    {
        // Arrange
        var json = "42";

        // Act
        var result = DependencyResolver.ExtractValue(JsonDocument.Parse(json).RootElement.Clone(), "$");

        // Assert
        result.Should().NotBeNull();
        result!.GetValue<int>().Should().Be(42);
    }

    // --- Wildcard / Multi-Match → JSON-Array (List-Fan-out, v1) -----------------

    /// <summary>
    /// Ein Wildcard-Pfad ($.items[*].id) liefert pro Treffer einen Match; statt nur
    /// des ersten (altes .Matches.First()) wird jetzt ein JSON-Array aller Treffer
    /// zurückgegeben — die Grundlage, damit List&lt;int&gt;-Parameter den ganzen Fächer
    /// statt nur eines Elements erhalten.
    /// </summary>
    [Fact]
    public void ExtractValue_WildcardPath_ReturnsArrayOfAllMatches()
    {
        // Arrange
        var json = "{\"items\":[{\"id\":1},{\"id\":2},{\"id\":3}]}";

        // Act
        var result = DependencyResolver.ExtractValue(
            JsonDocument.Parse(json).RootElement.Clone(), "$.items[*].id");

        // Assert — JSON-Array [1,2,3]
        result.Should().NotBeNull();
        result!.GetValueKind().Should().Be(JsonValueKind.Array);
        result.AsArray().Select(n => n!.GetValue<int>()).Should().Equal(1, 2, 3);
    }

    /// <summary>
    /// Wildcard über ein nacktes Array ($[*].id auf [{id:1},{id:2}]) — derselbe
    /// Multi-Match-Pfad ohne umschließendes Objekt.
    /// </summary>
    [Fact]
    public void ExtractValue_WildcardOverBareArray_ReturnsArrayOfAllMatches()
    {
        // Arrange
        var json = "[{\"id\":10},{\"id\":20}]";

        // Act
        var result = DependencyResolver.ExtractValue(
            JsonDocument.Parse(json).RootElement.Clone(), "$[*].id");

        // Assert
        result.Should().NotBeNull();
        result!.GetValueKind().Should().Be(JsonValueKind.Array);
        result.AsArray().Select(n => n!.GetValue<int>()).Should().Equal(10, 20);
    }

    /// <summary>
    /// String-Treffer werden genauso eingesammelt: $.items[*].name → ["One","Two"].
    /// Stellt sicher, dass der Fächer nicht nur für Zahlen, sondern typgetreu für
    /// jeden JSON-Wert (hier Strings) gebaut wird.
    /// </summary>
    [Fact]
    public void ExtractValue_WildcardStringProjection_ReturnsArrayOfStrings()
    {
        // Arrange
        var json = "{\"items\":[{\"name\":\"One\"},{\"name\":\"Two\"}]}";

        // Act
        var result = DependencyResolver.ExtractValue(
            JsonDocument.Parse(json).RootElement.Clone(), "$.items[*].name");

        // Assert
        result.Should().NotBeNull();
        result!.GetValueKind().Should().Be(JsonValueKind.Array);
        result.AsArray().Select(n => n!.GetValue<string>()).Should().Equal("One", "Two");
    }

    /// <summary>
    /// Rückwärtskompatibilität: ein konkreter Index-Pfad ($[0].id) liefert weiterhin
    /// genau den Skalar (kein Ein-Element-Array). Bewacht die „Count == 1 → Skalar"-Verzweigung.
    /// </summary>
    [Fact]
    public void ExtractValue_SingleMatchPath_ReturnsScalarNotOneElementArray()
    {
        // Arrange
        var json = "[{\"id\":1},{\"id\":2},{\"id\":3}]";

        // Act
        var result = DependencyResolver.ExtractValue(
            JsonDocument.Parse(json).RootElement.Clone(), "$[0].id");

        // Assert — Skalar 1, NICHT [1]
        result.Should().NotBeNull();
        result!.GetValueKind().Should().Be(JsonValueKind.Number);
        result.GetValue<int>().Should().Be(1);
    }

    /// <summary>
    /// Ein Pfad, der auf ein ganzes Array trifft („$" über einer List-Root), liefert
    /// genau diesen Array-Knoten als Skalar-Treffer (Count == 1) — nicht nochmal
    /// eingepackt. Die Unterscheidung „eine Selektion, die ein Array ist" vs. „viele
    /// Skalar-Treffer" folgt der Match-Anzahl, nicht dem Wert-Typ.
    /// </summary>
    [Fact]
    public void ExtractValue_RootPathOverArray_ReturnsArrayAsSingleMatch()
    {
        // Arrange
        var json = "[1,2,3]";

        // Act
        var result = DependencyResolver.ExtractValue(
            JsonDocument.Parse(json).RootElement.Clone(), "$");

        // Assert — das ganze Array (1 Match, Wert ist ein Array), unverändert
        result.Should().NotBeNull();
        result!.GetValueKind().Should().Be(JsonValueKind.Array);
        result.AsArray().Select(n => n!.GetValue<int>()).Should().Equal(1, 2, 3);
    }
}