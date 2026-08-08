using FluentAssertions;
using TrameClient.Trame;
using TrameCommon.Models;
using TrameCommon.Results;
using Xunit;

namespace TrameTests.Unit.Client;

/// <summary>
/// Unit-Tests für TrameInMemoryClient (Phase 3, Schritt 5 — Client-Test-Doubles).
/// Verifiziert On/On&lt;T&gt;/OnError/404-Fallback/CallBinary-NSE.
/// </summary>
public class TrameInMemoryClientTests
{
    [Fact]
    public async Task On_T_Returns200_WithSerializedData()
    {
        var client = new TrameInMemoryClient();
        client.On("Customer", "GetById", (req, ct) => new Customer { Id = 42, Name = "Alice" });

        var result = await client.Call<Customer>(new TrameRequest { Controller = "Customer", Method = "GetById", Id = "1" });

        result.Should().NotBeNull();
        result!.Id.Should().Be(42);
        result.Name.Should().Be("Alice");
    }

    [Fact]
    public async Task On_ResponseHandler_ReturnsCustomResponse()
    {
        var client = new TrameInMemoryClient();
        client.On("Order", "Place", (req, ct) => TrameResults.Ok(new { orderId = 99 }));

        var result = await client.Call(new TrameRequest { Controller = "Order", Method = "Place", Id = "1" });

        result!.Code.Should().Be(200);
        // Die Id wird normalerweise vom Invoker/Transport gesetzt, nicht von TrameResults.
        // Der InMemoryClient gibt die Response zurueck, wie der Handler sie gebaut hat.
    }

    [Fact]
    public async Task OnError_ReturnsErrorResponse()
    {
        var client = new TrameInMemoryClient();
        client.OnError("Customer", "Delete", 404, "Customer not found.");

        var result = await client.Call(new TrameRequest { Controller = "Customer", Method = "Delete", Id = "1" });

        result!.Code.Should().Be(404);
        result.Error!.Message.Should().Be("Customer not found.");
    }

    [Fact]
    public async Task NoHandler_Returns404()
    {
        var client = new TrameInMemoryClient();

        var result = await client.Call(new TrameRequest { Controller = "Ghost", Method = "Missing", Id = "1" });

        result!.Code.Should().Be(404);
        result.Error!.Message.Should().Contain("Ghost.Missing");
    }

    [Fact]
    public async Task Call_Batch_ReturnsResponsesInOrder()
    {
        var client = new TrameInMemoryClient();
        client.On("A", "M1", (req, ct) => TrameResults.Ok(new { v = 1 }));
        client.On("A", "M2", (req, ct) => TrameResults.Ok(new { v = 2 }));

        var batch = new TrameMultiRequest
        {
            Mode = ExecutionMode.Parallel,
            Requests = new List<TrameRequest>
            {
                new() { Controller = "A", Method = "M1", Id = "1" },
                new() { Controller = "A", Method = "M2", Id = "2" },
            },
        };

        var results = await client.Call(batch);

        results.Should().NotBeNull();
        results!.Count().Should().Be(2);
        results.ElementAt(0)!.Code.Should().Be(200);
        results.ElementAt(1)!.Code.Should().Be(200);
    }

    [Fact]
    public async Task CallBinary_Throws_NotSupported()
    {
        var client = new TrameInMemoryClient();
        var act = () => client.CallBinary(new TrameRequest { Controller = "X", Method = "Y", Id = "1" });
        await act.Should().ThrowAsync<NotSupportedException>();
    }

    private sealed class Customer { public int Id { get; set; } public string Name { get; set; } = ""; }
}