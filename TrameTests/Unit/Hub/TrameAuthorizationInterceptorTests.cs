using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TrameCommon;
using TrameCommon.Models;
using TrameCore.Attributes;
using TrameCore.Services;
using Xunit;

namespace TrameTests.Unit.Hub;

/// <summary>
/// Unit-Tests für den TrameAuthorizationInterceptor (Phase 1).
/// Testet isoliert mit Mock-IAuthorizationService: 401/403/Policy-Success/Policy-Fail/
/// IAuthorizationService-null→500. Hotfix 1.1.1: Test-Abdeckung für Policy-Auth.
/// </summary>
public class TrameAuthorizationInterceptorTests
{
    private static readonly ILogger<TrameHub.Interceptors.TrameAuthorizationInterceptor> Logger =
        NullLogger<TrameHub.Interceptors.TrameAuthorizationInterceptor>.Instance;

    [Fact]
    public async Task InvokeAsync_NoInvokeInfo_PassesThrough()
    {
        var interceptor = new TrameHub.Interceptors.TrameAuthorizationInterceptor(null, Logger, requireAuthentication: false);
        var ctx = new TrameInvocationContext
        {
            Request = new TrameRequest { Controller = "C", Method = "M", Id = "1" },
            CancellationToken = CancellationToken.None,
        };
        var called = false;
        var result = await interceptor.InvokeAsync(ctx, _ => { called = true; return Task.FromResult<TrameResponse?>(new TrameResponse { Code = 200 }); });
        called.Should().BeTrue();
        result!.Code.Should().Be(200);
    }

    [Fact]
    public async Task InvokeAsync_AnonymousAttribute_PassesThrough()
    {
        var interceptor = new TrameHub.Interceptors.TrameAuthorizationInterceptor(null, Logger, requireAuthentication: true);
        var ctx = new TrameInvocationContext
        {
            Request = new TrameRequest { Controller = "C", Method = "M", Id = "1" },
            CancellationToken = CancellationToken.None,
            InvokeInfo = new TrameInvoker.InvokeInfo { AnonymousAttribute = new TrameAnonymousAttribute() },
        };
        var result = await interceptor.InvokeAsync(ctx, _ => Task.FromResult<TrameResponse?>(new TrameResponse { Code = 200 }));
        result!.Code.Should().Be(200);
    }

    [Fact]
    public async Task InvokeAsync_RequireAuth_NotAuthenticated_Returns401()
    {
        var interceptor = new TrameHub.Interceptors.TrameAuthorizationInterceptor(null, Logger, requireAuthentication: true);
        var ctx = new TrameInvocationContext
        {
            Request = new TrameRequest { Controller = "C", Method = "M", Id = "1" },
            HttpContext = new DefaultHttpContext(), // nicht authentifiziert
            CancellationToken = CancellationToken.None,
            InvokeInfo = new TrameInvoker.InvokeInfo(),
        };
        var result = await interceptor.InvokeAsync(ctx, _ => Task.FromResult<TrameResponse?>(new TrameResponse { Code = 200 }));
        result!.Code.Should().Be(401);
    }

