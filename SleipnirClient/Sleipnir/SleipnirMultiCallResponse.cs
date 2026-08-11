using System.Text.Json;

namespace SleipnirClient.Sleipnir
{
    public class SleipnirMultiCallResponse
    {
        // Einmalige, wiederverwendete Options statt pro Instanz (D3).
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        public ExecutionMode Mode { get; set; } = ExecutionMode.Serial;

        public List<SleipnirResponse?>? Responses { get; set; }

        public static async Task<SleipnirMultiCallResponse> Call(ISleipnirClient client, SleipnirMultiRequest request)
        {
            var response = await client.Call(request);
            return new SleipnirMultiCallResponse()
            {
                Responses = response?.ToList()
            };
        }

        /// <summary>
        /// Liefert das Ergebnis des per <paramref name="name"/> (Request-Id) benannten
        /// Sub-Calls deserialisiert in <typeparamref name="T"/>. Liefert <c>default</c>
        /// bei nicht-erfolgreichem Sub-Call oder fehlendem <c>Data</c>; siehe
        /// <see cref="GetError"/> für die Fehlerdetails.
        /// </summary>
        public T? Get<T>(string name)
        {
            var theResult = Responses?.FirstOrDefault(s => s?.Id == name);
            if (theResult is not { IsSuccess: true })
                return default;

            // Single-Pass Fast-Path: DataBytes direkt in T (kein JsonElement-Baum),
            // Fallback über Data wie in SleipnirClientBase.Call<T>.
            if (theResult.DataBytes is { } dataBytes && dataBytes.Length > 0)
                return JsonSerializer.Deserialize<T>(dataBytes, JsonOptions);

            if (!theResult.Data.HasValue)
                return default;

            return theResult.Data.Value.Deserialize<T>(JsonOptions);
        }

        /// <summary>
        /// Liefert die strukturierten Fehlerdetails des per <paramref name="name"/>
        /// benannten Sub-Calls oder <c>null</c>, wenn der Sub-Call erfolgreich war
        /// oder nicht gefunden wurde.
        /// </summary>
        public SleipnirError? GetError(string name)
        {
            var theResult = Responses?.FirstOrDefault(s => s?.Id == name);
            if (theResult is null or { IsSuccess: true })
                return null;

            return theResult.Error ?? SleipnirError.FromResponse(theResult);
        }
    }
}