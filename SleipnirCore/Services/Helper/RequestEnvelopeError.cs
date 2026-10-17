using System.Text.Json;

namespace SleipnirCore.Services.Helper;

/// <summary>
/// The one wording for a request envelope that failed to deserialize, shared by the REST and
/// WebSocket transports. Names the JSON path and the position, so a hand-written request
/// (<c>{"id":1,…}</c> where <c>id</c> is a string) gets an actionable 400 instead of the
/// framework's bare 400 (Production) or exception page (Development).
/// </summary>
internal static class RequestEnvelopeError
{
    /// <summary>
    /// e.g. for <c>{"id":1,…}</c>: <c>Invalid request envelope at '$.id' (line 1, byte 7): The JSON value could not be
    /// converted to System.String.</c> — the System.Text.Json reason without its trailing
    /// " Path: … | LineNumber: …" suffix, which is restated in front.
    /// </summary>
    internal static string Describe(JsonException ex)
    {
        var reason = ex.Message;
        var cut = reason.IndexOf(" Path: ", StringComparison.Ordinal);
        if (cut >= 0)
            reason = reason[..cut];

        var where = string.IsNullOrEmpty(ex.Path) ? "" : $" at '{ex.Path}'";
        if (ex.LineNumber is { } line && ex.BytePositionInLine is { } bytePos)
            where += $" (line {line + 1}, byte {bytePos})";

        return $"Invalid request envelope{where}: {reason}";
    }
}
