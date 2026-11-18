namespace Autheris.Tests.Unit.ConsentRequests;

using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.ConsentRequests;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Common;
using Autheris.Tests.Unit.Catalog;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>The one access-request limit per (tenant, sid) sits in <see cref="ConsentRequestService"/>, so REST, GraphQL and MCP share it.</summary>
public sealed class AccessRequestRateLimitTests
{
    private static ConsentRequestService Service(IRateLimiterService limiter) => new(
        Substitute.For<ITableMetadataRepository>(),
        Substitute.For<IConsentApprovalRepository>(),
        gatewayOptions: Options.Create(new GatewayOptions { Tenancy = TenancyShape.Multi.Options() }),
        rateLimiter: limiter);

    private static IRateLimiterService Denying(int retryAfter)
    {
        var limiter = Substitute.For<IRateLimiterService>();
        limiter.CheckCostQuotaAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<ClientQuotaPolicy>(), Arg.Any<CancellationToken>())
            .Returns(new CostQuotaResult(false, 0, retryAfter));
        return limiter;
    }

    private static ConsentRequestSubmission Command() => new(
        "sales", "dbo", "orders", "Quarterly audit", 7, null, new Sid("S-1"), Wp0.User("S-1", "tenant-a"), new TenantId("tenant-a"));

    [Fact]
    [Trait("Category", "Security")]
    public async Task Submit_OverTheHourlyLimit_ThrowsRateLimitExceededWithRetryAfter_PerTenantAndSid()
    {
        var limiter = Denying(120);

        var ex = await Should.ThrowAsync<ConsentRequestException>(() => Service(limiter).SubmitAsync(Command()));

        ex.Code.ShouldBe("RATE_LIMIT_EXCEEDED");
        ex.RetryAfterSeconds.ShouldBe(120);
        await limiter.Received(1).CheckCostQuotaAsync(
            "discovery-access-request:tenant-a:S-1", 1, Arg.Is<ClientQuotaPolicy>(p => p.MaxTokensCapacity == 30), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task RestCreateRequest_OverTheHourlyLimit_Answers429WithRetryAfter()
    {
        var context = new DefaultHttpContext { User = Wp0.User("S-1", "tenant-a") };
        context.RequestServices = new ServiceCollection().AddLogging().AddOptions().AddProblemDetails().BuildServiceProvider();
        context.Response.Body = new System.IO.MemoryStream();
        var body = new CreateConsentRequestBody(Domain: "sales", Schema: "dbo", TableName: "orders", Justification: "Quarterly audit", DurationDays: 7);

        var result = await ConsentApiEndpoints.CreateRequestAsync(
            body, context, Service(Denying(90)), Substitute.For<IConsentApprovalRepository>());
        await result.ExecuteAsync(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status429TooManyRequests);
        context.Response.Headers.RetryAfter.ToString().ShouldBe("90");
    }
}
