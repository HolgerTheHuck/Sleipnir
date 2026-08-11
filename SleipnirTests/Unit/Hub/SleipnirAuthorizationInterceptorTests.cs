using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SleipnirCommon;
using SleipnirCommon.Models;
using SleipnirCore.Attributes;
using SleipnirCore.Services;
using Xunit;

namespace SleipnirTests.Unit.Hub;

/// <summary>
/// Unit-Tests für den SleipnirAuthorizationInterceptor (Phase 1).
/// Testet isoliert mit Mock-IAuthorizationService: 401/403/Policy-Success/Policy-Fail/
/// IAuthorizationService-null→500. Hotfix 1.1.1: Test-Abdeckung für Policy-Auth.
/// </summary>
public class SleipnirAuthorizationInterceptorTests
{
    private static readonly ILogger<SleipnirHub.Interceptors.SleipnirAuthorizationInterceptor> Logger =
        NullLogger<SleipnirHub.Interceptors.SleipnirAuthorizationInterceptor>.Instance;

    [Fact]
    public async Task InvokeAsync_NoInvokeInfo_PassesThrough()
    {
        var interceptor = new SleipnirHub.Interceptors.SleipnirAuthorizationInterceptor(null, Logger, requireAuthentication: false);
        var ctx = new SleipnirInvocationContext
        {
            Request = new SleipnirRequest { Controller = "C", Method = "M", Id = "1" },
            CancellationToken = CancellationToken.None,
        };
        var called = false;
        var result = await interceptor.InvokeAsync(ctx, _ => { called = true; return Task.FromResult<SleipnirResponse?>(new SleipnirResponse { Code = 200 }); });
        called.Should().BeTrue();
        result!.Code.Should().Be(200);
    }

    [Fact]
    public async Task InvokeAsync_AnonymousAttribute_PassesThrough()
    {
        var interceptor = new SleipnirHub.Interceptors.SleipnirAuthorizationInterceptor(null, Logger, requireAuthentication: true);
        var ctx = new SleipnirInvocationContext
        {
            Request = new SleipnirRequest { Controller = "C", Method = "M", Id = "1" },
            CancellationToken = CancellationToken.None,
            InvokeInfo = new SleipnirInvoker.InvokeInfo { AnonymousAttribute = new SleipnirAnonymousAttribute() },
        };
        var result = await interceptor.InvokeAsync(ctx, _ => Task.FromResult<SleipnirResponse?>(new SleipnirResponse { Code = 200 }));
        result!.Code.Should().Be(200);
    }

    [Fact]
    public async Task InvokeAsync_RequireAuth_NotAuthenticated_Returns401()
    {
        var interceptor = new SleipnirHub.Interceptors.SleipnirAuthorizationInterceptor(null, Logger, requireAuthentication: true);
        var ctx = new SleipnirInvocationContext
        {
            Request = new SleipnirRequest { Controller = "C", Method = "M", Id = "1" },
            HttpContext = new DefaultHttpContext(), // nicht authentifiziert
            CancellationToken = CancellationToken.None,
            InvokeInfo = new SleipnirInvoker.InvokeInfo(),
        };
        var result = await interceptor.InvokeAsync(ctx, _ => Task.FromResult<SleipnirResponse?>(new SleipnirResponse { Code = 200 }));
        result!.Code.Should().Be(401);
    }

