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
//   • Request B nutzt "@alias" als Parameterwert (data-Wert mit @-Präfix).
//     Der Server ersetzt es vor der Ausführung.
//   • Mode = Serial (sobald ein DependencyMapping existiert, schaltet der Server
//     ohnehin auf topologische Batch-Ausführung — Mode wird dann ignoriert).
// ==============================================================================

using System.Text.Json;
using SleipnirClient.Sleipnir;
using SleipnirCommon.Models;

namespace Sleipnir.Samples.CSharp;

public static class DependencyChainScenario
{
    public static async Task RunAsync(SleipnirRestJsonClient rest, TextWriter w)
    {
        // -----------------------------------------------------------------------------
        // Variante A — Fluent Builder (einfach, 2-Step, Ein-Parameter-@alias)
        // -----------------------------------------------------------------------------
        // AddCustomer → liefert neue Id (int) → weiter als @newId an GetCustomerById.
        var multi = new SleipnirMultiRequest
        {
            Mode = ExecutionMode.Serial,
            Requests = new List<SleipnirRequest>
            {
                SleipnirCall.Init("Customer", "AddCustomer")
                    .Named("step1")
                    .Param("name", "Carol")
                    .Param("email", "carol@x.com")
                    .Exposes("$", "newId")          // ganzer int-Rückgabewert → Alias "newId"
                    .ToRequest(),

                SleipnirCall.Init("Customer", "GetCustomerById")
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
        var chain = new SleipnirMultiRequest
        {
            Mode = ExecutionMode.Serial,
            Requests = new List<SleipnirRequest>
            {
                // step1: Kunden anlegen, neue Id als "custId" weitergeben.
                // Params ist ein JsonArray von { parameterName, data }-Einträgen;
                // data ist ein NATIVER JSON-Wert (keine Doppelkodierung mehr).
                new SleipnirRequest
                {
                    Controller = "Customer", Method = "AddCustomer", Id = "step1",
                    Params = JsonSerializer.SerializeToNode(new object?[]
                    {
                        new { parameterName = "name",  data = "Dave" },
                        new { parameterName = "email", data = "dave@x.com" },
                    }),
                    DependencyMapping = new Dictionary<string, string> { ["custId"] = "$" },
                },

                // step2: Bestellung für diesen Kunden anlegen; customerId kommt von
                // @custId, total ist ein Literal. OrderId als "orderId" weitergeben.
                new SleipnirRequest
                {
                    Controller = "Order", Method = "CreateOrder", Id = "step2",
                    Params = JsonSerializer.SerializeToNode(new object?[]
                    {
                        // data="@custId" → Server erkennt das @-Präfix und substituiert.
                        new { parameterName = "customerId", data = "@custId" },
                        new { parameterName = "total",      data = 99.90m },
                    }),
                    DependencyMapping = new Dictionary<string, string> { ["orderId"] = "$" },
                },

                // step3: Bestellung anhand der weitergegebenen OrderId laden.
                new SleipnirRequest
                {
                    Controller = "Order", Method = "GetOrderById", Id = "step3",
                    Params = JsonSerializer.SerializeToNode(new object?[]
                    {
                        new { parameterName = "id", data = "@orderId" },
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