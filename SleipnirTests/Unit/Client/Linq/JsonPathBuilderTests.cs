// Unit tests for the Sleipnir.Client.Linq expression→JsonPath translator and the Arg<T>/Dep<T>
// compile-time-safety surface. JsonPathBuilder is internal, reached via InternalsVisibleTo.
//
// Two groups:
//   1. JsonPathBuilder: selector→result-relative JsonPath, incl. the casing regression guard
//      (the spike's ToCamelCase only lowercased the first char → ID became iD while the wire has id,
//      a silent Unresolved). The fix reads [JsonPropertyName] then falls back to
//      JsonNamingPolicy.CamelCase (the server's actual transform → ID→id, IPAddress→ipAddress).
//   2. Arg<T>: the load-bearing compile-time check — a Dep<T> converts into Arg<T> only for the
//      same T. A reflection assertion pins that there is no implicit Dep<int>→Arg<string> conversion
//      (the guarantee the package sells; a commented-out snippet documents the compile error).
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json.Serialization;
using FluentAssertions;
using Sleipnir.Client.Linq;
using Xunit;

namespace SleipnirTests.Unit.Client.Linq;

public class JsonPathBuilderTests
{
    // --- Fixture types: [JsonPropertyName] where the wire name differs from the CamelCase fallback,
    //     and bare properties to exercise the CamelCase fallback across the acronym edge cases. ---

    private sealed class CasingDto
    {
        public int ID { get; set; }                 // CamelCase → "id" (acronym rule; spike gave "iD")
        public string IPAddress { get; set; } = ""; // CamelCase → "ipAddress" (spike gave "iPAaddress")
        public string Name { get; set; } = "";      // → "name"
        public int Id { get; set; }                 // → "id"
        public int CustomerId { get; set; }         // → "customerId"
        [JsonPropertyName("wireValue")]
        public int CustomNamed { get; set; }        // attribute wins → "wireValue"
    }

    private sealed class Outer { public Inner A { get; set; } = new(); }
    private sealed class Inner { public int B { get; set; } }

    // --- selector → JsonPath -----------------------------------------------------

    [Fact]
    public void Root_parameter_yields_dollar()
    {
        Expression<Func<CasingDto, CasingDto>> selector = x => x;
        JsonPathBuilder.Build(selector.Body).Should().Be("$");
    }

    [Fact]
    public void Member_access_yields_camelCase_property()
    {
        Expression<Func<CasingDto, string>> selector = x => x.Name;
        JsonPathBuilder.Build(selector.Body).Should().Be("$.name");
    }

    [Fact]
    public void Nested_member_access_yields_dotted_path()
    {
        Expression<Func<Outer, int>> selector = x => x.A.B;
        JsonPathBuilder.Build(selector.Body).Should().Be("$.a.b");
    }

    [Fact]
    public void List_index_then_member_yields_indexed_path()
    {
        // List<T>[i] compiles to a get_Item method call (not an IndexExpression) — generated contracts
        // emit List<T> for array-kind returns, so this is the realistic list-element selector path.
        Expression<Func<List<CasingDto>, int>> selector = x => x[0].Id;
        JsonPathBuilder.Build(selector.Body).Should().Be("$[0].id");
    }

    [Fact]
    public void Native_array_index_then_member_yields_indexed_path()
    {
        // Native arrays use ArrayIndex (a BinaryExpression), the other index path.
        Expression<Func<CasingDto[], int>> selector = x => x[1].Id;
        JsonPathBuilder.Build(selector.Body).Should().Be("$[1].id");
    }

    [Fact]
    public void Select_projection_yields_wildcard_multimatch_path()
    {
        // x.Select(e => e.Id) → "$[*].id". The server's multi-match rule collects every id into one
        // List<int> parameter (list fan-out into a parameter, not N requests) — the wildcard-Expose
        // path that makes explicit multi-hop dependency chains ergonomic.
        Expression<Func<List<CasingDto>, IEnumerable<int>>> selector = x => x.Select(e => e.Id);
        JsonPathBuilder.Build(selector.Body).Should().Be("$[*].id");
    }

    [Fact]
    public void Select_projection_then_member_yields_wildcard_dotted_path()
    {
        Expression<Func<List<Outer>, IEnumerable<int>>> selector = x => x.Select(o => o.A.B);
        JsonPathBuilder.Build(selector.Body).Should().Be("$[*].a.b");
    }

