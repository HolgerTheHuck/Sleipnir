using System.Text.Json.Serialization;
using Sleipnir.Generated;
using SleipnirClient.Sleipnir;

namespace Sleipnir.Guide.Admin.Services;

// The admin's server-side auth session (chapter 8 — the chapter-7 numbering moved for the
// LINQ chapter). Blazor Server keeps the admin's bearer HERE, on the server, never in the
// browser — that is the Pflege-Backend's whole point: the admin token never crosses to the
// client. (A Blazor WASM variant would instead hold the bearer in the browser and require
// CORS on the API; the README notes that trade.)
//
// The generated SleipnirGeneratedClient is a singleton that wraps a SleipnirTransportRouter.
// After login we cast its ISleipnirClient to the router and call SetBearer(token); the
// server-side C# WS + REST clients CAN set the Authorization header (unlike a browser WS), so
// the admin keeps the `auto` profile (WS probed first) even for authed calls — the contrast
// the chapter draws with the browser portal, which must pin REST+SSE after login.
//
// Scoped so each admin circuit gets its own session. (The underlying client is a shared
// singleton; SetBearer mutates its bundled backends, so this is single-admin-at-a-time in the
// guide. A multi-admin deployment would use one router per session.)
public class AdminAuth
{
    private readonly SleipnirGeneratedClient _client;
    public string? Token { get; private set; }
    public Profile? Profile { get; private set; }
    public bool IsLoggedIn => Token is not null;

    public AdminAuth(SleipnirGeneratedClient client) => _client = client;

    public async Task<(bool Ok, string? Error)> LoginAsync(string username, string password)
    {
        try
        {
            // Login returns SleipnirResponse; its `data` field is { token, profile }. The
            // generator emits opaque `unknown` for a SleipnirResponse return type (it can't see
            // the inner shape), so we deserialize the data field into a tiny local payload.
            // Bad credentials come back as a 401 SleipnirResponse with no data → payload is null.
            var payload = await _client.Call<LoginPayload>(_client.Account.Login(username, password));
            if (payload?.Token is null || payload.Profile is null)
                return (false, "invalid credentials (401)");

            Token = payload.Token;
            Profile = payload.Profile;
            // Cast the wrapped ISleipnirClient to the router and arm every bundled backend.
            ((SleipnirTransportRouter)_client.Client).SetBearer(Token);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public void Logout()
    {
        Token = null;
        Profile = null;
        ((SleipnirTransportRouter)_client.Client).SetBearer(null);
    }
}

// The shape of Account.Login's `data` field. LoginResult is not generated (it lives inside
// SleipnirResults.Ok, not as a method return type), so we model just the two fields we need.
// Profile IS generated (Account.Me returns it), so we reuse the generated type.
public class LoginPayload
{
    [JsonPropertyName("token")]
    public string? Token { get; set; }
    [JsonPropertyName("profile")]
    public Profile? Profile { get; set; }
}