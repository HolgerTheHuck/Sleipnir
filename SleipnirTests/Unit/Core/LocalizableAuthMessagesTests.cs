using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Globalization;
using System.Security.Claims;
using System.Text.Json.Nodes;
using SleipnirCommon.Models;
using SleipnirCommon.Results;
using SleipnirCore.Attributes;
using SleipnirCore.Services;
using SleipnirHub.Interceptors;
using Xunit;

namespace SleipnirTests.Unit.Core;

/// <summary>
/// The framework-generated 401/403 messages are configurable (<c>SleipnirOptions.Messages</c> →
/// <see cref="SleipnirInvoker.Messages"/> + <see cref="SleipnirAuthorizationInterceptor"/>), while
/// code and category stay fixed. Covers the invoker's single-call and batch paths, the auth
/// interceptor (run directly — the invoker performs the same check itself), a culture-resolving
/// subclass, and the empty-text fallback.
/// </summary>
public class LocalizableAuthMessagesTests
{
    /// <summary>Own fixture (no shared static counters with <c>AuthPostureTests</c>, which run in parallel).</summary>
    [SleipnirController("LocalizedAuth", AutoDiscover = false)]
    public class LocalizedAuthController
    {
        [SleipnirMethod("Open")]
        public string Open() => "open";

        [SleipnirMethod("Locked")]
        [SleipnirAuthorise]
        public string Locked() => "locked";

        [SleipnirMethod("AdminOnly")]
        [SleipnirAuthorise(Role = "Admin")]
        public string AdminOnly() => "admin";
    }

