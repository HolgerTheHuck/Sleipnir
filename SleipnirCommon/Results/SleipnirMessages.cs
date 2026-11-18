namespace SleipnirCommon.Results;

/// <summary>
/// The texts of the error messages Sleipnir generates itself for authentication and
/// authorization (<see cref="SleipnirErrorCategory.Unauthenticated"/> /
/// <see cref="SleipnirErrorCategory.PermissionDenied"/>), so an application can return them in
/// its own language. Set via <c>SleipnirOptions.Messages</c>.
/// </summary>
/// <remarks>
/// <para>
/// Only <c>error.message</c> changes — <c>code</c> (401/403) and <c>category</c> stay fixed, so
/// clients that branch on the category keep working. The defaults are the 1.x texts
/// (<c>"Unauthorized."</c> / <c>"Forbidden."</c>).
/// </para>
/// <para>
/// <b>Fixed texts:</b> assign the properties
/// (<c>options.Messages.Unauthenticated = "Bitte anmelden.";</c>).
/// <b>Per-request culture:</b> derive from this class and override the getters, e.g. with an
/// <c>IStringLocalizer</c> — they are read on every rejected call, on the request's execution
/// context, so <c>CultureInfo.CurrentUICulture</c> is the culture the request-localization
/// middleware set (REST, SSE). For WebSocket and SignalR it is the culture of the connection's
/// upgrade request. A getter must not throw; a <c>null</c>/empty result falls back to the default.
/// </para>
/// </remarks>
public class SleipnirMessages
{
    /// <summary>Default text for <see cref="SleipnirErrorCategory.Unauthenticated"/> (401).</summary>
    public const string DefaultUnauthenticated = "Unauthorized.";

    /// <summary>Default text for <see cref="SleipnirErrorCategory.PermissionDenied"/> (403).</summary>
    public const string DefaultPermissionDenied = "Forbidden.";

    /// <summary>Message of the framework-generated 401 (caller not authenticated).</summary>
    public virtual string Unauthenticated { get; set; } = DefaultUnauthenticated;

    /// <summary>Message of the framework-generated 403 (authenticated, role/policy denied).</summary>
    public virtual string PermissionDenied { get; set; } = DefaultPermissionDenied;

    /// <summary>Resolves the 401 text, falling back to the default when unset or empty.</summary>
    public static string ResolveUnauthenticated(SleipnirMessages? messages)
        => NonEmpty(messages?.Unauthenticated, DefaultUnauthenticated);

    /// <summary>Resolves the 403 text, falling back to the default when unset or empty.</summary>
    public static string ResolvePermissionDenied(SleipnirMessages? messages)
        => NonEmpty(messages?.PermissionDenied, DefaultPermissionDenied);

    private static string NonEmpty(string? value, string fallback)
        => string.IsNullOrEmpty(value) ? fallback : value;
}
