// ==============================================================================
// Sleipnir Beispiel-Server (Drop-in) — unterstützt alle Szenarien aus samples/.
//
// Zeigt: Controller-Deklaration via [SleipnirController]/[SleipnirMethod], DTOs als
// Plain-POCOs (per Signatur-Inferenz in der Discovery expandiert — kein [SleipnirDataContract]
// mehr nötig), Fehlerbehandlung via SleipnirResults, und Rückgabewerte, die
// für Dependency-Chaining (@alias) geeignet sind.
//
// In-Memory-Store NUR für Demos — in Produktion DI-Repositories verwenden.
// ==============================================================================

using SleipnirCommon.Attribute;   // SleipnirDocumentation, SleipnirExample (DataContract entfällt — Inference-Default)
using SleipnirCommon.Models;     // SleipnirResponse
using SleipnirCommon.Results;     // SleipnirResults (Ok/NotFound/BadRequest)
using SleipnirCore.Attributes;    // SleipnirController, SleipnirMethod

namespace Sleipnir.Samples.Server;

// --- DTOs (der Vertrag) -------------------------------------------------------
// Plain-POCOs ohne [SleipnirDataContract] — die Discovery expandiert sie automatisch,
// weil sie in derselben Assembly wie die Controller liegen (Signatur-Inferenz, Weg C).

public class Customer
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
}

public class Order
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public decimal Total { get; set; }
    public DateTime CreatedAt { get; set; }
}

// --- In-Memory-Store (Demo) ---------------------------------------------------

// Static-Store nur für das Beispiel. In einer echten App via DI als Singleton/
// Scoped registrieren und in den Controller injizieren.
public static class DemoStore
{
    private static int _nextCustomerId = 0;
    private static int _nextOrderId = 0;
    private static readonly List<Customer> _customers = new();
    private static readonly List<Order> _orders = new();

    public static Task<int> AddCustomerAsync(string name, string email, CancellationToken ct)
    {
        var c = new Customer { Id = Interlocked.Increment(ref _nextCustomerId), Name = name, Email = email };
        _customers.Add(c);
        return Task.FromResult(c.Id); // Rückgabe = neue Id (ganzer int → JsonPath "$")
    }

    public static Task<Customer?> GetCustomerByIdAsync(int id, CancellationToken ct)
        => Task.FromResult(_customers.FirstOrDefault(c => c.Id == id));

    public static Task<IReadOnlyList<Customer>> GetAllCustomersAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<Customer>>(_customers);

    public static Task<int> CreateOrderAsync(int customerId, decimal total, CancellationToken ct)
    {
        // Business-Validierung → Domänenfehler als SleipnirResponse (kein Throw).
        if (!_customers.Any(c => c.Id == customerId))
            return Task.FromResult(-1); // siehe Controller: dort → SleipnirResults.BadRequest

        var o = new Order
        {
            Id = Interlocked.Increment(ref _nextOrderId),
            CustomerId = customerId,
            Total = total,
            CreatedAt = DateTime.UtcNow
        };
        _orders.Add(o);
        return Task.FromResult(o.Id); // Rückgabe = neue OrderId (→ JsonPath "$")
    }

    public static Task<Order?> GetOrderByIdAsync(int id, CancellationToken ct)
        => Task.FromResult(_orders.FirstOrDefault(o => o.Id == id));
}

// --- Controller ---------------------------------------------------------------

[SleipnirController("Customer")]
[SleipnirDocumentation("Kunden verwalten — AddCustomer liefert die neue Id (int).")]
public class CustomerController
{
    [SleipnirMethod("AddCustomer")]
    [SleipnirDocumentation("Legt einen Kunden an. Rückgabe: neue Id (skalarer int → JsonPath '$').")]
    public Task<int> AddCustomer(string name, string email, CancellationToken ct)
        => DemoStore.AddCustomerAsync(name, email, ct);

    [SleipnirMethod("GetCustomerById")]
    [SleipnirDocumentation("Lädt einen Kunden nach Id. Rückgabe: Customer oder 404.")]
    public async Task<SleipnirResponse> GetCustomerById(int id, CancellationToken ct)
    {
        var c = await DemoStore.GetCustomerByIdAsync(id, ct);
        return c is null
            ? SleipnirResults.NotFound($"Kunde {id} nicht gefunden")
            : SleipnirResults.Ok(c);
    }

    [SleipnirMethod("GetAllCustomers")]
    [SleipnirDocumentation("Alle Kunden. Rückgabe: Liste → JsonPath '$[0].Id' für das erste Element.")]
    public Task<IReadOnlyList<Customer>> GetAllCustomers(CancellationToken ct)
        => DemoStore.GetAllCustomersAsync(ct);
}

[SleipnirController("Order")]
[SleipnirDocumentation("Bestellungen verwalten — CreateOrder liefert die neue OrderId (int).")]
public class OrderController
{
    [SleipnirMethod("CreateOrder")]
    [SleipnirDocumentation("Legt eine Bestellung an. Parameter: customerId, total. Rückgabe: neue OrderId.")]
    public async Task<SleipnirResponse> CreateOrder(int customerId, decimal total, CancellationToken ct)
    {
        var orderId = await DemoStore.CreateOrderAsync(customerId, total, ct);
        // Domänenfehler → SleipnirResponse (Code+Message erreichen den Client unverfälscht).
        return orderId < 0
            ? SleipnirResults.BadRequest($"Kunde {customerId} existiert nicht")
            : SleipnirResults.Ok(orderId); // skalarer int → JsonPath "$"
    }

    [SleipnirMethod("GetOrderById")]
    [SleipnirDocumentation("Lädt eine Bestellung nach Id. Rückgabe: Order oder 404.")]
    public async Task<SleipnirResponse> GetOrderById(int id, CancellationToken ct)
    {
        var o = await DemoStore.GetOrderByIdAsync(id, ct);
        return o is null
            ? SleipnirResults.NotFound($"Bestellung {id} nicht gefunden")
            : SleipnirResults.Ok(o);
    }
}

// --- Server-Setup -------------------------------------------------------------
// Das ausführbare Setup liegt in Program.cs (dieses Verzeichnis) — drei Zeilen
// Sleipnir-Wiring (AddSleipnir → UseSleipnirTransports → MapSleipnir). Die [SleipnirController]-
// Typen dieser Datei werden per Attribut-Scan automatisch gefunden und beim
// Aufruf von UseSleipnir() im Invoker registriert — keine manuelle Registrierung.
//
// Start:  dotnet run --project samples/server/SampleServer.csproj