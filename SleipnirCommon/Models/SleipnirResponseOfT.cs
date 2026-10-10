using System.Text.Json;
using System.Text.Json.Serialization;
using SleipnirCommon.Results;

namespace SleipnirCommon.Models;

/// <summary>
/// Typed envelope: carries the identical wire state as <see cref="SleipnirResponse"/> —
/// only the static factories and the <see cref="Value"/> accessor are typed. Use it when
/// a controller method wants business errors (<c>NotFound</c>/<c>Unauthorized</c>/…)
/// with a status code AND a typed payload: discovery and the generated clients see
/// the payload type <typeparamref name="T"/> (the generic envelope unwraps to it), while
/// the non-2xx branch keeps <c>code</c>+<c>error.message</c> on the wire.
/// </summary>
/// <remarks>
/// Derives from <see cref="SleipnirResponse"/> so the invoker pass-through
/// (<c>ReturnResponse</c>: <c>result is SleipnirResponse</c>) serves it verbatim — no
/// new invocation path. It deliberately carries <b>no</b> MessagePack/JSON metadata and
/// adds zero serialized members: every wire site (REST endpoint, WebSocket, SignalR hub)
/// serializes with the declared base type, so the existing response converter and the
/// manual MessagePack formatter handle derived instances unchanged. Never declare
/// <see cref="SleipnirResponse{T}"/> itself as a hub argument/return type.
///
/// The static factories mirror <see cref="SleipnirResults"/> but return
/// <see cref="SleipnirResponse{T}"/>. Unlike <see cref="SleipnirResults"/>, the generic
/// type has <b>no</b> raw-JSON <c>Ok(string)</c> or binary <c>Ok(byte[])</c> overload:
/// <c>Ok</c> always means "serialize the payload" (an author who pre-serializes keeps
/// <see cref="SleipnirResults.Ok(string)"/> on the non-generic envelope).
///
/// The non-generic <see cref="SleipnirResponse"/> itself stays opaque in discovery
/// (back-compat) — only the generic envelope unwraps.
/// </summary>
public sealed class SleipnirResponse<T> : SleipnirResponse
{
    /// <summary>
    /// The typed payload, deserialized from <see cref="SleipnirResponse.DataBytes"/>
    /// (bulk path) or the lazily materialized <see cref="SleipnirResponse.Data"/>
    /// (set-path), with the very same camelCase options the factories serialize with.
    /// Meaningful only when <see cref="SleipnirResponse.IsSuccess"/> is true — error
    /// envelopes (the <c>Error</c> factory family leaves Data null) and 204 / Ok(null)
    /// yield <c>default</c>.
    /// </summary>
    /// <remarks>
    /// <see cref="JsonIgnoreAttribute"/> is load-bearing: the REST/WS endpoints serialize
    /// the envelope via its RUNTIME type (minimal-API <c>Results.Ok(object)</c>), so the
    /// base write-only converter is not selected for the derived class — reflection walks
    /// it instead. Ignoring <c>Value</c> (and the base's transient members being already
    /// attributed) keeps that reflection output byte-identical to the non-generic
    /// <see cref="SleipnirResponse"/> wire shape.
    /// </remarks>
    [JsonIgnore]
    public T? Value
    {
        get
        {
            if (!IsSuccess) return default;
            var bytes = DataBytes;
            if (bytes is { Length: > 0 })
                return JsonSerializer.Deserialize<T>(bytes, SleipnirResults.CamelCaseJsonOptions);
            if (Data.HasValue)
                return Data.Value.Deserialize<T>(SleipnirResults.CamelCaseJsonOptions);
            return default;
        }
    }

    /// <summary>
    /// 200 OK with the typed payload. <c>null</c> yields 204 — identical wire image to
    /// <see cref="SleipnirResults.Ok(object?)"/>.
    /// </summary>
    public static SleipnirResponse<T> Ok(T? result)
    {
        if (result is null) return NoContent();
        return new SleipnirResponse<T>
        {
            Code = SleipnirErrorCodes.Ok,
            DataBytes = JsonSerializer.SerializeToUtf8Bytes(result, SleipnirResults.CamelCaseJsonOptions),
        };
    }

    /// <summary>204 No Content — the success case without a payload.</summary>
    public static SleipnirResponse<T> NoContent() => new() { Code = SleipnirErrorCodes.NoContent };

    /// <summary>
    /// Non-2xx error with the payload left null and a custom status code. The
    /// <see cref="SleipnirResults"/> conventions mirror in the named factories
    /// (<see cref="BadRequest"/>, <see cref="Unauthorized"/>, <see cref="NotFound"/>, …);
    /// this one keeps the arbitrary-code escape hatch.
    /// </summary>
    /// <remarks>
    /// Named <b>Fail</b> deliberately: a static <c>Error</c> would shadow the inherited
    /// <see cref="SleipnirResponse.Error"/> property (static member lookup shadows
    /// instance access on every envelope instance, e.g. <c>resp.Error.Message</c>).
    /// </remarks>
    public static SleipnirResponse<T> Fail(int code, string message,
        SleipnirErrorCategory category = SleipnirErrorCategory.None, string? details = null)
    {
        var envelope = new SleipnirResponse<T> { Code = code };
        envelope.AssignError(SleipnirResults.BuildError(code, message, category, details));
        return envelope;
    }

    /// <summary>400 Bad Request — invalid parameters / validation failure.</summary>
    public static SleipnirResponse<T> BadRequest(string message, string? details = null)
        => Fail(SleipnirErrorCodes.BadRequest, message, SleipnirErrorCategory.InvalidArgument, details);

    /// <summary>401 Unauthorized — authentication required/failed.</summary>
    public static SleipnirResponse<T> Unauthorized(string message = "Unauthorized.")
        => Fail(SleipnirErrorCodes.Unauthorized, message, SleipnirErrorCategory.Unauthenticated);

    /// <summary>403 Forbidden — authenticated but not permitted.</summary>
    public static SleipnirResponse<T> Forbidden(string message = "Forbidden.", string? details = null)
        => Fail(SleipnirErrorCodes.Forbidden, message, SleipnirErrorCategory.PermissionDenied, details);

    /// <summary>404 Not Found — resource/entity not found (business not-found).</summary>
    public static SleipnirResponse<T> NotFound(string message, string? details = null)
        => Fail(SleipnirErrorCodes.NotFound, message, SleipnirErrorCategory.NotFound, details);

    /// <summary>409 Conflict — conflict with the current state (e.g. duplicate).</summary>
    public static SleipnirResponse<T> Conflict(string message, string? details = null)
        => Fail(SleipnirErrorCodes.Conflict, message, SleipnirErrorCategory.Conflict, details);

    /// <summary>
    /// 500 Internal Server Error — only for controllers that deliberately signal an
    /// internal failure; otherwise throw (the invoker produces the generic 500).
    /// </summary>
    public static SleipnirResponse<T> InternalServerError(string message, string? details = null)
        => Fail(SleipnirErrorCodes.InternalServerError, message, SleipnirErrorCategory.Internal, details);

    // The Error property is inherited; assigning through a private instance helper keeps
    // the type-level lookup clean (the static family shadows nothing). Mirrors the base
    // semantics: the error envelope carries code + error only, Data stays null.
    private void AssignError(SleipnirError error) => ((SleipnirResponse)this).Error = error;
}