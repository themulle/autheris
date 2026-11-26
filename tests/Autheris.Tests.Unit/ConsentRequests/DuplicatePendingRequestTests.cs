namespace Autheris.Tests.Unit.ConsentRequests;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.ConsentRequests;
using Autheris.Application.Governance.ConsentRequests;
using Autheris.Application.Governance.Grants;
using Autheris.Tests.Common.Fakes;
using Autheris.Tests.Unit.Catalog;
using Autheris.Application.Governance.Requests;
using Autheris.Application.Security;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>Issue #1168: an identical open request answers 409 DUPLICATE_PENDING_REQUEST with the id of the caller's own request.</summary>
[Trait("Category", "Security")]
public sealed class DuplicatePendingRequestTests
{
    private const string CallerSid = "S-1-5-21-USER-1";
    private const string FixedDetail = "An identical permission request is already pending.";
    private const string ExistingJustification = "Secret justification of the existing request";
    private const string Justification = "Quarterly reconciliation of the orders ledger";
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly TableIdentifier OrdersTable = new("pg", "public", "orders");

    private static System.Security.Claims.ClaimsPrincipal Caller() =>
        ConsentApiTestHarness.BuildUserPrincipal(TenantA.Value, CallerSid, "Developer");

    private static CreateConsentRequestBody Body(int days = 7) =>
        new(Domain: "pg", Schema: "public", TableName: "orders", Justification: Justification, DurationDays: days);

    private static ConsentRequest Seed(
        ConsentApiTestHarness harness,
        string status = "PENDING",
        string requesterSid = CallerSid,
        TenantId? tenant = null)
    {
        var request = new ConsentRequest
        {
            Id = Guid.NewGuid(),
            TableIdentifier = OrdersTable,
            RequesterSid = new Sid(requesterSid),
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = requesterSid,
            RequestKind = ConsentRequestKind.Data,
            Status = status,
            BusinessJustification = ExistingJustification,
            TenantId = tenant ?? TenantA
        };
        harness.Store.Requests.Add(request);
        return request;
    }

    [Fact]
    public async Task CreateRequest_DuplicatePending_Returns409WithOwnRequestId()
    {
        await using var harness = ConsentApiTestHarness.Create();
        var existing = Seed(harness);

        var response = await harness.CreateRequestAsync(Caller(), Body());

        response.StatusCode.ShouldBe(StatusCodes.Status409Conflict);
        var problem = response.ReadJson();
        problem.GetProperty("code").GetString().ShouldBe("DUPLICATE_PENDING_REQUEST");
        problem.GetProperty("requestId").GetString().ShouldBe(existing.Id.ToString("D"));
    }

    [Fact]
    public async Task CreateRequest_DuplicateAwaitingSecondApproval_Returns409WithOwnRequestId()
    {
        await using var harness = ConsentApiTestHarness.Create();
        var existing = Seed(harness, SecretFourEyesRule.PendingSecondApproval);

        var response = await harness.CreateRequestAsync(Caller(), Body());

        response.StatusCode.ShouldBe(StatusCodes.Status409Conflict);
        var problem = response.ReadJson();
        problem.GetProperty("code").GetString().ShouldBe("DUPLICATE_PENDING_REQUEST");
        problem.GetProperty("requestId").GetString().ShouldBe(existing.Id.ToString("D"));
    }

    [Fact]
    public async Task CreateRequest_DuplicatePending_OtherUsersIdenticalRequest_IsNotDuplicateAndDoesNotExposeId()
    {
        await using var harness = ConsentApiTestHarness.Create();
        var other = Seed(harness, requesterSid: "S-1-5-21-USER-2");

        var response = await harness.CreateRequestAsync(Caller(), Body());

        response.StatusCode.ShouldBe(StatusCodes.Status201Created);
        response.Body.ShouldNotContain(other.Id.ToString("D"));
    }

    [Fact]
    public async Task CreateRequest_DuplicatePending_OtherTenantIdenticalRequest_IsNotDuplicateAndDoesNotExposeId()
    {
        await using var harness = ConsentApiTestHarness.Create();
        var other = Seed(harness, tenant: new TenantId("tenant-b"));

        var response = await harness.CreateRequestAsync(Caller(), Body());

        response.StatusCode.ShouldBe(StatusCodes.Status201Created);
        response.Body.ShouldNotContain(other.Id.ToString("D"));
    }

