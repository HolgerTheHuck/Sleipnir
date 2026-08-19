using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Security.Claims;
using SleipnirCore.Services;
using SleipnirTests.Fixtures;
using Xunit;

namespace SleipnirTests.Unit.Core;

/// <summary>
/// Focused unit tests for the Phase R3 reconnect auth re-check
/// (<see cref="SleipnirInvoker.AuthorizeSubscribeAsync"/>). A resume re-runs the SAME authorization
/// a fresh subscribe runs, against the ORIGINAL controller/method recorded on the durable state at
/// create time (NOT the client-claimed route — a caller cannot lie about the route to land a weaker
/// auth check). The E2E <c>ResumeTests.AuthRevoke_OnResume_RejectedAndTornDown</c> proves the 401
/// path plus teardown end-to-end; this class isolates <em>every</em> branch of the public method:
/// unknown controller (404), unknown method (400 — a missing method on an existing controller is a
/// bad request, mirroring the single-call path's convention), a non-event route (400), authenticated-
/// but-role-revoked (403 — the realistic "role revoked during the disconnect gap, token still valid"
/// case), unauthenticated (401), and an authorized resume (null = pass). Branch-by-branch coverage
/// closes the gap left by the single 401 E2E test.
/// </summary>
public class AuthorizeSubscribeTests
{
    private readonly SleipnirInvoker _invoker;

    public AuthorizeSubscribeTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTransient<AuthedResumableEventController>();
        services.AddTransient<PlainCallController>();
        var sp = services.BuildServiceProvider();
        _invoker = new SleipnirInvoker(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ILogger<SleipnirInvoker>>());
        _invoker.Register<AuthedResumableEventController>();
        _invoker.Register<PlainCallController>();
    }

    /// <summary>HttpContext with an authenticated user (optionally in the Admin role).</summary>
    private static HttpContext AuthenticatedContext(bool admin = false)
    {
        var claims = new List<Claim> { new(ClaimTypes.Name, "tester") };
        if (admin) claims.Add(new Claim(ClaimTypes.Role, "Admin"));
        var identity = new ClaimsIdentity(claims, "TestAuth");
        return new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
    }

    [Fact]
    public async Task Unknown_Controller_Returns_404()
    {
        var err = await _invoker.AuthorizeSubscribeAsync("Nope", "SecureTick", AuthenticatedContext(admin: true));
        err.Should().NotBeNull();
        err!.Code.Should().Be((int)HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Unknown_Method_Returns_400()
    {
        // A missing method on an existing controller is a bad request, not a 404 — this mirrors
        // the single-call path's convention (controller-not-found is 404, method-not-found is 400).
        var err = await _invoker.AuthorizeSubscribeAsync("AuthedResumableEvent", "Nope", AuthenticatedContext(admin: true));
        err.Should().NotBeNull();
        err!.Code.Should().Be((int)HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task NonEvent_Method_Returns_400()
    {
        // A caller cannot lie that a plain call is a resumable event to land a weaker auth check.
        var err = await _invoker.AuthorizeSubscribeAsync("PlainCall", "Ping", AuthenticatedContext(admin: true));
        err.Should().NotBeNull();
        err!.Code.Should().Be((int)HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Authenticated_ButRoleRevoked_Returns_403()
    {
        // The realistic "role revoked during the disconnect gap, token still valid" case —
        // authenticated, but the Admin role is gone, so the resume must not silently resume.
        var err = await _invoker.AuthorizeSubscribeAsync(
            "AuthedResumableEvent", "SecureTick", AuthenticatedContext(admin: false));
        err.Should().NotBeNull();
        err!.Code.Should().Be((int)HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Unauthenticated_Returns_401()
    {
        var err = await _invoker.AuthorizeSubscribeAsync("AuthedResumableEvent", "SecureTick", null);
        err.Should().NotBeNull();
        err!.Code.Should().Be((int)HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Authorized_Resume_Returns_Null()
    {
        // Authenticated Admin — the original route's auth check passes, so resume proceeds.
        var err = await _invoker.AuthorizeSubscribeAsync(
            "AuthedResumableEvent", "SecureTick", AuthenticatedContext(admin: true));
        err.Should().BeNull();
    }
}