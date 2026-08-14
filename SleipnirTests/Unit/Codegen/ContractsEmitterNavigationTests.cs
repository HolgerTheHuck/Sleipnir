// Tests for the [SleipnirNavigation] emission + drift-gate in EmitContracts (Sleipnir.Codegen.Core).
// The codegen reads the `navigation` field from discovery (produced by the server-side
// [SleipnirNavigation] in SleipnirCommon) and re-emits the client-side [SleipnirNavigation] (in
// Sleipnir.Client.Linq) onto contract DTO properties. EmitContracts also drift-checks each edge at
// generation time (fetch/key/param/opaque-target) and refuses to emit on a mismatch — the gate that
// makes the string keys safe. These tests feed a QueryChain-shaped discovery fixture and assert the
// emitted attributes, then feed malformed inline discoveries and assert DiscoveryShapeException.
using System;
using System.IO;
using FluentAssertions;
using Sleipnir.Codegen.Core;
using Xunit;

namespace SleipnirTests.Unit.Codegen;

public class ContractsEmitterNavigationTests
{
    private static DirectoryInfo ResolveRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "stories"))
                && Directory.Exists(Path.Combine(dir.FullName, "clients"))
                && File.Exists(Path.Combine(dir.FullName, "Sleipnir.sln")))
            {
                return dir;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate repo root from " + AppContext.BaseDirectory);
    }

    private static string NavigationFixture()
    {
        var repo = ResolveRepoRoot();
        return File.ReadAllText(Path.Combine(repo.FullName, "clients", "codegen", "test", "fixtures", "navigation-discovery.json"));
    }

    private static string Emit() => SleipnirCodegen.EmitContracts(NavigationFixture());

    // ── Positive: emitted attribute shapes ───────────────────────────────────────────────

    [Fact]
    public void Emits_reference_navigation_with_explicit_param()
    {
        // Customer.Kontakt: reference nav, Key="kontaktId" (the FK on the parent), Param="kontaktIds".
        var cs = Emit();
        cs.Should().Contain("[SleipnirNavigation(Fetch = \"QueryChain.GetKontakte\", Key = \"kontaktId\", Param = \"kontaktIds\")]");
    }

    [Fact]
    public void Emits_collection_navigation_with_inferred_param()
    {
        // Customer.Bestellungen: the fixture OMITS param; the drift gate infers it from GetBestellungen'
        // single collection parameter ("customerIds") and the emitter always emits a non-null Param.
        var cs = Emit();
        cs.Should().Contain("[SleipnirNavigation(Fetch = \"QueryChain.GetBestellungen\", Key = \"id\", Param = \"customerIds\")]");
    }

    [Fact]
    public void Emits_childkey_only_when_declared()
    {
        // Kontakt.Ansprechpartner declares childKey="kontaktId" → it is emitted. The convention navs
        // (Customer.Kontakt / Bestellungen, Bestellung.Positions) omit childKey → it must NOT appear.
        var cs = Emit();
        cs.Should().Contain("[SleipnirNavigation(Fetch = \"QueryChain.GetAnsprechpartner\", Key = \"id\", ChildKey = \"kontaktId\", Param = \"kontaktIds\")]");
        // Exactly one nav declares a ChildKey (Ansprechpartner); the three convention navs omit it.
        var childKeyCount = 0;
        var idx = 0;
        while ((idx = cs.IndexOf("ChildKey = ", idx, StringComparison.Ordinal)) >= 0)
        {
            childKeyCount++;
            idx++;
        }
        childKeyCount.Should().Be(1);
    }

    [Fact]
    public void Emits_navigation_before_jsonpropertyname_on_the_property()
    {
        // The [SleipnirNavigation] attribute sits on the property, immediately before [JsonPropertyName].
        var cs = Emit();
        cs.Should().Contain("[SleipnirNavigation(Fetch = \"QueryChain.GetPositions\", Key = \"id\", Param = \"bestellungIds\")]\n        [JsonPropertyName(\"positions\")]");
    }

    [Fact]
    public void No_navigation_attribute_on_plain_properties()
    {
        // Leaf DTOs (Ansprechpartner, Position) and scalar columns carry no [SleipnirNavigation].
        var cs = Emit();
        // Count nav attributes: exactly 4 nav properties in the fixture.
        var count = 0;
        var idx = 0;
        while ((idx = cs.IndexOf("[SleipnirNavigation(", idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx++;
        }
        count.Should().Be(4);
    }

    // ── Negative: drift gate refuses to emit ─────────────────────────────────────────────

    // Minimal valid base: type T with scalar Id + ref Nav (navigation fetch C.M, key id, param ids);
    // controller C with method M(ids: List<int>) returning List<T>. Every variant below tweaks one
    // field to break exactly one drift-gate check.
    private const string BaseNav =
        "{\"discoveryVersion\":\"1\",\"controllers\":[{\"name\":\"C\",\"methods\":[{\"methodName\":\"M\",\"returnType\":{\"kind\":\"array\",\"element\":{\"kind\":\"ref\",\"ref\":\"T\"}},\"parameters\":[{\"parameterName\":\"ids\",\"parameterType\":{\"kind\":\"array\",\"element\":{\"kind\":\"scalar\",\"name\":\"int\"}}}]}]}],\"types\":{\"T\":{\"kind\":\"object\",\"typeName\":\"T\",\"properties\":[{\"propertyName\":\"Id\",\"propertyType\":{\"kind\":\"scalar\",\"name\":\"int\"}},{\"propertyName\":\"Nav\",\"propertyType\":{\"kind\":\"ref\",\"ref\":\"T\"},\"navigation\":{\"fetch\":\"C.M\",\"key\":\"id\",\"param\":\"ids\"}}]}}}";

    private static string Emit(string json) => SleipnirCodegen.EmitContracts(json);

    private static Action Emitting(string json) => () => SleipnirCodegen.EmitContracts(json);

    [Fact]
    public void Valid_base_emits_without_error()
    {
        // Sanity: the base fixture passes the drift gate and emits the attribute.
        var cs = Emit(BaseNav);
        cs.Should().Contain("[SleipnirNavigation(Fetch = \"C.M\", Key = \"id\", Param = \"ids\")]");
    }

    [Fact]
    public void Rejects_key_that_is_not_a_parent_property()
    {
        var json = BaseNav.Replace("\"key\":\"id\"", "\"key\":\"nope\"");
        Emitting(json).Should().Throw<DiscoveryShapeException>()
            .WithMessage("*key \"nope\" is not a property*");
    }

    [Fact]
    public void Rejects_unknown_fetch_controller()
    {
        var json = BaseNav.Replace("\"fetch\":\"C.M\"", "\"fetch\":\"D.M\"");
        Emitting(json).Should().Throw<DiscoveryShapeException>()
            .WithMessage("*unknown controller \"D\"*");
    }

    [Fact]
    public void Rejects_unknown_fetch_method()
    {
        var json = BaseNav.Replace("\"fetch\":\"C.M\"", "\"fetch\":\"C.N\"");
        Emitting(json).Should().Throw<DiscoveryShapeException>()
            .WithMessage("*unknown method \"N\"*");
    }

    [Fact]
    public void Rejects_fetch_without_controller_method_dot()
    {
        var json = BaseNav.Replace("\"fetch\":\"C.M\"", "\"fetch\":\"NoDot\"");
        Emitting(json).Should().Throw<DiscoveryShapeException>()
            .WithMessage("*\"NoDot\" is not a \"Controller.Method\" pair*");
    }

    [Fact]
    public void Rejects_explicit_param_that_is_not_a_collection()
    {
        // Point param at a scalar parameter: make the controller's `ids` a scalar int.
        var json = BaseNav.Replace("\"parameterType\":{\"kind\":\"array\",\"element\":{\"kind\":\"scalar\",\"name\":\"int\"}}",
                                   "\"parameterType\":{\"kind\":\"scalar\",\"name\":\"int\"}");
        Emitting(json).Should().Throw<DiscoveryShapeException>()
            .WithMessage("*param \"ids\" of \"C.M\" is not a collection*");
    }

    [Fact]
    public void Rejects_element_type_mismatch_between_key_and_fetch_param()
    {
        // Key is int (T.Id); make the fetch param element long → int != long.
        var json = BaseNav.Replace("\"element\":{\"kind\":\"scalar\",\"name\":\"int\"}}}]",
                                   "\"element\":{\"kind\":\"scalar\",\"name\":\"long\"}}}]");
        Emitting(json).Should().Throw<DiscoveryShapeException>()
            .WithMessage("*key \"id\" scalar \"int\" does not match fetch param \"ids\" element scalar \"long\"*");
    }

    [Fact]
    public void Rejects_navigation_to_opaque_target()
    {
        // Nav property targets an opaque type instead of a contract ref.
        var json = BaseNav.Replace("\"propertyType\":{\"kind\":\"ref\",\"ref\":\"T\"},\"navigation\"",
                                   "\"propertyType\":{\"kind\":\"opaque\",\"nativeName\":\"object\"},\"navigation\"");
        Emitting(json).Should().Throw<DiscoveryShapeException>()
            .WithMessage("*navigation target must be a contract type*");
    }

    [Fact]
    public void Rejects_inference_when_fetch_has_no_collection_parameter()
    {
        // Omit param; give the fetch method only a scalar parameter → nothing to infer from.
        var json = BaseNav
            .Replace("\"parameterType\":{\"kind\":\"array\",\"element\":{\"kind\":\"scalar\",\"name\":\"int\"}}",
                     "\"parameterType\":{\"kind\":\"scalar\",\"name\":\"int\"}")
            .Replace(",\"param\":\"ids\"", "");
        Emitting(json).Should().Throw<DiscoveryShapeException>()
            .WithMessage("*no collection parameter to infer*");
    }

    [Fact]
    public void Rejects_inference_when_fetch_has_multiple_collection_parameters()
    {
        // Omit param; give the fetch method two collection parameters → ambiguous inference.
        var json = BaseNav
            .Replace("\"parameters\":[{\"parameterName\":\"ids\",\"parameterType\":{\"kind\":\"array\",\"element\":{\"kind\":\"scalar\",\"name\":\"int\"}}}]",
                     "\"parameters\":[{\"parameterName\":\"ids\",\"parameterType\":{\"kind\":\"array\",\"element\":{\"kind\":\"scalar\",\"name\":\"int\"}}},{\"parameterName\":\"names\",\"parameterType\":{\"kind\":\"array\",\"element\":{\"kind\":\"scalar\",\"name\":\"string\"}}}]")
            .Replace(",\"param\":\"ids\"", "");
        Emitting(json).Should().Throw<DiscoveryShapeException>()
            .WithMessage("*multiple collection parameters*");
    }
}