    [Fact]
    public void ToList_terminal_does_not_alter_path()
    {
        // .Select(...).ToList() fixes the C# return type (Dep<List<int>>) but not the JsonPath.
        Expression<Func<List<CasingDto>, List<int>>> selector = x => x.Select(e => e.CustomerId).ToList();
        JsonPathBuilder.Build(selector.Body).Should().Be("$[*].customerId");
    }

    [Fact]
    public void Cast_is_unwrapped()
    {
        Expression<Func<CasingDto, object>> selector = x => (object)x.Name;
        JsonPathBuilder.Build(selector.Body).Should().Be("$.name");
    }

    // --- casing regression guard (the bug the fix addresses) ---------------------

    [Theory]
    [InlineData(nameof(CasingDto.ID), "id")]             // acronym → lowercased fully
    [InlineData(nameof(CasingDto.IPAddress), "ipAddress")] // leading acronym + Word → ipAddress
    [InlineData(nameof(CasingDto.Name), "name")]
    [InlineData(nameof(CasingDto.Id), "id")]
    [InlineData(nameof(CasingDto.CustomerId), "customerId")]
    public void CamelCase_fallback_matches_server_wire_casing(string property, string expectedWire)
    {
        var prop = typeof(CasingDto).GetProperty(property)!;
        // Build x.<property> via a Parameter + MemberExpression.
        var param = Expression.Parameter(typeof(CasingDto), "x");
        var member = Expression.Property(param, prop);
        JsonPathBuilder.Build(member).Should().Be("$." + expectedWire,
            "the fallback must use JsonNamingPolicy.CamelCase (the server's transform), not a first-char-lower approximation");
    }

    [Fact]
    public void JsonPropertyName_attribute_wins_over_fallback()
    {
        var prop = typeof(CasingDto).GetProperty(nameof(CasingDto.CustomNamed))!;
        var param = Expression.Parameter(typeof(CasingDto), "x");
        var member = Expression.Property(param, prop);
        JsonPathBuilder.Build(member).Should().Be("$.wireValue",
            "the [JsonPropertyName] attribute is the drift-proof wire name and must take precedence over the CamelCase fallback");
    }
}

public class ArgTypeSafetyTests
{
    /// <summary>
    /// The guarantee the package sells: <see cref="Arg{T}"/> accepts a <see cref="Dep{T}"/> only for the
    /// SAME T. A mismatched <c>Dep&lt;int&gt;</c> does not convert into <c>Arg&lt;string&gt;</c>, so an
    /// <c>@alias</c> wired into the wrong-typed parameter is rejected by the compiler — not at runtime.
    /// This pins that contract via reflection (the operators are compiler-generated <c>op_Implicit</c>).
    /// </summary>
    [Fact]
    public void Arg_T_has_implicit_conversion_only_from_Dep_of_same_T()
    {
        var implicitOps = typeof(Arg<string>)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == "op_Implicit" && m.ReturnType == typeof(Arg<string>))
            .Select(m => m.GetParameters()[0].ParameterType)
            .ToList();

        implicitOps.Should().Contain(typeof(Dep<string>),
            "Arg<string> must be implicitly constructible from Dep<string> (the happy path)");
        implicitOps.Should().NotContain(typeof(Dep<int>),
            "Arg<string> must NOT accept Dep<int> — that is the compile-time type check that combats runtime uncertainty");
        // From a concrete value is also allowed (the literal path).
        implicitOps.Should().Contain(typeof(string),
            "Arg<string> must be implicitly constructible from a string literal");
    }

    // Compile-time-safety negative case — documented as a snippet because it is, by design, a
    // compile error and therefore cannot live in a compiling test body:
    //
    //   var linq = new SleipnirLinqClient(rest);
    //   var make = linq.Build((IDepChainService c) => c.MakeDto(7, "Foo"));
    //   Dep<int> id = make.Expose(o => o.Id);
    //   // CS0029: cannot convert 'Dep<int>' to 'Arg<string>' — there is no implicit conversion.
    //   var bad = linq.Build((IDepChainService c) => c.EchoString(id));
    //
    // EchoString(Arg<string>) rejects the Dep<int> at compile time. The reflection assertion above
    // pins the absence of that operator so an accidental widening cannot be reintroduced silently.
}