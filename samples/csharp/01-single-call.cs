// ==============================================================================
// 01 — Single Call (C#-Client)
// ==============================================================================
// Ein einzelner RPC-Aufruf. Gezeigt für REST und WebSocket — wähle je nach
// Anforderung. WebSocket ist der empfohlene primäre Kanal (persistent, geringe
// Latenz); REST ist zustandslos und am einfachsten.
// ==============================================================================

using System.Text.Json;
using SleipnirClient.Sleipnir;
using SleipnirCommon.Models;

namespace Sleipnir.Samples.CSharp;

public static class SingleCallScenario
{
    public static async Task RunAsync(SleipnirRestJsonClient rest, TextWriter w)
    {
        // --- REST: Kunden anlegen (skalarer int kommt zurück) ----------------------
        var addReq = SleipnirCall.Init("Customer", "AddCustomer")
            .Param("name", "Alice")          // named parameter — server bindet nach Name
            .Param("email", "alice@x.com")
            .ToRequest();

        int newId = await rest.Call<int>(addReq);
        await w.WriteLineAsync($"  [REST]    AddCustomer  -> new id = {newId}");

        // --- REST: Kunden laden (Call<T> deserialisiert direkt nach Customer) ------
        var getReq = SleipnirCall.Init("Customer", "GetCustomerById")
            .Param("id", newId)
            .ToRequest();

        Customer? customer = await rest.Call<Customer>(getReq);
        await w.WriteLineAsync($"  [REST]    GetCustomerById({newId}) -> {customer?.Name} <{customer?.Email}>");

        // --- REST: alle Kunden (Liste) ---------------------------------------------
        var allReq = SleipnirCall.Init("Customer", "GetAllCustomers").ToRequest();
        var all = await rest.Call<List<Customer>>(allReq);
        await w.WriteLineAsync($"  [REST]    GetAllCustomers -> {all?.Count ?? 0} customer(s)");

        // --- WebSocket: derselbe Aufruf über den persistenten Kanal ----------------
        // IAsyncDisposable. Der C#-WS-Client nimmt die Basis-URL (https://…) + einen
        // separaten wsPath (Default "sleipnirws") und hebt intern auf wss://…/sleipnirws ab.
        await using var ws = new SleipnirWebSocketClient("https://localhost:5001");
        await ws.ConnectAsync();

        // With(params object?[]) = positionsbasiert (Namen param0, param1, …). Der
        // Server bindet positional, wenn der Name nicht passt — für Ein-Parameter-
        // Methoden ok.
        var wsReq = SleipnirCall.Init("Customer", "GetCustomerById")
            .With(newId)
            .ToRequest();

        Customer? customer2 = await ws.Call<Customer>(wsReq);
        await w.WriteLineAsync($"  [WebSocket] GetCustomerById({newId}) -> {customer2?.Name} <{customer2?.Email}>");

        // --- Raw-Form (ohne Fluent Builder) — gelegentlich nützlich ----------------
        // Params = nativer JSON-Wert: JsonArray aus { parameterName, data }-Einträgen,
        // data ist der JSON-Token selbst (Zahl 42 → 42, String "A" → "A").
        var raw = new SleipnirRequest
        {
            Controller = "Customer",
            Method = "GetAllCustomers",
            Id = "Customer.GetAllCustomers",
        };
        var rawList = await rest.Call<List<Customer>>(raw);
        await w.WriteLineAsync($"  [REST raw] GetAllCustomers -> {rawList?.Count ?? 0} customer(s)");
    }
}