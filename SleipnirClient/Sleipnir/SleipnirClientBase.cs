using System.Text.Json;
using System.Threading;
// SleipnirException is now provided via global using alias from GlobalUsings.cs

namespace SleipnirClient.Sleipnir
{
    public abstract class SleipnirClientBase(JsonSerializerOptions? options = null) : ISleipnirClient
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
        /// Sendet einen einzelnen SleipnirRequest und liefert eine SleipnirResponse zurück.
        /// Die konkrete Umsetzung übernimmt die abgeleitete Client-Klasse (z. B. REST oder SignalR).
        /// </summary>
        public abstract Task<SleipnirResponse?> Call(SleipnirRequest? request, CancellationToken ct = default);

        /// <summary>
        /// Sendet einen SleipnirRequest und versucht, das Feld Data in den angegebenen Typ T zu deserialisieren.
        /// Wirft eine SleipnirException, wenn ein Fehler (z. B. Netzwerkfehler oder Deserialisierungsfehler) auftritt.
        /// </summary>
        public async Task<T?> Call<T>(SleipnirRequest? request, CancellationToken ct = default)
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
                        throw new SleipnirException("Failed to deserialize the response.", ex);
                    }
                }
                // Fallback: Data als JsonElement (SignalR-Client via MessagePack-Formatter,
                // Legacy-Server ohne Converter, oder leerer Erfolg ohne Ergebniswert).
                if (response.Data.HasValue)
                {
                    try { return response.Data.Value.Deserialize<T>(JsonOptions); }
                    catch (Exception ex)
                    {
                        throw new SleipnirException("Failed to deserialize the response.", ex);
                    }
                }
                return default;
            }
            if (response != null && !response.IsSuccess)
            {
                var error = response.Error ?? SleipnirError.FromResponse(response);
                throw new SleipnirException(error, null);
            }
            return default;
        }

        /// <summary>
        /// Sendet einen SleipnirRequest und liefert das binäre <c>Content</c>-Feld der
        /// Antwort (z. B. für Methoden, die <c>byte[]</c> zurückgeben). Wirft eine
        /// <see cref="SleipnirException"/> bei nicht-erfolgreichem Call.
        /// </summary>
        public async Task<byte[]?> CallBinary(SleipnirRequest? request, CancellationToken ct = default)
        {
            if (request == null)
                return default;

            var response = await Call(request, ct);
            if (response is { IsSuccess: true })
                return response.Content;

            if (response != null && !response.IsSuccess)
            {
                var error = response.Error ?? SleipnirError.FromResponse(response);
                throw new SleipnirException(error, null);
            }
            return default;
        }

        /// <summary>
        /// Sendet mehrere SleipnirRequests (MultiCall) und liefert eine Liste von SleipnirResponses zurück.
        /// Die konkrete Umsetzung übernimmt die abgeleitete Client-Klasse.
        /// </summary>
        public abstract Task<IEnumerable<SleipnirResponse?>?> Call(SleipnirMultiRequest? request, CancellationToken ct = default);

        /// <summary>
        /// Default: this backend is calls-only (REST) and cannot subscribe to events.
        /// Overridden by event-capable backends (WebSocket / SSE / SignalR) and the
        /// <see cref="SleipnirTransportRouter"/>. Throws <see cref="NotImplementedException"/>.
        /// </summary>
        public virtual Task<SleipnirSubscription<T>> SubscribeAsync<T>(SleipnirRequest? request, ResumePolicy? resumePolicy = null, CancellationToken ct = default)
            => throw new NotSupportedException(
                "This client backend does not support event subscriptions. " +
                "Use SleipnirTransportRouter or an event-capable backend (WebSocket / SSE / SignalR).");

        /// <summary>
        /// Default: this backend cannot resume a subscription (e.g. WebSocket needs the
        /// original controller/method and is not resumable by id alone). Overridden by
        /// resume-capable backends (SSE / SignalR) and the <see cref="SleipnirTransportRouter"/>.
        /// </summary>
        public virtual Task<SleipnirSubscription<T>> ResumeAsync<T>(string subscriptionId, long lastEventId, ResumePolicy? resumePolicy = null, CancellationToken ct = default)
            => throw new NotSupportedException(
                "This client backend does not support resuming a subscription by id. " +
                "Use an SSE or SignalR backend, or the SleipnirTransportRouter.");
    }
}
