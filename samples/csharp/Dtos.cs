// ==============================================================================
// Client-seitige DTOs für die C#-Samples.
//
// Spiegel der server-seitigen Typen (samples/server/SampleServer.cs). Die
// Property-Namen sind PascalCase — der C#-Client deserialisiert mit
// PropertyNameCaseInsensitive=true, sodass camelCase-JSON vom Server ohne
// weitere Attribute bindet. Kein [TrameDataContract] nötig (Client-seite sind
// reine Deserialisierungs-POCOs, keine Discovery-Verträge).
// ==============================================================================

using System.Text.Json;

namespace Trame.Samples.CSharp;

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

/// <summary>
/// Geteilte JSON-Optionen für die manuelle Deserialisierung von
/// <c>TrameResponse.Data</c> (JsonElement) in den Batch-Szenarien.
/// CaseInsensitive, weil der Server camelCase serialisiert (siehe Discovery).
/// </summary>
public static class SampleJson
{
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNameCaseInsensitive = true,
    };
}