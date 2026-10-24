namespace Autheris.Tests.Unit;

using System;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Api.Middleware;
using Autheris.Application.Governance.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>Issue #340: empty/malformed bodies on simulation and sunsetting endpoints must yield a sanitized 400, never 500.</summary>
public sealed class GovernanceEmptyBodyValidationTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static DefaultHttpContext CreateContext(params string[] roles)
    {
        var claims = roles.Select(r => new Claim(ClaimTypes.Role, r)).ToList();
        claims.Add(new Claim("tenant_id", "tenant-a"));
        var services = new ServiceCollection().AddLogging().AddOptions().BuildServiceProvider();
        var context = new DefaultHttpContext
        {
            RequestServices = services,
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test", ClaimTypes.Name, ClaimTypes.Role))
        };
        context.Response.Body = new MemoryStream();
        context.Items[TenantResolutionMiddleware.TenantIdItemKey] = new TenantId("tenant-a");
        return context;
    }

    private static async Task<(int Status, string Body)> ExecuteAsync(IResult result, HttpContext context)
    {
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        return (context.Response.StatusCode, await new StreamReader(context.Response.Body).ReadToEndAsync());
    }

    private static void AssertSanitizedBadRequest((int Status, string Body) response)
    {
        response.Status.ShouldBe(StatusCodes.Status400BadRequest);
        response.Body.ShouldContain("BAD_REQUEST");
        response.Body.ShouldNotContain("Exception");
        response.Body.ShouldNotContain("StackTrace");
        response.Body.ShouldNotContain(" at Autheris");
    }

    private static T? Bind<T>(string json) where T : class => JsonSerializer.Deserialize<T>(json, Web);

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"draftPolicyCsv\":\"  \"}")]
    public async Task Replay_EmptyOrBlankBody_Returns400(string json)
    {
        var service = Substitute.For<IPolicySimulationService>();
        var context = CreateContext("GovernanceAdmin");

        var result = await GovernanceEndpoints.ReplayPolicySimulationAsync(Bind<PolicySimulationRequest>(json), service, context);

        AssertSanitizedBadRequest(await ExecuteAsync(result, context));
        await service.DidNotReceiveWithAnyArgs().SimulateAsync(default!, default, default);
    }

    [Fact]
    public async Task Replay_EmptyBody_NonPrivilegedCaller_StillGets403()
    {
        var context = CreateContext("Reader");

        var result = await GovernanceEndpoints.ReplayPolicySimulationAsync(
            Bind<PolicySimulationRequest>("{}"), Substitute.For<IPolicySimulationService>(), context);

        (await ExecuteAsync(result, context)).Status.ShouldBe(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task Replay_ValidBody_Succeeds()
    {
        var service = Substitute.For<IPolicySimulationService>();
        var context = CreateContext("GovernanceAdmin");
        var request = Bind<PolicySimulationRequest>("{\"draftPolicyCsv\":\"p, a, b\"}");

        var result = await GovernanceEndpoints.ReplayPolicySimulationAsync(request, service, context);

        (await ExecuteAsync(result, context)).Status.ShouldBe(StatusCodes.Status200OK);
        await service.Received(1).SimulateAsync(request!, Arg.Any<TenantId>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"targetTable\":\"a.b.c\"}")]
    public async Task RegisterRule_EmptyOrIncompleteBody_Returns400(string json)
    {
        var service = Substitute.For<ISchemaSunsettingService>();
        var context = CreateContext("GovernanceAdmin");

        var result = await GovernanceEndpoints.RegisterSunsettingRuleAsync(Bind<FieldSunsettingRule>(json), service, context);

        AssertSanitizedBadRequest(await ExecuteAsync(result, context));
        await service.DidNotReceiveWithAnyArgs().RegisterRuleAsync(default!, default);
    }

    [Fact]
    public async Task RegisterRule_EmptyBody_NonAdmin_StillGets403()
    {
        var context = CreateContext("DataOwner");

        var result = await GovernanceEndpoints.RegisterSunsettingRuleAsync(
            Bind<FieldSunsettingRule>("{}"), Substitute.For<ISchemaSunsettingService>(), context);

        (await ExecuteAsync(result, context)).Status.ShouldBe(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task RegisterRule_ValidBody_Returns201()
    {
        var service = Substitute.For<ISchemaSunsettingService>();
        var context = CreateContext("GovernanceAdmin");
        var rule = new FieldSunsettingRule(Guid.NewGuid(), "src.public.t", "f", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(30));

        var result = await GovernanceEndpoints.RegisterSunsettingRuleAsync(rule, service, context);

        (await ExecuteAsync(result, context)).Status.ShouldBe(StatusCodes.Status201Created);
        await service.Received(1).RegisterRuleAsync(rule, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"targetTable\":\"a.b.c\"}")]
    public async Task Evaluate_EmptyOrIncompleteBody_Returns400(string json)
    {
        var service = Substitute.For<ISchemaSunsettingService>();
        var context = CreateContext("GovernanceAdmin");

        var result = await GovernanceEndpoints.EvaluateSunsettingAsync(Bind<EvaluateFieldSunsettingRequest>(json), service, context);

        AssertSanitizedBadRequest(await ExecuteAsync(result, context));
        await service.DidNotReceiveWithAnyArgs().EvaluateFieldAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task Evaluate_ValidBody_Succeeds()
    {
        var service = Substitute.For<ISchemaSunsettingService>();
        service.EvaluateFieldAsync("a.b.c", "f", Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<SunsettingEvaluationResult?>((SunsettingEvaluationResult?)null));
        var context = CreateContext("GovernanceAdmin");

        var result = await GovernanceEndpoints.EvaluateSunsettingAsync(
            new EvaluateFieldSunsettingRequest("a.b.c", "f"), service, context);

        (await ExecuteAsync(result, context)).Status.ShouldBe(StatusCodes.Status200OK);
    }
}
