using System.Text.Json;
using System.Threading;
// TrameException is now provided via global using alias from GlobalUsings.cs

namespace TrameClient.Trame
{
    public abstract class TrameClientBase(JsonSerializerOptions? options = null) : ITrameClient
    {
        /// <summary>
        /// Gemeinsame JSON-Serializer-Optionen, die von allen Clients genutzt werden.
        /// </summary>
        protected readonly JsonSerializerOptions JsonOptions = options ?? new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = false
        };

        /// <summary>
        /// Sendet einen einzelnen TrameRequest und liefert eine TrameResponse zurück.
        /// Die konkrete Umsetzung übernimmt die abgeleitete Client-Klasse (z. B. REST oder SignalR).
        /// </summary>
        public abstract Task<TrameResponse?> Call(TrameRequest? request, CancellationToken ct = default);

        /// <summary>
        /// Sendet einen TrameRequest und versucht, das Feld Data in den angegebenen Typ T zu deserialisieren.
        /// Wirft eine TrameException, wenn ein Fehler (z. B. Netzwerkfehler oder Deserialisierungsfehler) auftritt.
        /// </summary>
        public async Task<T?> Call<T>(TrameRequest? request, CancellationToken ct = default)
        {
            if (request == null)
                return default;

            var response = await Call(request, ct);
            if (response is { IsSuccess: true })
            {
                // Single-Pass Fast-Path: DataBytes (rohe UTF-8-Bytes aus dem Transport-
                // Parser) direkt in T deserialisieren — ein Pass, kein JsonElement-Baum.
                if (response.DataBytes is { } dataBytes && dataBytes.Length > 0)
                {
                    try { return JsonSerializer.Deserialize<T>(dataBytes, JsonOptions); }
                    catch (Exception ex)
                    {
                        throw new TrameException("Failed to deserialize the response.", ex);
                    }
                }
                // Fallback: Data als JsonElement (SignalR-Client via MessagePack-Formatter,
                // Legacy-Server ohne Converter, oder leerer Erfolg ohne Ergebniswert).
                if (response.Data.HasValue)
                {
                    try { return response.Data.Value.Deserialize<T>(JsonOptions); }
                    catch (Exception ex)
                    {
                        throw new TrameException("Failed to deserialize the response.", ex);
                    }
                }
                return default;
            }
            if (response != null && !response.IsSuccess)
            {
                var error = response.Error ?? TrameError.FromResponse(response);
                throw new TrameException(error, null);
            }
            return default;
        }

        /// <summary>
        /// Sendet einen TrameRequest und liefert das binäre <c>Content</c>-Feld der
        /// Antwort (z. B. für Methoden, die <c>byte[]</c> zurückgeben). Wirft eine
        /// <see cref="TrameException"/> bei nicht-erfolgreichem Call.
        /// </summary>
        public async Task<byte[]?> CallBinary(TrameRequest? request, CancellationToken ct = default)
        {
            if (request == null)
                return default;

            var response = await Call(request, ct);
            if (response is { IsSuccess: true })
                return response.Content;

            if (response != null && !response.IsSuccess)
            {
                var error = response.Error ?? TrameError.FromResponse(response);
                throw new TrameException(error, null);
            }
            return default;
        }

        /// <summary>
        /// Sendet mehrere TrameRequests (MultiCall) und liefert eine Liste von TrameResponses zurück.
        /// Die konkrete Umsetzung übernimmt die abgeleitete Client-Klasse.
        /// </summary>
        public abstract Task<IEnumerable<TrameResponse?>?> Call(TrameMultiRequest? request, CancellationToken ct = default);
    }
}
