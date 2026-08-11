using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SleipnirWebSocket;

/// <summary>
/// Geteilte JSON-Serializer-Options für den WS-Transport (camelCase + relaxed Encoder,
/// wie der Invoker). Wird von der Middleware und dem Subscription-Manager genutzt.
/// </summary>
internal static class SleipnirJsonOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}