// Unit tests for the [SleipnirNavigation] client attribute + the ChildKey-convention inference in
// Navigation.BuildEdge (LINQ_QUERY.md §3/§5). Reaches the internal QueryState.Edges via InternalsVisibleTo
// to assert the resolved child join-back key without running a server.
//
//   reference nav   → child PK "id"
//   collection nav  → child FK "CamelCase(parentEntityName)Id"  (Customer→customerId, Kontakt→kontaktId)
//   explicit ChildKey always wins.
using System.Linq.Expressions;
using FluentAssertions;
using Moq;
using Sleipnir.Client.Linq;
using SleipnirClient.Sleipnir;
using SleipnirTests.Fixtures;
using Xunit;

namespace SleipnirTests.Unit.Client.Linq;

public class NavigationAttributeTests
{
    private static SleipnirLinqClient NewClient() => new(new Mock<ISleipnirClient>().Object);

    private static NavigationEdge SingleEdge<TProp>(SleipnirLinqClient linq, Expression<Func<Customer, TProp>> nav)
    {
        var q = linq.From((IQueryChainService c) => c.SelectCustomers()).Include(nav);
        return ((SleipnirQueryBase)q).State.Edges[0];
    }

    // --- attribute is read off the selector's property ----------------------

    [Fact]
    public void Include_ReadsNavigationAttribute_OffTheSelectorProperty()
    {
        var edge = SingleEdge(NewClient(), c => c.Kontakt);
        edge.FetchController.Should().Be("QueryChain");
        edge.FetchMethod.Should().Be("GetKontakte");
        edge.KeyWire.Should().Be("kontaktId");      // parent's per-element key (Customer.KontaktId)
        edge.Param.Should().Be("kontaktIds");
        edge.NavProperty.Name.Should().Be("Kontakt");
    }

    // --- ChildKey convention: reference nav → child PK "id" -----------------

    [Fact]
    public void ReferenceNavigation_InfersChildKeyId_ByConvention()
    {
        // Customer.Kontakt is a reference nav → child joins by PK "id" (Kontakt.Id).
        var edge = SingleEdge(NewClient(), c => c.Kontakt);
        edge.IsCollectionNav.Should().BeFalse();
        edge.ChildKeyWire.Should().Be("id",
            "a reference navigation's child is the parent's single related row, joined by the child PK");
    }

    // --- ChildKey convention: collection nav → child FK "{parent}Id" ------

    [Fact]
    public void CollectionNavigation_InfersChildKeyParentEntityId_ByConvention()
    {
        // Customer.Bestellungen → child FK "customerId" (Bestellung.CustomerId).
        var bestell = SingleEdge(NewClient(), c => c.Bestellungen);
        bestell.IsCollectionNav.Should().BeTrue();
        bestell.ChildKeyWire.Should().Be("customerId");

        // Kontakt.Ansprechpartner → child FK "kontaktId" (Ansprechpartner.KontaktId).
        var linq = NewClient();
        var q = linq.From((IQueryChainService c) => c.SelectCustomers())
                    .Include(c => c.Kontakt)
                    .ThenInclude(k => k.Ansprechpartner);
        ((SleipnirQueryBase)q).State.Edges[1].ChildKeyWire.Should().Be("kontaktId");
    }

    // --- collection leaf's element type is resolved (covariant ThenInclude) --

    [Fact]
    public void CollectionLeaf_ThenInclude_ResolvesElementTypeAsProvider()
    {
        // .Include(c => c.Bestellungen).ThenInclude(b => b.Positions): the provider is the Bestellung
        // node (the collection's ELEMENT, not the list), so the Positions edge's child element is Position.
        var linq = NewClient();
        var q = linq.From((IQueryChainService c) => c.SelectCustomers())
                    .Include(c => c.Bestellungen)
                    .ThenInclude(b => b.Positions);
        var positionsEdge = ((SleipnirQueryBase)q).State.Edges[1];
        positionsEdge.ProviderIndex.Should().Be(1,
            "ThenInclude off a collection leaf providers from the collection's element node, reached via covariance");
        positionsEdge.ChildElementType.Should().Be<Position>();
        positionsEdge.ChildKeyWire.Should().Be("bestellungId");   // Bestellung → "bestellungId"
        positionsEdge.KeyWire.Should().Be("id");                  // Bestellung.Id
    }

    // --- explicit ChildKey overrides the convention ------------------------

    [Fact]
    public void Explicit_ChildKey_OverridesConvention()
    {
        var linq = NewClient();
        var q = linq.From((IConventionOverrideService c) => c.All()).Include(p => p.Children);
        ((SleipnirQueryBase)q).State.Edges[0].ChildKeyWire.Should().Be("parentId",
            "an explicit [SleipnirNavigation].ChildKey must win over the CamelCase(parent)+Id convention");
    }

    // --- the per-element key path composes to "$[*].{Key}" (Tier 2) ---------

    [Fact]
    public void CollectionRoot_KeyPath_IsWildcardDottedKey()
    {
        var batch = NewClient()
            .From((IQueryChainService c) => c.SelectCustomers())
            .Include(c => c.Kontakt)
            .Build();
        // Tier 2: every node is a collection, so the per-element export path is uniformly "$[*].{key}".
        batch.Requests[0].DependencyMapping!["nav0"].Should().Be("$[*].kontaktId");
    }
}

/// <summary>Contract for the explicit-ChildKey override case (server method not needed — no send).</summary>
[SleipnirServiceContract("ConventionOverride")]
public interface IConventionOverrideService
{
    [SleipnirMethodContract("All")]
    Task<List<ConventionParent>?> All();
}

public class ConventionParent
{
    public int Id { get; set; }
    // Convention would give "conventionParentId"; explicit says "parentId".
    [SleipnirNavigation(Fetch = "ConventionOverride.GetChildren", Key = "id", ChildKey = "parentId", Param = "parentIds")]
    public List<ConventionChild>? Children { get; set; }
}

public class ConventionChild { public int Id { get; set; } public int ParentId { get; set; } }