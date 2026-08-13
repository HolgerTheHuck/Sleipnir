// Unit tests for the Tier-2 SleipnirQuery<T> wire-compiler (LINQ_QUERY.md §4/§11). These do NOT hit a
// server — .Build() compiles the navigation chain into a plain SleipnirMultiRequest of @alias-wired
// calls, and we assert the exact wire shape (request count, controller/method, the @alias each fetch
// consumes, the dependencyMapping each provider exports, and the $[*].{key} path). The transport is a
// Moq dummy — Build() never calls it (only CreateFetchRequest, which constructs a SleipnirRequest).
//
// Coverage:
//   1-hop Include, 2-hop Include.ThenInclude, sibling Include (both consumers off the root),
//   the alias identifier pattern, .Where param binding, and the .Include/Param-negative guards.
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FluentAssertions;
using Moq;
using Sleipnir.Client.Linq;
using SleipnirClient.Sleipnir;
using SleipnirCommon.Models;
using SleipnirTests.Fixtures;
using Xunit;

namespace SleipnirTests.Unit.Client.Linq;

public class QueryBuildTests
{
    // A dummy transport — Build() does not send; only CreateFetchRequest runs, and it never touches
    // the transport. Keeps these tests pure (no Kestrel).
    private static SleipnirLinqClient NewClient() => new(new Mock<ISleipnirClient>().Object);

    private static readonly JsonSerializerOptions Opts = SleipnirLinqClient.Options;

    /// <summary>Deserialize a request's Params JSON array into the parameter models (wire names are camelCase).</summary>
    private static List<SleipnirParameter> ParamsOf(SleipnirRequest req)
        => req.Params is null
            ? new()
            : req.Params.Deserialize<List<SleipnirParameter>>(Opts)!;

    // --- 1-hop: From(...).Include(c => c.Kontakt) → 2 requests -----------------

    [Fact]
    public void Build_SingleInclude_ProducesTwoRequestsWithRootExport()
    {
        var linq = NewClient();
        var q = linq.From((IQueryChainService c) => c.SelectCustomers()).Include(c => c.Kontakt);
        var batch = q.Build();

        batch.Mode.Should().Be(ExecutionMode.Serial);
        batch.Requests.Should().HaveCount(2);

        // Root (node 0): exports the Kontakt edge's alias → "$[*].kontaktId".
        var root = batch.Requests[0];
        root.Controller.Should().Be("QueryChain");
        root.Method.Should().Be("SelectCustomers");
        root.DependencyMapping.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new KeyValuePair<string, string>("nav0", "$[*].kontaktId"));

