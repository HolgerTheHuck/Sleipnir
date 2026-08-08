using FluentAssertions;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TrameTests.Fixtures;
using Xunit;

namespace TrameTests.Integration;

/// <summary>
/// R3 server-side regression: WebSocket error frames must be real <see cref="TrameCommon.Models.TrameResponse"/>
/// objects (Code + <see cref="TrameCommon.Models.TrameError"/> + Id) so a client can surface the
/// message. The pre-fix frames were anonymous <c>{ code, data }</c> with no <c>id</c> and no
/// <c>error</c> envelope, so a C# client's strict dispatcher dropped them (hang) and the message
/// hid in <c>data</c> where <c>TrameException</c> never looks.
/// </summary>
public class WebSocketCorrelationTests : IClassFixture<TransportTestFixture>
{
    private readonly TransportTestFixture _fixture;

    public WebSocketCorrelationTests(TransportTestFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<JsonElement> SendRawAndReceiveAsync(string json)
    {
        using var ws = new ClientWebSocket();
        var wsBase = _fixture.BaseUrl.Replace("http://", "ws://").Replace("https://", "wss://");
        await ws.ConnectAsync(new Uri(wsBase + "tramews"), CancellationToken.None);

        var bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);

        var buffer = new byte[8192];
        var received = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
        var text = Encoding.UTF8.GetString(buffer, 0, received.Count);

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task MalformedJson_ReturnsStructuredErrorFrame_NotAnonymousDataEnvelope()
    {
        // A malformed request cannot be parsed, so no correlation id is available. The server
        // must still reply with a structured frame (code + error.message + id), not the pre-fix
        // anonymous { code, data } that a client never surfaced as a TrameException.
        var root = await SendRawAndReceiveAsync("{ this is not valid json }");

        root.GetProperty("code").GetInt32().Should().Be(400);
        root.GetProperty("error").GetProperty("message").GetString().Should().NotBeNullOrEmpty();
        // Pre-fix the message hid in `data` as a string; post-fix it travels in error.message
        // and `data` is null (TrameException reads Error.Message, never data).
        root.GetProperty("data").ValueKind.Should().Be(JsonValueKind.Null,
            "the error message must travel in error.message, not in data");
        // An id field is present (empty for an unparseable request — the best-effort contract).
        root.TryGetProperty("id", out _).Should().BeTrue("every error frame now carries an id field");
    }
}