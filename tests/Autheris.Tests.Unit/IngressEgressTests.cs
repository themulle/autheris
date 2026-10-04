namespace Autheris.Tests.Unit;

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Autheris.Api.Middleware;
using Autheris.Application.Extensibility;
using Autheris.Application.Extensibility.Interceptors;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

public class IngressEgressTests
{
    [Fact]
    public async Task JustificationAndBreakGlassInterceptor_NormalRequest_PassesCleanly()
    {
        var options = Options.Create(new GatewayOptions());
        var interceptor = new JustificationAndBreakGlassInterceptor(options, NullLogger<JustificationAndBreakGlassInterceptor>.Instance);

        var context = new IngressContext
        {
            Path = "/graphql",
            Method = "POST"
        };

        var result = await interceptor.OnIngressAsync(context);

        result.Decision.ShouldBe(IngressDecision.Continue);
        context.Items.ContainsKey("IsBreakGlass").ShouldBeFalse();
    }

    [Fact]
    public async Task JustificationAndBreakGlassInterceptor_BreakGlassWithoutTicket_ChallengesWith412()
    {
        var options = Options.Create(new GatewayOptions());
        var interceptor = new JustificationAndBreakGlassInterceptor(options, NullLogger<JustificationAndBreakGlassInterceptor>.Instance);

        var headers = new Dictionary<string, string>
        {
            ["X-Break-Glass"] = "true"
        };

        var context = new IngressContext
        {
            Headers = headers,
            User = BreakGlassOperator() // SEC M-06: break-glass requires an authenticated principal with role (default)
        };

        var result = await interceptor.OnIngressAsync(context);

        result.Decision.ShouldBe(IngressDecision.Challenge);
        result.StatusCode.ShouldBe(412);
        result.ResponseHeaders.ContainsKey("X-Challenge-Reason").ShouldBeTrue();
    }

    [Theory]
    [InlineData("invalid-ticket")]
    [InlineData("INC")]
    [InlineData("12345")]
    [InlineData("BUG-999")]
    public async Task JustificationAndBreakGlassInterceptor_BreakGlassWithInvalidTicketFormat_ChallengesWith412(string ticket)
    {
        var options = Options.Create(new GatewayOptions());
        var interceptor = new JustificationAndBreakGlassInterceptor(options, NullLogger<JustificationAndBreakGlassInterceptor>.Instance);

        var headers = new Dictionary<string, string>
        {
            ["X-Break-Glass"] = "true",
            ["X-Access-Justification"] = ticket
        };

        var context = new IngressContext
        {
            Headers = headers,
            User = BreakGlassOperator() // SEC M-06: break-glass requires an authenticated principal with role (default)
        };

        var result = await interceptor.OnIngressAsync(context);

        result.Decision.ShouldBe(IngressDecision.Challenge);
        result.StatusCode.ShouldBe(412);
    }

    [Theory]
    [InlineData("INC-10928")]
    [InlineData("CHG-4451")]
    [InlineData("SEC-007")]
    [InlineData("REQ-99212")]
    public async Task JustificationAndBreakGlassInterceptor_BreakGlassWithValidTicket_AllowsAccessAndSetsAuditFlags(string ticket)
    {
        var options = Options.Create(new GatewayOptions());
        var interceptor = new JustificationAndBreakGlassInterceptor(options, NullLogger<JustificationAndBreakGlassInterceptor>.Instance);

        var headers = new Dictionary<string, string>
        {
            ["X-Break-Glass"] = "true",
            ["X-Access-Justification"] = ticket
        };

        var context = new IngressContext
        {
            Headers = headers,
            User = BreakGlassOperator() // SEC M-06: break-glass requires an authenticated principal with role (default)
        };

        var result = await interceptor.OnIngressAsync(context);

        result.Decision.ShouldBe(IngressDecision.Continue);
        context.Items["IsBreakGlass"].ShouldBe(true);
        context.Items["AccessJustification"].ShouldBe(ticket);
    }

    [Fact]
    public async Task JustificationAndBreakGlassInterceptor_BreakGlassGloballyDisabled_DeniesWith403()
    {
        var opts = new GatewayOptions
        {
            Extensibility = new ExtensibilityOptions
            {
                EnableBreakGlass = false
            }
        };
        var options = Options.Create(opts);

        var interceptor = new JustificationAndBreakGlassInterceptor(options, NullLogger<JustificationAndBreakGlassInterceptor>.Instance);

        var headers = new Dictionary<string, string>
        {
            ["X-Break-Glass"] = "true",
            ["X-Access-Justification"] = "INC-12345"
        };

        var context = new IngressContext
        {
            Headers = headers
        };

        var result = await interceptor.OnIngressAsync(context);

        result.Decision.ShouldBe(IngressDecision.Deny);
        result.StatusCode.ShouldBe(403);
    }