    [Fact]
    public async Task InvokeAsync_WithRole_UserHasRole_Passes()
    {
        var interceptor = new SleipnirHub.Interceptors.SleipnirAuthorizationInterceptor(null, Logger, requireAuthentication: false);
        var httpContext = new DefaultHttpContext();
        httpContext.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
            new[] { new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "Admin") }, "Test"));
        var ctx = new SleipnirInvocationContext
        {
            Request = new SleipnirRequest { Controller = "C", Method = "M", Id = "1" },
            HttpContext = httpContext,
            CancellationToken = CancellationToken.None,
            InvokeInfo = new SleipnirInvoker.InvokeInfo { AuthoriseAttribute = new SleipnirAuthoriseAttribute("Admin") },
        };
        var result = await interceptor.InvokeAsync(ctx, _ => Task.FromResult<SleipnirResponse?>(new SleipnirResponse { Code = 200 }));
        result!.Code.Should().Be(200);
    }

    [Fact]
    public async Task InvokeAsync_WithRole_UserLacksRole_Returns403()
    {
        var interceptor = new SleipnirHub.Interceptors.SleipnirAuthorizationInterceptor(null, Logger, requireAuthentication: false);
        var httpContext = new DefaultHttpContext();
        httpContext.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
            new[] { new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "User") }, "Test"));
        var ctx = new SleipnirInvocationContext
        {
            Request = new SleipnirRequest { Controller = "C", Method = "M", Id = "1" },
            HttpContext = httpContext,
            CancellationToken = CancellationToken.None,
            InvokeInfo = new SleipnirInvoker.InvokeInfo { AuthoriseAttribute = new SleipnirAuthoriseAttribute("Admin") },
        };
        var result = await interceptor.InvokeAsync(ctx, _ => Task.FromResult<SleipnirResponse?>(new SleipnirResponse { Code = 200 }));
        result!.Code.Should().Be(403);
    }

    [Fact]
    public async Task InvokeAsync_WithPolicy_PolicySucceeds_Passes()
    {
        var authService = new TestAuthorizationService(succeed: true);
        var interceptor = new SleipnirHub.Interceptors.SleipnirAuthorizationInterceptor(authService, Logger, requireAuthentication: false);
        var httpContext = new DefaultHttpContext();
        httpContext.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity("Test"));
        var ctx = new SleipnirInvocationContext
        {
            Request = new SleipnirRequest { Controller = "C", Method = "M", Id = "1" },
            HttpContext = httpContext,
            CancellationToken = CancellationToken.None,
            InvokeInfo = new SleipnirInvoker.InvokeInfo { AuthoriseAttribute = new SleipnirAuthoriseAttribute { Policy = "CanApprove" } },
        };
        var result = await interceptor.InvokeAsync(ctx, _ => Task.FromResult<SleipnirResponse?>(new SleipnirResponse { Code = 200 }));
        result!.Code.Should().Be(200);
        authService.LastPolicy.Should().Be("CanApprove");
    }

    [Fact]
    public async Task InvokeAsync_WithPolicy_PolicyFails_Returns403()
    {
        var authService = new TestAuthorizationService(succeed: false);
        var interceptor = new SleipnirHub.Interceptors.SleipnirAuthorizationInterceptor(authService, Logger, requireAuthentication: false);
        var httpContext = new DefaultHttpContext();
        httpContext.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity("Test"));
        var ctx = new SleipnirInvocationContext
        {
            Request = new SleipnirRequest { Controller = "C", Method = "M", Id = "1" },
            HttpContext = httpContext,
            CancellationToken = CancellationToken.None,
            InvokeInfo = new SleipnirInvoker.InvokeInfo { AuthoriseAttribute = new SleipnirAuthoriseAttribute { Policy = "CanApprove" } },
        };
        var result = await interceptor.InvokeAsync(ctx, _ => Task.FromResult<SleipnirResponse?>(new SleipnirResponse { Code = 200 }));
        result!.Code.Should().Be(403);
    }

    [Fact]
    public async Task InvokeAsync_WithPolicy_NoAuthService_Returns500()
    {
        var interceptor = new SleipnirHub.Interceptors.SleipnirAuthorizationInterceptor(null, Logger, requireAuthentication: false);
        var httpContext = new DefaultHttpContext();
        httpContext.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity("Test"));
        var ctx = new SleipnirInvocationContext
        {
            Request = new SleipnirRequest { Controller = "C", Method = "M", Id = "1" },
            HttpContext = httpContext,
            CancellationToken = CancellationToken.None,
            InvokeInfo = new SleipnirInvoker.InvokeInfo { AuthoriseAttribute = new SleipnirAuthoriseAttribute { Policy = "CanApprove" } },
        };
        var result = await interceptor.InvokeAsync(ctx, _ => Task.FromResult<SleipnirResponse?>(new SleipnirResponse { Code = 200 }));
        result!.Code.Should().Be(500);
    }

    /// <summary>Minimal IAuthorizationService-Mock — immer succeed oder fail.</summary>
    private sealed class TestAuthorizationService : IAuthorizationService
    {
        private readonly bool _succeed;
        public string? LastPolicy { get; private set; }

        public TestAuthorizationService(bool succeed) => _succeed = succeed;

        public Task<AuthorizationResult> AuthorizeAsync(System.Security.Claims.ClaimsPrincipal user, object? resource, IEnumerable<IAuthorizationRequirement> requirements)
            => Task.FromResult(_succeed ? AuthorizationResult.Success() : AuthorizationResult.Failed());

        public Task<AuthorizationResult> AuthorizeAsync(System.Security.Claims.ClaimsPrincipal user, object? resource, string policyName)
        {
            LastPolicy = policyName;
            return Task.FromResult(_succeed ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        }
    }
}