    private static SleipnirInvoker CreateInvoker()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTransient<LocalizedAuthController>();
        var sp = services.BuildServiceProvider();
        var invoker = new SleipnirInvoker(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ILogger<SleipnirInvoker>>());
        invoker.Register<LocalizedAuthController>();
        return invoker;
    }

    private static SleipnirRequest Req(string method) => new()
    {
        Controller = "LocalizedAuth",
        Method = method,
        Params = JsonNode.Parse("[]"),
        Id = method
    };

    private static HttpContext AuthenticatedContext()
    {
        var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "tester") }, "TestAuth");
        return new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
    }

    [Fact]
    public async Task Defaults_AreTheOneXTexts()
    {
        var invoker = CreateInvoker();

        var unauth = await invoker.InvokeDi(Req("Locked"), null);
        var forbidden = await invoker.InvokeDi(Req("AdminOnly"), AuthenticatedContext());

        unauth!.Error!.Message.Should().Be("Unauthorized.");
        forbidden!.Error!.Message.Should().Be("Forbidden.");
    }

    [Fact]
    public async Task SingleCall_UsesConfiguredTexts_CodeAndCategoryUnchanged()
    {
        var invoker = CreateInvoker();
        invoker.Messages = new SleipnirMessages
        {
            Unauthenticated = "Bitte anmelden.",
            PermissionDenied = "Keine Berechtigung."
        };

        var unauth = await invoker.InvokeDi(Req("Locked"), null);
        var forbidden = await invoker.InvokeDi(Req("AdminOnly"), AuthenticatedContext());

        unauth!.Code.Should().Be(401);
        unauth.Error!.Message.Should().Be("Bitte anmelden.");
        unauth.Error.Category.Should().Be(SleipnirErrorCategory.Unauthenticated);
        forbidden!.Code.Should().Be(403);
        forbidden.Error!.Message.Should().Be("Keine Berechtigung.");
        forbidden.Error.Category.Should().Be(SleipnirErrorCategory.PermissionDenied);
    }

    [Theory]
    [InlineData(ExecutionMode.Parallel)]
    [InlineData(ExecutionMode.Serial)]
    public async Task Batch_UsesConfiguredTexts(ExecutionMode mode)
    {
        var invoker = CreateInvoker();
        invoker.Messages = new SleipnirMessages { Unauthenticated = "Bitte anmelden." };

        var responses = (await invoker.InvokeDi(new[] { Req("Locked"), Req("Open") }, null, mode)).ToList();

        responses[0]!.Code.Should().Be(401);
        responses[0]!.Error!.Message.Should().Be("Bitte anmelden.");
        responses[1]!.Code.Should().Be(200);
    }

    /// <summary>Runs the interceptor directly with a resolved <c>InvokeInfo</c> for <paramref name="method"/>.</summary>
    private static async Task<SleipnirResponse?> RunInterceptor(
        SleipnirAuthorizationInterceptor interceptor, string method, HttpContext? ctx)
    {
        var methodInfo = typeof(LocalizedAuthController).GetMethod(method)!;
        var info = new SleipnirInvoker.InvokeInfo
        {
            MethodInfo = methodInfo,
            AuthoriseAttribute = (SleipnirAuthoriseAttribute?)Attribute.GetCustomAttribute(methodInfo, typeof(SleipnirAuthoriseAttribute)),
        };
        var context = new SleipnirInvocationContext { Request = Req(method), HttpContext = ctx, InvokeInfo = info };
        return await interceptor.InvokeAsync(context, _ => Task.FromResult<SleipnirResponse?>(new SleipnirResponse { Code = 200 }));
    }

    [Fact]
    public async Task AuthInterceptor_UsesConfiguredTexts()
    {
        var interceptor = new SleipnirAuthorizationInterceptor(
            authorizationService: null,
            NullLogger<SleipnirAuthorizationInterceptor>.Instance,
            requireAuthentication: false,
            new SleipnirMessages { Unauthenticated = "Bitte anmelden.", PermissionDenied = "Keine Berechtigung." });

        var unauth = await RunInterceptor(interceptor, "Locked", null);
        var forbidden = await RunInterceptor(interceptor, "AdminOnly", AuthenticatedContext());

        unauth!.Code.Should().Be(401);
        unauth.Error!.Message.Should().Be("Bitte anmelden.");
        unauth.Error.Category.Should().Be(SleipnirErrorCategory.Unauthenticated);
        forbidden!.Code.Should().Be(403);
        forbidden.Error!.Message.Should().Be("Keine Berechtigung.");
        forbidden.Error.Category.Should().Be(SleipnirErrorCategory.PermissionDenied);
    }

    [Fact]
    public async Task AuthInterceptor_LegacyConstructor_KeepsDefaults()
    {
        var interceptor = new SleipnirAuthorizationInterceptor(
            authorizationService: null,
            NullLogger<SleipnirAuthorizationInterceptor>.Instance,
            requireAuthentication: false);

        (await RunInterceptor(interceptor, "Locked", null))!.Error!.Message.Should().Be("Unauthorized.");
        (await RunInterceptor(interceptor, "AdminOnly", AuthenticatedContext()))!.Error!.Message.Should().Be("Forbidden.");
    }

    /// <summary>Resolves the text from the current UI culture on every read.</summary>
    private sealed class CultureMessages : SleipnirMessages
    {
        public override string Unauthenticated
        {
            get => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de" ? "Bitte anmelden." : "Please sign in.";
        }
    }

    [Fact]
    public async Task Subclass_ResolvesPerRequestCulture()
    {
        var invoker = CreateInvoker();
        invoker.Messages = new CultureMessages();
        var original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("de-DE");
            (await invoker.InvokeDi(Req("Locked"), null))!.Error!.Message.Should().Be("Bitte anmelden.");

            CultureInfo.CurrentUICulture = new CultureInfo("en-US");
            (await invoker.InvokeDi(Req("Locked"), null))!.Error!.Message.Should().Be("Please sign in.");
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    [Fact]
    public async Task EmptyOrNullText_FallsBackToDefault()
    {
        var invoker = CreateInvoker();
        invoker.Messages = new SleipnirMessages { Unauthenticated = "", PermissionDenied = null! };

        (await invoker.InvokeDi(Req("Locked"), null))!.Error!.Message.Should().Be("Unauthorized.");
        (await invoker.InvokeDi(Req("AdminOnly"), AuthenticatedContext()))!.Error!.Message.Should().Be("Forbidden.");
    }
}
