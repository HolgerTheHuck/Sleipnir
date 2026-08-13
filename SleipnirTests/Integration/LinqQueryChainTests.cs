// Integration test for the Tier-2 SleipnirQuery<T> façade (Sleipnir.Client.Linq) against the real
// Sleipnir server. Runs a navigation batch through the in-process Kestrel fixture against the
// deterministic QueryChain controller (LINQ_QUERY.md §9 worked example), so the whole
// .From/.Include/.ThenInclude/.Build path — compiled client-side into a plain @alias/dependencyMapping
// multi-request — is dispatched through the server's auto-selected topological batch executor and the
// flat per-node response lists are stitched back into the nested client-side graph at Materialize.
using FluentAssertions;
using Sleipnir.Client.Linq;
using SleipnirTests.Fixtures;
using Xunit;

namespace SleipnirTests.Integration;

public class LinqQueryChainTests : IClassFixture<TransportTestFixture>
{
    private readonly TransportTestFixture _fixture;

    public LinqQueryChainTests(TransportTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Include_ReferenceNavigation_StitchesSingleChildPerParent()
    {
        using var rest = _fixture.CreateRestClient();
        var linq = new SleipnirLinqClient(rest);

        var query = linq.From((IQueryChainService c) => c.SelectCustomers())
                       .Include(c => c.Kontakt);
        var responses = await linq.SendAsync(query.Build());
        var customers = linq.Materialize(query, responses);

        customers.Should().HaveCount(2);
        customers[0].Kontakt.Should().NotBeNull();
        customers[0].Kontakt!.Id.Should().Be(10);
        customers[1].Kontakt.Should().NotBeNull();
        customers[1].Kontakt!.Id.Should().Be(11);
        // A non-included collection nav stays null (no fetch node, no stitch).
        customers[0].Bestellungen.Should().BeNull();
    }

    [Fact]
    public async Task ThenInclude_ChainsOffReferenceLeaf_StitchesGrandchildren()
    {
        using var rest = _fixture.CreateRestClient();
        var linq = new SleipnirLinqClient(rest);

        // Customer → Kontakt (ref) → Ansprechpartner (collection). ThenInclude operates on the
        // reference leaf (Kontakt), so the Ansprechpartner edge providers from the Kontakt node.
        var query = linq.From((IQueryChainService c) => c.SelectCustomers())
                        .Include(c => c.Kontakt)
                        .ThenInclude(k => k.Ansprechpartner);
        var responses = await linq.SendAsync(query.Build());
        var customers = linq.Materialize(query, responses);

        // Kontakt 10 has AP 100,101 ; Kontakt 11 has AP 102.
        customers[0].Kontakt!.Ansprechpartner!.Select(a => a.Id).Should().Equal(100, 101);
        customers[1].Kontakt!.Ansprechpartner!.Select(a => a.Id).Should().Equal(102);
    }

    [Fact]
    public async Task SiblingInclude_LoadsTwoRootNavigationsInOneBatch()
    {
        using var rest = _fixture.CreateRestClient();
        var linq = new SleipnirLinqClient(rest);

        // Two sibling Includes off the root: Kontakt (ref) + Bestellungen (collection). Both consume
        // a root-exported alias; the server fans them out in the topological batch.
        var query = linq.From((IQueryChainService c) => c.SelectCustomers())
                        .Include(c => c.Kontakt)
                        .Include(c => c.Bestellungen);
        var responses = await linq.SendAsync(query.Build());
        var customers = linq.Materialize(query, responses);

        customers[0].Bestellungen!.Select(b => b.Id).Should().Equal(1000, 1001);
        customers[1].Bestellungen!.Select(b => b.Id).Should().Equal(1002);
        customers[0].Kontakt!.Id.Should().Be(10);
    }

    [Fact]
    public async Task ThenInclude_OffCollectionLeaf_StitchesViaElementCovariance()
    {
        using var rest = _fixture.CreateRestClient();
        var linq = new SleipnirLinqClient(rest);

        // Customer → Bestellungen (collection) → Positions (collection off the element). The
        // collection-leaf ThenInclude overload resolves the element (Bestellung) via interface
        // covariance and providers the Positions edge from the Bestellung node.
        var query = linq.From((IQueryChainService c) => c.SelectCustomers())
                        .Include(c => c.Bestellungen)
                        .ThenInclude(b => b.Positions);
        var responses = await linq.SendAsync(query.Build());
        var customers = linq.Materialize(query, responses);

        // Bestellung 1000 has P-5000,P-5001 ; 1001 has none (empty list, not null) ; 1002 has P-5002.
        var b1 = customers[0].Bestellungen!;
        b1.Single(b => b.Id == 1000).Positions!.Select(p => p.Id).Should().Equal(5000, 5001);
        b1.Single(b => b.Id == 1001).Positions.Should().NotBeNull().And.BeEmpty(
            "a parent with no children must get an empty list, not null — the stitcher never leaves a "
            + "collection nav unset when the edge was Included");
        customers[1].Bestellungen!.Single(b => b.Id == 1002).Positions!.Select(p => p.Id).Should().Equal(5002);
    }

    [Fact]
    public async Task FullThreeHopGraph_StitchesTheWholeTree()
    {
        using var rest = _fixture.CreateRestClient();
        var linq = new SleipnirLinqClient(rest);

        // The full graph: Customers → Kontakt → Ansprechpartner, plus sibling Bestellungen — five
        // nodes, four edges, one topological batch.
        var query = linq.From((IQueryChainService c) => c.SelectCustomers())
                        .Include(c => c.Kontakt)
                        .ThenInclude(k => k.Ansprechpartner)
                        .Include(c => c.Bestellungen);
        var responses = await linq.SendAsync(query.Build());
        var customers = linq.Materialize(query, responses);

        customers.Should().HaveCount(2);

        // Alpha (1): Kontakt 10 → AP [100,101]; Bestellungen [1000,1001].
        var alpha = customers.Single(c => c.Id == 1);
        alpha.Kontakt!.Id.Should().Be(10);
        alpha.Kontakt!.Ansprechpartner!.Select(a => a.Name).Should().Equal("AP-100", "AP-101");
        alpha.Bestellungen!.Select(b => b.Art).Should().Equal("A", "B");

        // Beta (2): Kontakt 11 → AP [102]; Bestellungen [1002].
        var beta = customers.Single(c => c.Id == 2);
        beta.Kontakt!.Ansprechpartner!.Select(a => a.Id).Should().Equal(102);
        beta.Bestellungen!.Select(b => b.Id).Should().Equal(1002);
    }
}