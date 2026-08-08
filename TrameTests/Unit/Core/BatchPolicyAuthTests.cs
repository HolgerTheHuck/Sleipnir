using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using TrameCommon.Models;
using TrameCore.Services;
using TrameTests.Fixtures;
using Xunit;

namespace TrameTests.Unit.Core;

/// <summary>
/// Regression tests for the 1.1.1 batch-path policy fix (<c>ROADMAP.md</c> R6).
///
/// <c>[TrameAuthorise(Policy=...)]</c> was evaluated only by the
/// <c>TrameAuthorizationInterceptor</c> on the single-call path; the batch path
/// (<c>ResolveAndAuthorizeAsync</c> → <c>CheckAuthorisation</c>) had no access to
/// <c>IAuthorizationService</c>, so a policy method in a batch was silently allowed.
/// Hotfix 1.1.1 plumbed a <c>PolicyEvaluator</c> delegate onto the invoker, set by
/// <c>AddTrame</c> when <c>IAuthorizationService</c> is available, and <c>CheckAuthorisation</c>
/// now evaluates it in the serial pre-pass. <c>PolicyEvaluator</c> appeared nowhere in
/// <c>TrameTests</c> before this — a security hotfix without a regression test.
/// </summary>
[Collection("auth-propagation")]
public class BatchPolicyAuthTests
{
    private readonly TrameInvoker _invoker;

    public BatchPolicyAuthTests()
    {
        PolicyAuthController.ResetCounters();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTransient<PolicyAuthController>();
        var sp = services.BuildServiceProvider();
        _invoker = new TrameInvoker(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ILogger<TrameInvoker>>());
        _invoker.Register<PolicyAuthController>();

        // Mirror the AddTrame wiring: a PolicyEvaluator that grants "allowed" and denies
        // everything else (the delegate closes over IAuthorizationService in production).
        _invoker.PolicyEvaluator = (ctx, policy) => Task.FromResult(policy == "allowed");
    }

    private static TrameRequest Req(string id, string method,
        Dictionary<string, string>? mapping = null,
        params (string name, string jsonValue)[] parameters)
    {
        var paramList = parameters
            .Select(p => new TrameParameter
            {
                ParameterName = p.name,
                Data = p.jsonValue.StartsWith("@") ? JsonValue.Create(p.jsonValue) : JsonNode.Parse(p.jsonValue)
            })
            .ToList();
        return new TrameRequest
        {
            Id = id,
            Controller = "PolicyAuth",
            Method = method,
            Params = JsonSerializer.SerializeToNode(paramList),
            DependencyMapping = mapping,
        };
    }

    private static (string name, string jsonValue) Alias(string paramName, string alias) =>
        (paramName, $"@{alias}");

    /// <summary>HttpContext with an authenticated user so <c>OnAuthorization</c> passes the
    ///  IsAuthenticated check and the request reaches the policy evaluation.</summary>
    private static HttpContext AuthenticatedContext()
    {
        var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "tester") }, "TestAuth");
        return new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
    }

    [Fact]
    public async Task Parallel_MixedPolicy_OnlyDeniedRequestFailsOthersSucceed()
    {
        // Batch with an open method, a policy the evaluator grants, and a policy it denies.
        // The denied request is 403 (authenticated but not permitted); the others succeed
        // — batch failure is per-request (JSON-RPC-conformant).
        var ctx = AuthenticatedContext();
        var batch = new List<TrameRequest>
        {
            Req("r1", "Open"),
            Req("r2", "AllowedPolicy"),
            Req("r3", "DeniedPolicy"),
        };

        var responses = (await _invoker.InvokeDi(batch, ctx, ExecutionMode.Parallel)).ToList();

        responses.Should().HaveCount(3);
        responses.Single(r => r?.Id == "r1")!.Code.Should().Be((int)HttpStatusCode.OK);
        responses.Single(r => r?.Id == "r2")!.Code.Should().Be((int)HttpStatusCode.OK);
        var denied = responses.Single(r => r?.Id == "r3")!;
        denied.Code.Should().Be((int)HttpStatusCode.Forbidden);

        // The denied method is rejected in the auth pre-pass and never executed.
        PolicyAuthController.OpenCalls.Should().Be(1);
        PolicyAuthController.AllowedCalls.Should().Be(1);
        PolicyAuthController.DeniedCalls.Should().Be(0);
    }

    [Fact]
    public async Task Parallel_PolicyDeniedWithoutEvaluator_StillDenies()
    {
        // The "no evaluator" branch of the 1.1.1 fix: a policy is configured but no
        // PolicyEvaluator is set → the pre-pass denies with 403 (fail-closed), not 200.
        _invoker.PolicyEvaluator = null;
        var ctx = AuthenticatedContext();
        var batch = new List<TrameRequest>
        {
            Req("r1", "AllowedPolicy"),
            Req("r2", "Open"),
        };

        var responses = (await _invoker.InvokeDi(batch, ctx, ExecutionMode.Parallel)).ToList();

        responses.Single(r => r?.Id == "r1")!.Code.Should().Be((int)HttpStatusCode.Forbidden);
        responses.Single(r => r?.Id == "r2")!.Code.Should().Be((int)HttpStatusCode.OK);
        PolicyAuthController.AllowedCalls.Should().Be(0);
        PolicyAuthController.OpenCalls.Should().Be(1);
    }

    [Fact]
    public async Task Topology_DeniedPolicyProvider_DependentsPropagateAndAreNotInvoked()
    {
        // A: DeniedPolicy (authenticated, but the evaluator denies → 403), exposes "a".
        // B: Open @a — a dependent of a denied provider must NOT run; it gets the
        //    propagation 400 citing provider 'A' and the 403, not a runtime "unresolved
        //    alias". Transitivity: A is skipped, so B is caught in the next batch.
        var ctx = AuthenticatedContext();
        var batch = new List<TrameRequest>
        {
            Req("A", "DeniedPolicy", new() { ["a"] = "$" }),
            Req("B", "Open", null, Alias("value", "a")),
        };

        var responses = (await _invoker.InvokeDi(batch, ctx, ExecutionMode.Serial)).ToList();

        responses.Single(r => r?.Id == "A")!.Code.Should().Be((int)HttpStatusCode.Forbidden);
        var b = responses.Single(r => r?.Id == "B")!;
        b.Code.Should().Be((int)HttpStatusCode.BadRequest);
        b.Error!.Message.Should().Contain("provider 'A'");
        b.Error.Message.Should().Contain("403");

        // Neither method body ran — A failed the policy in the pre-pass, B was skipped.
        PolicyAuthController.DeniedCalls.Should().Be(0);
        PolicyAuthController.OpenCalls.Should().Be(0);
    }

    [Fact]
    public async Task Topology_AllowedPolicyProvider_DependentSucceeds()
    {
        // Happy path: a provider whose policy the evaluator grants exposes the alias and
        // the dependent runs with the resolved value.
        var ctx = AuthenticatedContext();
        var batch = new List<TrameRequest>
        {
            Req("A", "AllowedPolicy", new() { ["a"] = "$" }),
            Req("B", "Open", null, Alias("value", "a")),
        };

        var responses = (await _invoker.InvokeDi(batch, ctx, ExecutionMode.Serial)).ToList();

        responses.Single(r => r?.Id == "A")!.Code.Should().Be((int)HttpStatusCode.OK);
        responses.Single(r => r?.Id == "B")!.Code.Should().Be((int)HttpStatusCode.OK);
        PolicyAuthController.AllowedCalls.Should().Be(1);
        PolicyAuthController.OpenCalls.Should().Be(1);
    }
}