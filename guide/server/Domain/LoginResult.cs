namespace Sleipnir.Guide.Api.Domain;

// The result of Account.Login: a signed JWT (the client sends it back as a Bearer token on
// subsequent calls) plus the profile it represents, so the client knows who it is without a
// second roundtrip to Account.Me.
public class LoginResult
{
    public string Token { get; set; } = string.Empty;
    public Profile Profile { get; set; } = new();
}