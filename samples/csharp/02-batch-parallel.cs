// ==============================================================================
// 02 — Batch Parallel (C#-Client)
// ==============================================================================
// Mehrere UNABHÄNGIGE Aufrufe in einer einzigen Roundtrip. Der Server führt sie
// per Task.WhenAll concurrently aus (ExecutionMode.Parallel). Ideal, um Latenz
// zu amortisieren, wenn Calls einander nicht brauchen.
//
// Wichtig: Parallel löst KEINE @alias-Abhängigkeiten auf — dafür siehe 03/04.
// ==============================================================================

using System.Text.Json;
using SleipnirClient.Sleipnir;
using SleipnirCommon.Models;

namespace Sleipnir.Samples.CSharp;

public static class BatchParallelScenario
{
    public static async Task RunAsync(SleipnirRestJsonClient rest, TextWriter w)
    {
        // Erst einen Kunden garantieren, damit GetCustomerById Treffer hat.
        await rest.Call<int>(SleipnirCall.Init("Customer", "AddCustomer")
            .Param("name", "Bob")
            .Param("email", "bob@x.com")
            .ToRequest());

        var multi = new SleipnirMultiRequest
        {
            Mode = ExecutionMode.Parallel,        // Task.WhenAll über alle Requests
            Requests = new List<SleipnirRequest>
            {
                // .Named(id) setzt die Korrelations-Id — wichtig, um Responses
                // zuzuordnen (insbesondere bei WebSocket, wo Batch-Responses an
                // requests[0].id korrelieren — eindeutige Ids verhindern Kollisionen).
                SleipnirCall.Init("Customer", "GetAllCustomers").Named("all").ToRequest(),
                SleipnirCall.Init("Customer", "GetCustomerById").With(1).Named("c1").ToRequest(),
                SleipnirCall.Init("Customer", "GetCustomerById").With(2).Named("c2").ToRequest(),
            },
        };

        // Call(SleipnirMultiRequest) liefert die Responses in Request-Reihenfolge.
        var responses = (await rest.Call(multi))!.ToList();

        var all = responses[0];   // GetAllCustomers → IReadOnlyList<Customer>
        var c1   = responses[1];  // GetCustomerById(1)
        var c2   = responses[2];  // GetCustomerById(2)

        // Bei Call(SleipnirMultiRequest) bleiben die Ergebnisse als SleipnirResponse (mit
        // .Data als JsonElement). Wer Typsicherheit pro Response will, deserialisiert
        // individuell (Single-Pass, kein doppeltes JSON.parse):
        var list = all.Data?.Deserialize<List<Customer>>(SampleJson.Default);
        var cust1 = c1.Data?.Deserialize<Customer>(SampleJson.Default);
        var cust2 = c2.Data?.Deserialize<Customer>(SampleJson.Default);

        await w.WriteLineAsync($"  [all]    GetAllCustomers -> {list?.Count ?? 0} customer(s)");
        await w.WriteLineAsync($"  [c1]     GetCustomerById(1) -> {cust1?.Name ?? "<none>"} (code {c1.Code})");
        await w.WriteLineAsync($"  [c2]     GetCustomerById(2) -> {cust2?.Name ?? "<none>"} (code {c2.Code})");
    }
}