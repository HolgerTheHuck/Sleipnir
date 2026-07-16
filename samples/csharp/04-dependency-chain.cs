// ==============================================================================
// 04 — Dependency Chaining (C#-Client)
// ==============================================================================
// Mehrere Aufrufe in EINER Roundtrip, wobei spätere Aufrufe Werte aus früheren
// nutzen — ohne Client-seitiges Zusammenfügen.
//
// Mechanik:
//   • Request A deklariert DependencyMapping: { "alias" → "$.JsonPath" }.
//     Der JsonPath ist ergebnisrelativ: "$" = der gesamte serialisierte
//     Rückgabewert; "$.Id" = Eigenschaft; "$[0].Id" = erstes Listenelement.
//     KEIN "$.data"-Envelope.
//   • Der Server extrahiert den Wert und speichert ihn in ExposedDependencies.
//   • Request B nutzt "@alias" als Parameterwert (Data-String mit @-Präfix).
//     Der Server ersetzt es vor der Ausführung.
//   • Mode = Serial (sobald ein DependencyMapping existiert, schaltet der Server
//     ohnehin auf topologische Batch-Ausführung — Mode wird dann ignoriert).
// ==============================================================================

using System.Text.Json;
using TrameClient.Trame;
using TrameCommon.Models;

namespace Trame.Samples.CSharp;

public static class DependencyChainScenario
{
    public static async Task RunAsync(TrameRestJsonClient rest, TextWriter w)
    {
        // -----------------------------------------------------------------------------
        // Variante A — Fluent Builder (einfach, 2-Step, Ein-Parameter-@alias)
        // -----------------------------------------------------------------------------
        // AddCustomer → liefert neue Id (int) → weiter als @newId an GetCustomerById.
        var multi = new TrameMultiRequest
        {
            Mode = ExecutionMode.Serial,
            Requests = new List<TrameRequest>
            {
                TrameCall.Init("Customer", "AddCustomer")
                    .Named("step1")
                    .Param("name", "Carol")
                    .Param("email", "carol@x.com")
                    .Exposes("$", "newId")          // ganzer int-Rückgabewert → Alias "newId"
                    .ToRequest(),

                TrameCall.Init("Customer", "GetCustomerById")
                    .Named("step2")
                    .WithAlias("@newId")            // Data="@newId"; ParameterName="newId"
                    .ToRequest(),
                // Hinweis: WithAlias setzt ParameterName auf den Alias-Namen ("newId").
                // Da GetCustomerById nur EINEN echten Parameter ("id") hat, bindet der
                // Server positional (Num=0) → klappt. Bei MEHRPARAMETRIGEN Methoden mit
                // @alias siehe Variante B (raw, ParameterName = echter Parametername).
            },
        };

        var responses = (await rest.Call(multi))!.ToList();
        var newId = responses[0].Data?.Deserialize<int>(SampleJson.Default);
        var chainedCustomer = responses[1].Data?.Deserialize<Customer>(SampleJson.Default);
        await w.WriteLineAsync($"  [A] AddCustomer -> Id {newId}; GetCustomerById(@newId) -> {chainedCustomer?.Name}");

        // -----------------------------------------------------------------------------
        // Variante B — Raw-Form (robust, 3-Step, mehrparametrige @alias-Bindung)
        // -----------------------------------------------------------------------------
        // AddCustomer → @custId → CreateOrder(customerId=@custId, total=99.90) → @orderId
        // → GetOrder(@orderId). CreateOrder hat ZWEI Parameter, davon einer @alias —
        // deshalb setzen wir ParameterName auf den echten Parameternamen ("customerId"),
        // damit der Server nach Name bindet (sicherer als positional).
        var chain = new TrameMultiRequest
        {
            Mode = ExecutionMode.Serial,
            Requests = new List<TrameRequest>
            {
                // step1: Kunden anlegen, neue Id als "custId" weitergeben.
                new TrameRequest
                {
                    Controller = "Customer", Method = "AddCustomer", Id = "step1",
                    StringData = JsonSerializer.Serialize(new[]
                    {
                        new TrameParameter { Num = 0, ParameterName = "name",  Data = JsonSerializer.Serialize("Dave") },
                        new TrameParameter { Num = 1, ParameterName = "email", Data = JsonSerializer.Serialize("dave@x.com") },
                    }),
                    DependencyMapping = new Dictionary<string, string> { ["custId"] = "$" },
                },

                // step2: Bestellung für diesen Kunden anlegen; customerId kommt von
                // @custId, total ist ein Literal. OrderId als "orderId" weitergeben.
                new TrameRequest
                {
                    Controller = "Order", Method = "CreateOrder", Id = "step2",
                    StringData = JsonSerializer.Serialize(new[]
                    {
                        // Data="@custId" → Server erkennt @-Präfix und substituiert.
                        new TrameParameter { Num = 0, ParameterName = "customerId", Data = "@custId" },
                        new TrameParameter { Num = 1, ParameterName = "total",      Data = JsonSerializer.Serialize(99.90m) },
                    }),
                    DependencyMapping = new Dictionary<string, string> { ["orderId"] = "$" },
                },

                // step3: Bestellung anhand der weitergegebenen OrderId laden.
                new TrameRequest
                {
                    Controller = "Order", Method = "GetOrderById", Id = "step3",
                    StringData = JsonSerializer.Serialize(new[]
                    {
                        new TrameParameter { Num = 0, ParameterName = "id", Data = "@orderId" },
                    }),
                },
            },
        };

        var chainResponses = (await rest.Call(chain))!.ToList();
        var custId = chainResponses[0].Data?.Deserialize<int>(SampleJson.Default);
        var orderId = chainResponses[1].Data?.Deserialize<int>(SampleJson.Default);
        var loadedOrder = chainResponses[2].Data?.Deserialize<Order>(SampleJson.Default);
        await w.WriteLineAsync($"  [B] custId={custId}, orderId={orderId}; GetOrderById(@orderId) -> Total={loadedOrder?.Total}");

        // -----------------------------------------------------------------------------
        // Gotchas
        // -----------------------------------------------------------------------------
        // • JsonPath ist ergebnisrelativ (kein $.data-Envelope): "$", "$.Id", "$[0].Id".
        // • Ein UNAUFGELÖSTES @alias → Server antwortet 400 "Unresolved dependencies".
        //   Stellen sicher, dass jeder @alias VOR seiner Nutzung deklariert ist
        //   (DependencyMapping eines früheren Requests in derselben Batch).
        // • Zirkuläre Abhängigkeiten → 400 für ALLE Requests der Batch.
    }
}