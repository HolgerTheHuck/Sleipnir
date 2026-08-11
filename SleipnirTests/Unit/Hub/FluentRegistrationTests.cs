using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Text.Json;
using SleipnirClient.Sleipnir;
using SleipnirCommon;
using SleipnirCore.Services;
using SleipnirHub.Extensions;
using SleipnirTests.Fixtures;
using Xunit;

namespace SleipnirTests.Unit.Hub;

/// <summary>
/// R6 regression for the fluent registration overload (<c>AddSleipnir(SleipnirOptions,
/// Action&lt;SleipnirControllerBuilder&gt;)</c>), which delivers R1 + R2:
/// <list type="bullet">
/// <item>R1: the overload routes through the canonical <c>AddSleipnir</c> (the drifted
/// <c>AddSleipnirCore</c> is gone), so it inherits every canonical side-effect — the camelCase
/// wire + <c>SleipnirResponseJsonConverter</c> JSON options, the SignalR setup, the built-in
/// interceptor set, the <c>SleipnirOptions</c> DI singleton, the rate limiter, all north-bound
/// pass-throughs — and disables the bulk auto-scan (<c>AutoDiscoverControllers=false</c>).</item>
/// <item>R2: each <c>Add&lt;T&gt;</c> / <c>FromAssemblies</c> writes the scoped DI registration
/// immediately, so the controller resolves from DI at request time (the old builder only
/// registered with the invoker, never with DI).</item>
/// </list>
/// These tests build a real DI host + <c>UseSleipnir</c> (no HTTP) and assert the contract end-to-end.
/// </summary>
public class FluentRegistrationTests
{
    private static (ServiceProvider sp, ISleipnirCore core) BuildHost(
        SleipnirOptions options, Action<SleipnirControllerBuilder> configureControllers)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSleipnir(options, configureControllers);
        var sp = services.BuildServiceProvider();
        new ApplicationBuilder(sp).UseSleipnir();
        var core = sp.GetRequiredService<ISleipnirCore>();
        return (sp, core);
    }

    [Fact]
    public async Task FluentOverload_RegistersOnlyExplicitControllers_AndParityWithCanonical()
    {
        // UseSignalR=true exercises the canonical SignalR branch (AddSignalR + the
        // MaximumParallelInvocationsPerClient>0 guard + MessagePack resolver); RequireAuthentication
        // left at default (false) so the open Echo route is callable without a principal.
        var options = new SleipnirOptions { UseSignalR = true, UseMessagePack = true };
        var (sp, core) = BuildHost(options, c => c.Add<TestInvokerController>());
        using (sp)
        {
            // R2: the explicitly-added controller resolves from DI (the old builder never
            // registered it with IServiceCollection, so this would have thrown).
            sp.GetRequiredService<TestInvokerController>().Should().NotBeNull();

            // The added controller is registered with the invoker → Echo routes and runs.
            var echo = await core.InvokeDi(
                SleipnirCall.Init("TestInvoker", "Echo").With("hello-fluent").ToRequest(),
                new DefaultHttpContext(), CancellationToken.None);
            echo!.Code.Should().Be(200);
            echo.Data.Value.GetRawText().Should().Contain("hello-fluent");

            // R1: AutoDiscoverControllers=false → a controller NOT added (PolicyAuthController is
            // [SleipnirController] in this assembly but excluded from the bulk scan) is NOT registered
            // with the invoker → route not found (404), not silently picked up by auto-discovery.
            var missing = await core.InvokeDi(
                SleipnirCall.Init("PolicyAuth", "Open").ToRequest(),
                new DefaultHttpContext(), CancellationToken.None);
            missing!.Code.Should().Be(404, "only explicitly-added controllers are registered under the fluent overload");

            // Parity with canonical AddSleipnir: the host-wide Minimal-API JSON options carry
            // camelCase (the wire contract) and the SleipnirResponseJsonConverter (single-pass Data).
            var jsonOptions = sp.GetRequiredService<IOptions<JsonOptions>>().Value;
            jsonOptions.SerializerOptions.PropertyNamingPolicy.Should().BeSameAs(JsonNamingPolicy.CamelCase,
                "the fluent overload must inherit the canonical camelCase wire configuration");
            jsonOptions.SerializerOptions.Converters.Should().Contain(c => c is SleipnirResponseJsonConverter,
                "the fluent overload must inherit the SleipnirResponseJsonConverter (single-pass Data serialization)");
        }
    }

    [Fact]
    public async Task FluentOverload_HonorsRequireAuthentication()
    {
        // North-bound default-deny is plumbed through the canonical path the fluent overload
        // delegates to → an unauthenticated caller is rejected at the invoker gate.
        var options = new SleipnirOptions { RequireAuthentication = true };
        var (sp, core) = BuildHost(options, c => c.Add<TestInvokerController>());
        using (sp)
        {
            core.RequireAuthentication.Should().BeTrue();

            var resp = await core.InvokeDi(
                SleipnirCall.Init("TestInvoker", "Echo").With("nope").ToRequest(),
                new DefaultHttpContext(), // unauthenticated
                CancellationToken.None);
            resp!.Code.Should().Be(401, "RequireAuthentication must deny unauthenticated calls on the fluent path");
        }
    }
}