    [Fact]
    public async Task AuditLineageEgressInterceptor_ComputesSha256LineageHashAndGovernanceHeaders()
    {
        var options = Options.Create(new GatewayOptions());
        var interceptor = new AuditLineageEgressInterceptor(options);

        var responsePayload = "{\"data\":{\"user\":{\"name\":\"Alice\"}}}";
        var responseBytes = Encoding.UTF8.GetBytes(responsePayload);
        var expectedHash = Convert.ToHexStringLower(SHA256.HashData(responseBytes));

        var ingressContext = new IngressContext();
        ingressContext.Items["IsBreakGlass"] = true;
        ingressContext.Items["AccessJustification"] = "INC-9999";

        var egressContext = new EgressContext
        {
            IngressContext = ingressContext,
            StatusCode = 200,
            ResponseBytes = responseBytes,
            ResponseBodyText = responsePayload
        };

        var result = await interceptor.OnEgressAsync(egressContext);

        result.AdditionalHeaders.ShouldContainKey("X-Audit-Lineage-Hash");
        result.AdditionalHeaders["X-Audit-Lineage-Hash"].ShouldBe(expectedHash);
        result.AdditionalHeaders["X-Governance-Status"].ShouldBe("Evaluated");
        result.AdditionalHeaders["X-Governance-Mode"].ShouldBe("Break-Glass-Active");
        result.AdditionalHeaders["X-Audit-Ticket"].ShouldBe("INC-9999");
    }

    [Fact]
    public async Task ExtensibilityPipeline_ExecutesOrderedInterceptorsAndStopsOnDenial()
    {
        var interceptor1 = new OrderedTestInterceptor(10, IngressResult.Continue());
        var interceptor2 = new OrderedTestInterceptor(20, IngressResult.Deny("Blocked by order 20"));
        var interceptor3 = new OrderedTestInterceptor(30, IngressResult.Continue());

        var pipeline = new ExtensibilityPipeline(
            [interceptor1, interceptor2, interceptor3],
            [],
            NullLogger<ExtensibilityPipeline>.Instance);

        var context = new IngressContext();
        var result = await pipeline.ProcessIngressAsync(context);

        result.Decision.ShouldBe(IngressDecision.Deny);
        result.Reason.ShouldBe("Blocked by order 20");
        interceptor1.Executed.ShouldBeTrue();
        interceptor2.Executed.ShouldBeTrue();
        interceptor3.Executed.ShouldBeFalse(); // Stopped early!
    }

    [Fact]
    public async Task GatewayExtensibilityMiddleware_Integration_ChallengesInvalidBreakGlass()
    {
        var options = Options.Create(new GatewayOptions());
        var pipeline = new ExtensibilityPipeline(
            [new JustificationAndBreakGlassInterceptor(options, NullLogger<JustificationAndBreakGlassInterceptor>.Instance)],
            [new AuditLineageEgressInterceptor(options)],
            NullLogger<ExtensibilityPipeline>.Instance);

        var middleware = new GatewayExtensibilityMiddleware(
            async ctx =>
            {
                ctx.Response.StatusCode = 200;
                await ctx.Response.WriteAsync("{\"data\":{}}");
            },
            pipeline,
            options,
            NullLogger<GatewayExtensibilityMiddleware>.Instance);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = "/graphql";
        httpContext.Request.Method = "POST";
        httpContext.Request.Headers["X-Break-Glass"] = "true";
        httpContext.User = BreakGlassOperator();
        // No justification header -> should challenge 412

        await middleware.InvokeAsync(httpContext);

        httpContext.Response.StatusCode.ShouldBe(412);
        httpContext.Response.Headers.ContainsKey("X-Challenge-Reason").ShouldBeTrue();
    }

    private static ClaimsPrincipal BreakGlassOperator() =>
        new(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "operator"), new Claim(ClaimTypes.Role, "BreakGlassOperator")],
            "TestAuth"));

    private sealed class OrderedTestInterceptor(int order, IngressResult result) : IIngressInterceptor
    {
        public int Order => order;
        public bool Executed { get; private set; }

        public ValueTask<IngressResult> OnIngressAsync(IngressContext context, CancellationToken cancellationToken = default)
        {
            Executed = true;
            return ValueTask.FromResult(result);
        }
    }
}