    [Fact]
    public async Task CreateRequest_DuplicatePending_ResponseContainsOnlyFixedDetailAndRequestIdExtension()
    {
        await using var harness = ConsentApiTestHarness.Create();
        var existing = Seed(harness);

        var response = await harness.CreateRequestAsync(Caller(), Body());

        response.StatusCode.ShouldBe(StatusCodes.Status409Conflict);
        var problem = response.ReadJson();
        problem.GetProperty("detail").GetString().ShouldBe(FixedDetail);
        response.Body.ShouldNotContain(ExistingJustification);
        response.Body.ShouldNotContain(Justification);
        response.Body.ShouldNotContain("S-1-5-21-USER-1");
        response.Body.ShouldNotContain("orders");
        // The id appears exactly once: in the requestId extension, never in the detail.
        problem.GetProperty("detail").GetString()!.ShouldNotContain(existing.Id.ToString("D"));
        response.Body.Split(existing.Id.ToString("D")).Length.ShouldBe(2);
    }

    [Fact]
    public async Task CreateRequest_DuplicatePending_PersistsNothingAndEmitsNoConsentEvent()
    {
        await using var harness = ConsentApiTestHarness.Create();
        Seed(harness);
        var requestsBefore = harness.Store.Requests.Count;
        var eventsBefore = harness.Store.Events.Count;

        var response = await harness.CreateRequestAsync(Caller(), Body());

        response.StatusCode.ShouldBe(StatusCodes.Status409Conflict);
        harness.Store.Requests.Count.ShouldBe(requestsBefore);
        harness.Store.Events.Count.ShouldBe(eventsBefore);
    }

    [Fact]
    public async Task CreateRequest_PolicyDeniedRequest_WithMatchingPendingRequest_ReturnsPolicyErrorNotDuplicate()
    {
        await using var harness = ConsentApiTestHarness.Create();
        var existing = Seed(harness);

        var response = await harness.CreateRequestAsync(Caller(), Body(days: 36500));

        response.StatusCode.ShouldNotBe(StatusCodes.Status409Conflict);
        response.StatusCode.ShouldBeGreaterThanOrEqualTo(400);
        response.Body.ShouldNotContain("DUPLICATE_PENDING_REQUEST");
        response.Body.ShouldNotContain(existing.Id.ToString("D"));
    }

    [Fact]
    public async Task CreateRequest_RateLimited_WithMatchingPendingRequest_Returns429NotDuplicate()
    {
        // The harness engine has no rate limiter, so this runs at engine level with a repository that holds a matching open request.
        var pendingId = Guid.NewGuid();
        var approvals = Substitute.For<IConsentApprovalRepository>();
        approvals.GetRequestsByRequesterSidAsync(Arg.Any<Sid>(), Arg.Any<TenantId>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<ConsentRequest>
            {
                new()
                {
                    Id = pendingId,
                    TableIdentifier = new TableIdentifier("sales", "dbo", "orders"),
                    RequesterSid = new Sid("S-1"),
                    RequestedGranteeType = GranteeType.User,
                    RequestedGranteeRef = "S-1",
                    RequestKind = ConsentRequestKind.Data,
                    Status = "PENDING",
                    TenantId = new TenantId("tenant-a")
                }
            });
        var limiter = Substitute.For<IRateLimiterService>();
        limiter.CheckCostQuotaAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<ClientQuotaPolicy>(), Arg.Any<CancellationToken>())
            .Returns(new CostQuotaResult(false, 0, 90));
        var engine = new PermissionRequestEngine(
            approvals,
            new InMemoryAuditWriter(),
            new ApproverResolver(),
            new ApproverEligibility(new GovernanceCapabilities()),
            new SeparationOfDutiesService(),
            new GrantPolicy(),
            GrantValidityPolicy.CreateDefault(),
            Array.Empty<IGrantEffectApplier>(),
            rateLimiter: limiter,
            gatewayOptions: Options.Create(new GatewayOptions { Tenancy = TenancyShape.Multi.Options() }));
        var context = new DefaultHttpContext { User = Wp0.User("S-1", "tenant-a") };
        context.RequestServices = new ServiceCollection().AddLogging().AddOptions().AddProblemDetails().BuildServiceProvider();
        context.Response.Body = new System.IO.MemoryStream();

        var result = await ConsentApiEndpoints.CreateRequestAsync(
            new CreateConsentRequestBody(Domain: "sales", Schema: "dbo", TableName: "orders", Justification: "Quarterly audit", DurationDays: 7),
            context,
            engine);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status429TooManyRequests);
        context.Response.Body.Position = 0;
        new System.IO.StreamReader(context.Response.Body).ReadToEnd().ShouldNotContain(pendingId.ToString("D"));
    }

    [Fact]
    public async Task CreateRequest_UnknownConsentErrorCode_StillReturns400()
    {
        await using var harness = ConsentApiTestHarness.Create();

        // An empty justification fails validation with a code that has no dedicated arm, so it takes the generic fallback.
        var response = await harness.CreateRequestAsync(
            Caller(),
            new CreateConsentRequestBody(Domain: "pg", Schema: "public", TableName: "orders", Justification: "", DurationDays: 7));

        response.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        response.Body.ShouldNotContain("requestId");
    }
}
