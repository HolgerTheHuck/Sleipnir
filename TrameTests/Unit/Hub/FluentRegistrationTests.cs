using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Text.Json;
using TrameClient.Trame;
using TrameCommon;
using TrameCore.Services;
using TrameHub.Extensions;
using TrameTests.Fixtures;
using Xunit;

namespace TrameTests.Unit.Hub;

/// <summary>
/// R6 regression for the fluent registration overload (<c>AddTrame(TrameOptions,
/// Action&lt;TrameControllerBuilder&gt;)</c>), which delivers R1 + R2:
/// <list type="bullet">
/// <item>R1: the overload routes through the canonical <c>AddTrame</c> (the drifted
/// <c>AddTrameCore</c> is gone), so it inherits every canonical side-effect — the camelCase
/// wire + <c>TrameResponseJsonConverter</c> JSON options, the SignalR setup, the built-in
/// interceptor set, the <c>TrameOptions</c> DI singleton, the rate limiter, all north-bound
/// pass-throughs — and disables the bulk auto-scan (<c>AutoDiscoverControllers=false</c>).</item>
/// <item>R2: each <c>Add&lt;T&gt;</c> / <c>FromAssemblies</c> writes the scoped DI registration
/// immediately, so the controller resolves from DI at request time (the old builder only
/// registered with the invoker, never with DI).</item>
/// </list>
/// These tests build a real DI host + <c>UseTrame</c> (no HTTP) and assert the contract end-to-end.
/// </summary>
public class FluentRegistrationTests
{
    private static (ServiceProvider sp, ITrameCore core) BuildHost(
        TrameOptions options, Action<TrameControllerBuilder> configureControllers)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTrame(options, configureControllers);
        var sp = services.BuildServiceProvider();
        new ApplicationBuilder(sp).UseTrame();
        var core = sp.GetRequiredService<ITrameCore>();
        return (sp, core);
    }

    [Fact]
    public async Task FluentOverload_RegistersOnlyExplicitControllers_AndParityWithCanonical()
    {
        // UseSignalR=true exercises the canonical SignalR branch (AddSignalR + the
        // MaximumParallelInvocationsPerClient>0 guard + MessagePack resolver); RequireAuthentication
        // left at default (false) so the open Echo route is callable without a principal.
        var options = new TrameOptions { UseSignalR = true, UseMessagePack = true };
        var (sp, core) = BuildHost(options, c => c.Add<TestInvokerController>());
        using (sp)
        {
            // R2: the explicitly-added controller resolves from DI (the old builder never
            // registered it with IServiceCollection, so this would have thrown).
            sp.GetRequiredService<TestInvokerController>().Should().NotBeNull();

            // The added controller is registered with the invoker → Echo routes and runs.
            var echo = await core.InvokeDi(
                TrameCall.Init("TestInvoker", "Echo").With("hello-fluent").ToRequest(),
                new DefaultHttpContext(), CancellationToken.None);
            echo!.Code.Should().Be(200);
            echo.Data.Value.GetRawText().Should().Contain("hello-fluent");

            // R1: AutoDiscoverControllers=false → a controller NOT added (PolicyAuthController is
            // [TrameController] in this assembly but excluded from the bulk scan) is NOT registered
            // with the invoker → route not found (404), not silently picked up by auto-discovery.
            var missing = await core.InvokeDi(
                TrameCall.Init("PolicyAuth", "Open").ToRequest(),
                new DefaultHttpContext(), CancellationToken.None);
            missing!.Code.Should().Be(404, "only explicitly-added controllers are registered under the fluent overload");

            // Parity with canonical AddTrame: the host-wide Minimal-API JSON options carry
            // camelCase (the wire contract) and the TrameResponseJsonConverter (single-pass Data).
            var jsonOptions = sp.GetRequiredService<IOptions<JsonOptions>>().Value;
            jsonOptions.SerializerOptions.PropertyNamingPolicy.Should().BeSameAs(JsonNamingPolicy.CamelCase,
                "the fluent overload must inherit the canonical camelCase wire configuration");
            jsonOptions.SerializerOptions.Converters.Should().Contain(c => c is TrameResponseJsonConverter,
                "the fluent overload must inherit the TrameResponseJsonConverter (single-pass Data serialization)");
        }
    }

    [Fact]
    public async Task FluentOverload_HonorsRequireAuthentication()
    {
        // North-bound default-deny is plumbed through the canonical path the fluent overload
        // delegates to → an unauthenticated caller is rejected at the invoker gate.
        var options = new TrameOptions { RequireAuthentication = true };
        var (sp, core) = BuildHost(options, c => c.Add<TestInvokerController>());
        using (sp)
        {
            core.RequireAuthentication.Should().BeTrue();

            var resp = await core.InvokeDi(
                TrameCall.Init("TestInvoker", "Echo").With("nope").ToRequest(),
                new DefaultHttpContext(), // unauthenticated
                CancellationToken.None);
            resp!.Code.Should().Be(401, "RequireAuthentication must deny unauthenticated calls on the fluent path");
        }
    }
}