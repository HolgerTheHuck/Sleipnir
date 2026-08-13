// Fixture for the Tier-2 SleipnirQuery<T> façade (Sleipnir.Client.Linq) — a deterministic in-memory
// Kunde→Kontakt→Ansprechpartner / Kunde→Bestellung graph exercised end-to-end through the real
// server (TransportTestFixture auto-discovers this controller). The DTOs carry the **client-side**
// [SleipnirNavigation] attribute (the same one EmitContracts will emit from the server-side model
// once B1/B2 land); the façade reads it to compile .Include/.ThenInclude into @alias edges. The server
// ignores the attribute — it only serializes the scalar/FK columns; the nav properties stay null on
// the wire and are stitched client-side at Materialize.
//
// Entity names are deliberately unprefixed ("Customer", not "QueryCustomer") so the collection-nav
// ChildKey convention CamelCase(parentEntityName)+"Id" resolves naturally:
//   Customer.Bestellungen → "customerId"   matches Bestellung.CustomerId
//   Kontakt.Ansprechpartner → "kontaktId"  matches Ansprechpartner.KontaktId
// The reference nav Customer.Kontakt uses the "id" (child PK) convention.
using Sleipnir.Client.Linq;
using SleipnirCore.Attributes;

namespace SleipnirTests.Fixtures;

// ── DTOs (shared: server return types + client contract types) ─────────────────────────

public class Customer
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int? KontaktId { get; set; }

    /// <summary>Reference navigation: one Kontakt per customer, joined Customer.KontaktId → Kontakt.Id.</summary>
    [SleipnirNavigation(Fetch = "QueryChain.GetKontakte", Key = "kontaktId", Param = "kontaktIds")]
    public Kontakt? Kontakt { get; set; }

    /// <summary>Collection navigation: many Bestellungen per customer, joined Customer.Id → Bestellung.CustomerId.</summary>
    [SleipnirNavigation(Fetch = "QueryChain.GetBestellungen", Key = "id", Param = "customerIds")]
    public List<Bestellung>? Bestellungen { get; set; }
}

public class Kontakt
{
    public int Id { get; set; }

    /// <summary>Collection navigation: many Ansprechpartner per kontakt, joined Kontakt.Id → Ansprechpartner.KontaktId.</summary>
    [SleipnirNavigation(Fetch = "QueryChain.GetAnsprechpartner", Key = "id", Param = "kontaktIds")]
    public List<Ansprechpartner>? Ansprechpartner { get; set; }
}

public class Ansprechpartner
{
    public int Id { get; set; }
    public int KontaktId { get; set; }
    public string Name { get; set; } = "";
}

public class Bestellung
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public string Art { get; set; } = "";

    /// <summary>Collection navigation off a collection leaf's element (exercises the covariant
    /// <c>ThenInclude(this ISleipnirQuery&lt;T,IEnumerable&lt;E&gt;?&gt;, e =&gt; e.Nav)</c> overload):
    /// after <c>.Include(c =&gt; c.Bestellungen)</c> the leaf is <c>List&lt;Bestellung&gt;?</c>, so
    /// <c>b =&gt; b.Positions</c> is checked against <c>Bestellung</c> (the element), not the list.</summary>
    [SleipnirNavigation(Fetch = "QueryChain.GetPositions", Key = "id", Param = "bestellungIds")]
    public List<Position>? Positions { get; set; }
}

public class Position
{
    public int Id { get; set; }
    public int BestellungId { get; set; }
    public string Name { get; set; } = "";
}

// ── Client contract (hand-written stand-in for a generated SleipnirContracts.g.cs) ────

/// <summary>
/// Hand-written contract mirroring a generated interface for the QueryChain controller. Only the
/// collection-root method needs to be on the contract — the navigation fetch methods are reached via
/// the [SleipnirNavigation].Fetch strings, never called directly through the LINQ client.
/// </summary>
[SleipnirServiceContract("QueryChain")]
public interface IQueryChainService
{
    [SleipnirMethodContract("SelectCustomers")]
    Task<List<Customer>?> SelectCustomers();
}

// ── Server controller (deterministic in-memory data, auto-discovered) ──────────────────

[SleipnirController("QueryChain")]
public class QueryChainController
{
    // Customers: 1→Kontakt 10, 2→Kontakt 11.
    // Ansprechpartner: 100,101→Kontakt 10 ; 102→Kontakt 11.
    // Bestellungen: 1000,1001→Customer 1 ; 1002→Customer 2.
    private static readonly List<Customer> _customers = new()
    {
        new() { Id = 1, Name = "Alpha", KontaktId = 10 },
        new() { Id = 2, Name = "Beta", KontaktId = 11 }
    };
    private static readonly List<Kontakt> _kontakte = new()
    {
        new() { Id = 10 },
        new() { Id = 11 }
    };
    private static readonly List<Ansprechpartner> _ansprechpartner = new()
    {
        new() { Id = 100, KontaktId = 10, Name = "AP-100" },
        new() { Id = 101, KontaktId = 10, Name = "AP-101" },
        new() { Id = 102, KontaktId = 11, Name = "AP-102" }
    };
    private static readonly List<Bestellung> _bestellungen = new()
    {
        new() { Id = 1000, CustomerId = 1, Art = "A" },
        new() { Id = 1001, CustomerId = 1, Art = "B" },
        new() { Id = 1002, CustomerId = 2, Art = "C" }
    };
    private static readonly List<Position> _positionen = new()
    {
        new() { Id = 5000, BestellungId = 1000, Name = "P-1" },
        new() { Id = 5001, BestellungId = 1000, Name = "P-2" },
        new() { Id = 5002, BestellungId = 1002, Name = "P-3" }
    };

    [SleipnirMethod("SelectCustomers")]
    public List<Customer> SelectCustomers() => _customers;

    [SleipnirMethod("GetKontakte")]
    public List<Kontakt> GetKontakte(List<int> kontaktIds)
        => _kontakte.Where(k => kontaktIds.Contains(k.Id)).ToList();

    [SleipnirMethod("GetAnsprechpartner")]
    public List<Ansprechpartner> GetAnsprechpartner(List<int> kontaktIds)
        => _ansprechpartner.Where(a => kontaktIds.Contains(a.KontaktId)).ToList();

    [SleipnirMethod("GetBestellungen")]
    public List<Bestellung> GetBestellungen(List<int> customerIds)
        => _bestellungen.Where(b => customerIds.Contains(b.CustomerId)).ToList();

    [SleipnirMethod("GetPositions")]
    public List<Position> GetPositions(List<int> bestellungIds)
        => _positionen.Where(p => bestellungIds.Contains(p.BestellungId)).ToList();
}