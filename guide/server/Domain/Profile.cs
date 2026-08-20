namespace Sleipnir.Guide.Api.Domain;

// The authenticated user's profile, returned by Account.Me and embedded in LoginResult.
// Role is "Customer" or "Admin" — the two tiers of the 3-tier app. The role is carried as a
// claim in the JWT (chapter 8) and checked by [SleipnirAuthorise(Role = "Admin")] on the
// admin-only Portfolio methods (StartFeed / StopFeed).
public class Profile
{
    public string Username { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
}