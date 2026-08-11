// Slice-2 proof: this Program compiles ONLY because the Sleipnir source generator emitted
// SleipnirGenerated.cs from contract.sleipnir.json during this project's build. Every type and
// controller/method below (SleipnirGeneratedClient, Order, Customer, OrderLine, Article, Stock,
// Address, client.Order.GetById, batch.Add/Exposes/Alias, resp.Get<T>) comes from generated code
// — none of it is hand-written. A drift between the server's runtime discovery and the committed
// contract would surface at the server-side drift-check (Slice 3); here the contract is assumed
// valid and the generator turns it into C#. Main is not executed against a live server; it exists
// so the typed diamond is exercised by the compiler.
using System.Collections.Generic;
using System.Threading.Tasks;
using Sleipnir.Generated;

namespace Sleipnir.Samples.GeneratedClient;

public static class Program
{
    public static async Task Main()
    {
        var client = new SleipnirGeneratedClient("http://localhost:5001");

        // Single typed call: Call<T> deserializes into the generated POCO.
        var order = await client.Call<Order>(client.Order.GetById(42));

        // Typed diamond batch (Serial — required for @alias resolution). This is the same shape as
        // the TS cs-compile harness and the CsCodegenParityTests compile gate.
        var batch = new Batch();
        var o = batch.Add(client.Order.GetById(42))
            .Exposes("$.customerId", "@customerId")
            .Exposes("$.id", "@orderId")
            .Exposes("$.shippingAddressId", "@addressId");
        batch.Add(client.Customer.GetById(o.Alias("@customerId")));
        var lines = batch.Add(client.OrderLine.GetByOrder(o.Alias("@orderId")))
            .Exposes("$[*].articleId", "@articleIds");
        batch.Add(client.Article.GetByIds(lines.Alias("@articleIds")));
        batch.Add(client.Stock.GetByArticles(lines.Alias("@articleIds")));
        batch.Add(client.Address.GetById(o.Alias("@addressId")));

        var resp = await client.Batch(batch);
        // Fetch results by request id (topological order is not request order).
        var fetchedOrder = resp.Get<Order>("Order.GetById");
        var customer = resp.Get<Customer>("Customer.GetById");
        var fetchedLines = resp.Get<List<OrderLine>>("OrderLine.GetByOrder");
        var articles = resp.Get<List<Article>>("Article.GetByIds");
        var stock = resp.Get<List<StockInfo>>("Stock.GetByArticles");
        var address = resp.Get<Address>("Address.GetById");
    }
}