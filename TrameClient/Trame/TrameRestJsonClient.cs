using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TrameClient.Trame
{
    public class TrameRestJsonClient : TrameClientBase, ITrameClient, IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly string _serverBase;
        private readonly string _apiPath;
        private readonly bool _ownsHttpClient;

        public TrameRestJsonClient(string serverBaseUrl, HttpClient? httpClient = null, string apiPath = "api/trame",
            TimeSpan? httpClientTimeout = null)
        {
            if (string.IsNullOrWhiteSpace(serverBaseUrl))
                throw new ArgumentException("Server-URL darf nicht leer sein.", nameof(serverBaseUrl));

            if (httpClient != null)
            {
                _httpClient = httpClient;
                _ownsHttpClient = false;
            }
            else
            {
                _httpClient = new HttpClient(
                    new SocketsHttpHandler
                    {
                        PooledConnectionLifetime = TimeSpan.FromMinutes(2)
                    },
                    disposeHandler: true);
                _ownsHttpClient = true;
            }

            if (httpClientTimeout.HasValue)
                _httpClient.Timeout = httpClientTimeout.Value;

            _serverBase = serverBaseUrl.EndsWith("/", StringComparison.Ordinal) ? serverBaseUrl : serverBaseUrl + "/";
            _apiPath = apiPath.Trim('/');
        }

        public override async Task<TrameResponse?> Call(TrameRequest? request, CancellationToken ct = default)
        {
            if (request == null)
            {
                return null;
            }

            var jsonData = JsonSerializer.Serialize(request, JsonOptions);
            var content = new StringContent(jsonData, Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync($"{_serverBase}{_apiPath}/json", content, ct);

            // Bytes statt String lesen — TrameResponseParser macht EINEN Pass über die
            // Wire-Bytes (ID, Envelope, T zusammen) statt drei Parses mit JsonDocument-Baum.
            var responseBytes = await response.Content.ReadAsByteArrayAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                var errorText = Encoding.UTF8.GetString(responseBytes);
                return new TrameResponse
                {
                    Code = (int)response.StatusCode,
                    Id = request.Id,
                    Error = new TrameError
                    {
                        Code = (int)response.StatusCode,
                        Message = $"HTTP Error: {response.StatusCode}",
                        Details = errorText
                    }
                };
            }
            return TrameResponseParser.Parse(responseBytes);
        }

        public override async Task<IEnumerable<TrameResponse?>?> Call(TrameMultiRequest? request, CancellationToken ct = default)
        {
            if (request == null || request.Requests == null)
                return null;

            foreach (var requestRequest in request.Requests)
            {
                if (string.IsNullOrEmpty(requestRequest.Id))
                {
                    requestRequest.Id = $"{requestRequest.Controller}.{requestRequest.Method}";
                }
            }

            var jsonData = JsonSerializer.Serialize(request, JsonOptions);
            var content = new StringContent(jsonData, Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync($"{_serverBase}{_apiPath}/json/multi", content, ct);
            var responseBytes = await response.Content.ReadAsByteArrayAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                var errorText = Encoding.UTF8.GetString(responseBytes);
                return new List<TrameResponse>
                {
                    new TrameResponse
                    {
                        Code = (int)response.StatusCode,
                        Id = request.Requests?.FirstOrDefault()?.Id,
                        Error = new TrameError
                        {
                            Code = (int)response.StatusCode,
                            Message = $"HTTP Error: {response.StatusCode}",
                            Details = errorText
                        }
                    }
                };
            }
            // Batch = JSON-Array → ParseArray (ein Pass, DataBytes pro Element).
            var trameResponses = TrameResponseParser.ParseArray(responseBytes);
            return trameResponses ?? new List<TrameResponse?>();
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing && _ownsHttpClient)
            {
                _httpClient?.Dispose();
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}
