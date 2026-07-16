using System.Text.Json;
using TrameCore.Attributes;

namespace TrameStories.Story01;

// === Story 01 Domain — code-first contract (no IDL, no .proto) =====================
// Die Klassen SIND der Vertrag. Der Trame-Server entdeckt sie per [TrameController]
// und exponiert sie 1:1. camelCase-Wire (id, customerId, articleId, …) — die JsonPath-
// Exposes im Batch müssen daher camelCase sein ($.customerId, $[*].articleId).

public sealed class Order
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public int ShippingAddressId { get; set; }
    public string Status { get; set; } = "";
    public DateTime PlacedAt { get; set; }
}

public sealed class Customer
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public sealed class OrderLine
{
    public int ArticleId { get; set; }
    public int Qty { get; set; }
}

public sealed class Article
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
}

public sealed class Address
{
    public int Id { get; set; }
    public string Street { get; set; } = "";
    public string Zip { get; set; } = "";
    public string City { get; set; } = "";
}

public sealed class StockInfo
{
    public int ArticleId { get; set; }
    public int InStock { get; set; }
}

/// <summary>
/// Simulierte Dienst-Latenz pro Controller-Aufruf — auf Localhost ist echter Netzwerk
/// kaum spürbar, deshalb wird hier pro Service-Call ein fester Delay aufgebracht, damit
/// der Unterschied „6 serielle Roundtrips vs. 1 Batch mit serverseitig paralleler
/// Graphen-Ausführung" sichtbar wird. Im echten Deployment ersetzt das die Netzwegzeit.
/// </summary>
internal static class StoryLatency
{
    public const int MsPerCall = 30;
    public static Task Wait() => Task.Delay(MsPerCall);

    // Wire ist camelCase, die C#-Typen PascalCase → case-insensitiv lesen.
    public static readonly JsonSerializerOptions WireOptions = new() { PropertyNameCaseInsensitive = true };
}

// === In-Memory-Backing-Stores (geseedet für Order #42) =============================

internal static class Store
{
    public static readonly Dictionary<int, Order> Orders = new()
    {
        [42] = new Order { Id = 42, CustomerId = 7, ShippingAddressId = 101, Status = "Open", PlacedAt = new DateTime(2026, 7, 1) },
    };

    public static readonly Dictionary<int, Customer> Customers = new()
    {
        [7] = new Customer { Id = 7, Name = "Contoso Ltd." },
    };

    public static readonly Dictionary<int, List<OrderLine>> LinesByOrder = new()
    {
        [42] = new()
        {
            new() { ArticleId = 1001, Qty = 2 },
            new() { ArticleId = 1002, Qty = 1 },
            new() { ArticleId = 1003, Qty = 5 },
        },
    };

    public static readonly Dictionary<int, Article> Articles = new()
    {
        [1001] = new() { Id = 1001, Name = "Widget", Price = 9.99m },
        [1002] = new() { Id = 1002, Name = "Gadget", Price = 19.99m },
        [1003] = new() { Id = 1003, Name = "Sprocket", Price = 2.49m },
    };

    public static readonly Dictionary<int, Address> Addresses = new()
    {
        [101] = new() { Id = 101, Street = "1 Market St", Zip = "94105", City = "San Francisco" },
    };

    public static readonly Dictionary<int, int> Stock = new()
    {
        [1001] = 120,
        [1002] = 0,
        [1003] = 43,
    };

    public static List<Article> GetManyArticles(List<int> ids)
        => ids.Distinct().Select(id => Articles.GetValueOrDefault(id)).Where(a => a is not null).ToList()!;

    public static List<StockInfo> GetManyStock(List<int> ids)
        => ids.Distinct().Select(id => new StockInfo { ArticleId = id, InStock = Stock.GetValueOrDefault(id, -1) }).ToList();
}

// === Controller — der Trame-Vertrag =================================================

[TrameController("Order")]
public class OrderController
{
    [TrameMethod("GetById")]
    public async Task<Order?> GetById(int id)
    {
        await StoryLatency.Wait();
        return Store.Orders.GetValueOrDefault(id);
    }
}

[TrameController("Customer")]
public class CustomerController
{
    // Parametername `customerId` entspricht dem Alias-Namen → Bindung nach Name.
    [TrameMethod("GetById")]
    public async Task<Customer?> GetById(int customerId)
    {
        await StoryLatency.Wait();
        return Store.Customers.GetValueOrDefault(customerId);
    }
}

[TrameController("OrderLine")]
public class OrderLineController
{
    [TrameMethod("GetByOrder")]
    public async Task<List<OrderLine>> GetByOrder(int orderId)
    {
        await StoryLatency.Wait();
        return Store.LinesByOrder.GetValueOrDefault(orderId) ?? new();
    }
}

[TrameController("Article")]
public class ArticleController
{
    // List<int> wird aus dem Multi-Match-Pfad $[*].articleId injiziert (ein Parameter,
    // nie Fan-out in N Requests). Bulk-nach-Primärschlüssel — heißt GetByIds, symmetrisch
    // zu GetById (Finder nach Fremdschlüssel heißen GetBy*, s. Stock.GetByArticles).
    [TrameMethod("GetByIds")]
    public async Task<List<Article>> GetByIds(List<int> articleIds)
    {
        await StoryLatency.Wait();
        return Store.GetManyArticles(articleIds);
    }
}

[TrameController("Address")]
public class AddressController
{
    [TrameMethod("GetById")]
    public async Task<Address?> GetById(int addressId)
    {
        await StoryLatency.Wait();
        return Store.Addresses.GetValueOrDefault(addressId);
    }
}

[TrameController("Stock")]
public class StockController
{
    // Zweiter Consumer desselben `articleIds`-Alias → Diamond im Dependency-Graph.
    [TrameMethod("GetByArticles")]
    public async Task<List<StockInfo>> GetByArticles(List<int> articleIds)
    {
        await StoryLatency.Wait();
        return Store.GetManyStock(articleIds);
    }
}