    [Fact]
    public async Task InvokeAsync_WithRole_UserHasRole_Passes()
    {
        var interceptor = new TrameHub.Interceptors.TrameAuthorizationInterceptor(null, Logger, requireAuthentication: false);
        var httpContext = new DefaultHttpContext();
        httpContext.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
            new[] { new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "Admin") }, "Test"));
        var ctx = new TrameInvocationContext
        {
            Request = new TrameRequest { Controller = "C", Method = "M", Id = "1" },
            HttpContext = httpContext,
            CancellationToken = CancellationToken.None,
            InvokeInfo = new TrameInvoker.InvokeInfo { AuthoriseAttribute = new TrameAuthoriseAttribute("Admin") },
        };
        var result = await interceptor.InvokeAsync(ctx, _ => Task.FromResult<TrameResponse?>(new TrameResponse { Code = 200 }));
        result!.Code.Should().Be(200);
    }

    [Fact]
    public async Task InvokeAsync_WithRole_UserLacksRole_Returns403()
    {
        var interceptor = new TrameHub.Interceptors.TrameAuthorizationInterceptor(null, Logger, requireAuthentication: false);
        var httpContext = new DefaultHttpContext();
        httpContext.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
            new[] { new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "User") }, "Test"));
        var ctx = new TrameInvocationContext
        {
            Request = new TrameRequest { Controller = "C", Method = "M", Id = "1" },
            HttpContext = httpContext,
            CancellationToken = CancellationToken.None,
            InvokeInfo = new TrameInvoker.InvokeInfo { AuthoriseAttribute = new TrameAuthoriseAttribute("Admin") },
        };
        var result = await interceptor.InvokeAsync(ctx, _ => Task.FromResult<TrameResponse?>(new TrameResponse { Code = 200 }));
        result!.Code.Should().Be(403);
    }

    [Fact]
    public async Task InvokeAsync_WithPolicy_PolicySucceeds_Passes()
    {
        var authService = new TestAuthorizationService(succeed: true);
        var interceptor = new TrameHub.Interceptors.TrameAuthorizationInterceptor(authService, Logger, requireAuthentication: false);
        var httpContext = new DefaultHttpContext();
        httpContext.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity("Test"));
        var ctx = new TrameInvocationContext
        {
            Request = new TrameRequest { Controller = "C", Method = "M", Id = "1" },
            HttpContext = httpContext,
            CancellationToken = CancellationToken.None,
            InvokeInfo = new TrameInvoker.InvokeInfo { AuthoriseAttribute = new TrameAuthoriseAttribute { Policy = "CanApprove" } },
        };
        var result = await interceptor.InvokeAsync(ctx, _ => Task.FromResult<TrameResponse?>(new TrameResponse { Code = 200 }));
        result!.Code.Should().Be(200);
        authService.LastPolicy.Should().Be("CanApprove");
    }

    [Fact]
    public async Task InvokeAsync_WithPolicy_PolicyFails_Returns403()
    {
        var authService = new TestAuthorizationService(succeed: false);
        var interceptor = new TrameHub.Interceptors.TrameAuthorizationInterceptor(authService, Logger, requireAuthentication: false);
        var httpContext = new DefaultHttpContext();
        httpContext.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity("Test"));
        var ctx = new TrameInvocationContext
        {
            Request = new TrameRequest { Controller = "C", Method = "M", Id = "1" },
            HttpContext = httpContext,
            CancellationToken = CancellationToken.None,
            InvokeInfo = new TrameInvoker.InvokeInfo { AuthoriseAttribute = new TrameAuthoriseAttribute { Policy = "CanApprove" } },
        };
        var result = await interceptor.InvokeAsync(ctx, _ => Task.FromResult<TrameResponse?>(new TrameResponse { Code = 200 }));
        result!.Code.Should().Be(403);
    }

    [Fact]
    public async Task InvokeAsync_WithPolicy_NoAuthService_Returns500()
    {
        var interceptor = new TrameHub.Interceptors.TrameAuthorizationInterceptor(null, Logger, requireAuthentication: false);
        var httpContext = new DefaultHttpContext();
        httpContext.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity("Test"));
        var ctx = new TrameInvocationContext
        {
            Request = new TrameRequest { Controller = "C", Method = "M", Id = "1" },
            HttpContext = httpContext,
            CancellationToken = CancellationToken.None,
            InvokeInfo = new TrameInvoker.InvokeInfo { AuthoriseAttribute = new TrameAuthoriseAttribute { Policy = "CanApprove" } },
        };
        var result = await interceptor.InvokeAsync(ctx, _ => Task.FromResult<TrameResponse?>(new TrameResponse { Code = 200 }));
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