        // Fetch (node 1): GetKontakte, one param "kontaktIds" wired to @nav0, no exports of its own.
        var fetch = batch.Requests[1];
        fetch.Controller.Should().Be("QueryChain");
        fetch.Method.Should().Be("GetKontakte");
        var p = ParamsOf(fetch).Single();
        p.ParameterName.Should().Be("kontaktIds");
        p.Data!.GetValue<string>().Should().Be("@nav0");
        fetch.DependencyMapping.Should().BeNull();
    }

    // --- 2-hop: From(...).Include(c => c.Kontakt).ThenInclude(k => k.Ansprechpartner) → 3 ----

    [Fact]
    public void Build_ThenInclude_ProducesThreeRequestsWithChainedExports()
    {
        var linq = NewClient();
        var q = linq.From((IQueryChainService c) => c.SelectCustomers())
                    .Include(c => c.Kontakt)
                    .ThenInclude(k => k.Ansprechpartner);
        var batch = q.Build();

        batch.Requests.Should().HaveCount(3);

        // Root exports nav0 (Kontakt key).
        batch.Requests[0].DependencyMapping!["nav0"].Should().Be("$[*].kontaktId");

        // Kontakt node consumes @nav0 AND exports nav1 (its own PK, the Ansprechpartner join key).
        var kontakt = batch.Requests[1];
        kontakt.Method.Should().Be("GetKontakte");
        ParamsOf(kontakt).Single().Data!.GetValue<string>().Should().Be("@nav0");
        kontakt.DependencyMapping!["nav1"].Should().Be("$[*].id");

        // Ansprechpartner node consumes @nav1, exports nothing.
        var ap = batch.Requests[2];
        ap.Method.Should().Be("GetAnsprechpartner");
        ParamsOf(ap).Single().ParameterName.Should().Be("kontaktIds");
        ParamsOf(ap).Single().Data!.GetValue<string>().Should().Be("@nav1");
        ap.DependencyMapping.Should().BeNull();
    }

    // --- sibling Include: two consumers off the root --------------------------

    [Fact]
    public void Build_SiblingInclude_BothConsumersOffRoot()
    {
        var linq = NewClient();
        var q = linq.From((IQueryChainService c) => c.SelectCustomers())
                    .Include(c => c.Kontakt)
                    .Include(c => c.Bestellungen);
        var batch = q.Build();

        batch.Requests.Should().HaveCount(3);

        // Root exports BOTH sibling aliases from the single Customer node.
        var root = batch.Requests[0].DependencyMapping!;
        root.Should().ContainKey("nav0").WhoseValue.Should().Be("$[*].kontaktId");
        root.Should().ContainKey("nav1").WhoseValue.Should().Be("$[*].id");

        // The two fetch nodes both consume a root alias (provider index 0).
        ParamsOf(batch.Requests[1]).Single().Data!.GetValue<string>().Should().Be("@nav0");
        batch.Requests[1].Method.Should().Be("GetKontakte");
        ParamsOf(batch.Requests[2]).Single().Data!.GetValue<string>().Should().Be("@nav1");
        batch.Requests[2].Method.Should().Be("GetBestellungen");
        ParamsOf(batch.Requests[2]).Single().ParameterName.Should().Be("customerIds");
    }

    // --- alias identifier pattern --------------------------------------------

    [Fact]
    public void Build_AliasesAreValidIdentifierPattern()
    {
        var linq = NewClient();
        var q = linq.From((IQueryChainService c) => c.SelectCustomers())
                    .Include(c => c.Kontakt)
                    .ThenInclude(k => k.Ansprechpartner)
                    .Include(c => c.Bestellungen);
        q.Build();

        var edges = ((SleipnirQueryBase)q).State.Edges;
        edges.Should().AllSatisfy(e =>
            Regex.IsMatch(e.Alias, @"^[A-Za-z0-9_]+$").Should().BeTrue(
                "the @alias must be a plain identifier so the server's '@'-prefix substitution parses it; got " + e.Alias));
        edges.Select(e => e.Alias).Should().BeEquivalentTo(new[] { "nav0", "nav1", "nav2" });
    }

    // --- .Where binds root-method params by wire name ------------------------

    [Fact]
    public void Build_Where_BindsRootParametersByWireName()
    {
        var linq = NewClient();
        // The filtered contract declares the root params .Where can bind (the server has no such
        // method — this is a pure wire-shape test, no send).
        var q = linq.From((IQueryChainFilteredService c) => c.SelectCustomers(0, ""))
                    .Where(c => c.Id == 7 && c.Name == "Foo");
        var batch = q.Build();

        var root = batch.Requests[0];
        var byName = ParamsOf(root).ToDictionary(p => p.ParameterName, p => p.Data);
        byName["id"]!.GetValue<int>().Should().Be(7);
        byName["name"]!.GetValue<string>().Should().Be("Foo");
    }

    [Fact]
    public void Build_Where_UnsupportedOperator_Throws()
    {
        var linq = NewClient();
        var q = linq.From((IQueryChainFilteredService c) => c.SelectCustomers(0, ""));
        var act = () => q.Where(c => c.Id > 5);
        act.Should().Throw<InvalidOperationException>(
            ".Where supports only == and && — a greater-than needs a server query engine, which Sleipnir is not.");
    }

    // --- negative guards ------------------------------------------------------

    [Fact]
    public void Build_Include_OnPropertyWithoutNavigationAttribute_Throws()
    {
        var linq = NewClient();
        var q = linq.From((IQueryChainService c) => c.SelectCustomers());
        // Name is a scalar column, not a navigation — no [SleipnirNavigation] on it.
        var act = () => q.Include(c => c.Name!);
        act.Should().Throw<InvalidOperationException>(
            "Include must target a [SleipnirNavigation] property; a scalar column carries no fetch edge.");
    }

    [Fact]
    public void Build_NavigationWithoutParam_Throws()
    {
        // A DTO whose nav declares no Param — the façade requires it so the fetch method knows which
        // parameter receives the key list. EmitContracts validates this at generation time; the client
        // guard is the last line of defense.
        var linq = NewClient();
        var q = linq.From((INoParamService c) => c.All());
        var act = () => q.Include(c => c.Child);
        act.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("Param");
    }

    // --- Compile-time-safety negatives (documented snippets — they are compile errors by design) ---
    //
    //   // ThenInclude off a reference leaf is compile-checked against the leaf type. After
    //   //   .Include(c => c.Kontakt)  the leaf is Kontakt, so k => k.Ansprechpartner type-checks.
    //   //   k => c.Bestellungen does NOT compile (c is Customer, the leaf is Kontakt):
    //   var bad = linq.From((IQueryChainService c) => c.SelectCustomers())
    //                 .Include(c => c.Kontakt)
    //                 .ThenInclude(k => k.Bestellungen); // CS1661: no overload accepts (Kontakt, …)
    //
    //   // ThenInclude off a COLLECTION leaf takes the element via covariance. After
    //   //   .Include(c => c.Bestellungen) the leaf is List<Bestellung>?, so b => b.Art checks
    //   //   against Bestellung (the element), not the list. b => b.Count (a List member) does NOT
    //   //   resolve to the collection overload and does not type-check against Bestellung either:
    //   var bad2 = linq.From((IQueryChainService c) => c.SelectCustomers())
    //                  .Include(c => c.Bestellungen)
    //                  .ThenInclude(b => b.Count); // CS1661: the ref-overload's lambda won't compile
    //                                              // against List<Bestellung>, and the collection
    //                                              // overload's lambda won't compile against List.
}

/// <summary>Filtered-root contract for the .Where wire-shape test (server method not needed — no send).</summary>
[SleipnirServiceContract("QueryChain")]
public interface IQueryChainFilteredService
{
    [SleipnirMethodContract("SelectCustomers")]
    Task<List<Customer>?> SelectCustomers(Arg<int> id, Arg<string> name);
}

/// <summary>Contract for the no-Param nav negative — its nav deliberately omits Param.</summary>
[SleipnirServiceContract("NoParam")]
public interface INoParamService
{
    [SleipnirMethodContract("All")]
    Task<List<NoParamParent>?> All();
}

public class NoParamParent
{
    public int Id { get; set; }
    // Fetch/Key set, Param deliberately empty → BuildEdge throws.
    [SleipnirNavigation(Fetch = "NoParam.GetChildren", Key = "id")]
    public List<NoParamChild>? Child { get; set; }
}

public class NoParamChild { public int Id { get; set; } public int ParentId { get; set; } }