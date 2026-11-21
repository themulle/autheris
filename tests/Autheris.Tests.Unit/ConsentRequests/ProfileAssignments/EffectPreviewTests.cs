namespace Autheris.Tests.Unit.ConsentRequests.ProfileAssignments;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Audit;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.ConsentRequests.Events;
using Autheris.Application.ConsentRequests.ProfileAssignments;
using Autheris.Application.Policy.Pdp;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Autheris.Tests.Unit.ConsentRequests;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class EffectPreviewTests
{
    private static readonly TenantId Tenant = new("tenant-a");
    private static readonly Sid Requester = new("S-1-5-21-USER-REQ");
    private static readonly Sid Approver = new("S-1-5-21-OWNER-APPR");

    [Fact]
    public async Task ApprovalEffectPreview_ReturnsIdsCountsAndFlagsOnly_ForPendingRequest()
    {
        var existingGrantId = Guid.NewGuid();
        var request = new ConsentRequest
        {
            Id = Guid.NewGuid(),
            TenantId = Tenant,
            RequesterSid = Requester,
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = Requester.Value,
            TableIdentifier = new TableIdentifier("pg", "sales", "orders"),
            Status = "PENDING",
            RequestedValidTo = ConsentApiTestHarness.FixedNow.AddDays(30)
        };

        var fakePreview = new StubGrantEffectPreview(new GrantEffectPreviewResult(
            MatchedObjectCount: 3,
            IncludesFutureObjects: true,
            RedundantGrants: [new RedundantGrantFlag(Guid.NewGuid(), existingGrantId)],
            EmptyHardIntersection: true,
            AffectedObjects: ["pg.sales.orders"]));

        var dto = await ApprovalEffectPreviewHelper.TryBuildAsync(fakePreview, request, Approver);

        dto.ShouldNotBeNull();
        dto.MatchedObjectCount.ShouldBe(3);
        dto.IncludesFutureObjects.ShouldBeTrue();
        dto.RedundantGrant.ShouldBeTrue();
        dto.EmptyHardIntersection.ShouldBeTrue();
        dto.RedundantGrantIds.ShouldBe([existingGrantId]);
        dto.Flags.ShouldContain("REDUNDANT_GRANT");
        dto.Flags.ShouldContain("EMPTY_HARD_INTERSECTION");
        dto.Flags.ShouldContain("INCLUDES_FUTURE_OBJECTS");

        var serialized = JsonSerializer.Serialize(dto);
        serialized.ShouldNotContain("WHERE", Case.Sensitive);
        serialized.ShouldNotContain("SELECT", Case.Sensitive);
        serialized.ShouldNotContain("S-1-5-21-FOREIGN", Case.Insensitive);
    }

    [Fact]
    public async Task ApprovalEffectPreview_IsNonBlocking_WhenPreviewThrows_OrRequestAlreadyDecided()
    {
        var pending = new ConsentRequest
        {
            Id = Guid.NewGuid(),
            TenantId = Tenant,
            RequesterSid = Requester,
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = Requester.Value,
            TableIdentifier = new TableIdentifier("pg", "sales", "orders"),
            Status = "PENDING",
            RequestedValidTo = ConsentApiTestHarness.FixedNow.AddDays(30)
        };

        var throwingPreview = new ThrowingGrantEffectPreview();
        (await ApprovalEffectPreviewHelper.TryBuildAsync(throwingPreview, pending, Approver)).ShouldBeNull();

        var approved = new ConsentRequest
        {
            Id = pending.Id,
            TenantId = Tenant,
            RequesterSid = Requester,
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = Requester.Value,
            TableIdentifier = pending.TableIdentifier,
            Status = "APPROVED",
            RequestedValidTo = pending.RequestedValidTo
        };

        var workingPreview = new StubGrantEffectPreview(new GrantEffectPreviewResult(1, false, [], false, []));
        (await ApprovalEffectPreviewHelper.TryBuildAsync(workingPreview, approved, Approver)).ShouldBeNull();
        workingPreview.CallCount.ShouldBe(0);
    }

    [Fact]
    public async Task GetRequestEndpoint_IncludesEffectPreview_WhileThinWebhookExcludesIt()
    {
        await using var harness = ConsentApiTestHarness.Create();
        var req = new ConsentRequest
        {
            Id = Guid.NewGuid(),
            TenantId = new TenantId("tenant-a"),
            RequesterSid = Requester,
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = Requester.Value,
            TableId = Guid.NewGuid(),
            TableIdentifier = new TableIdentifier("pg", "public", "orders"),
            BusinessJustification = "Analytics query access",
            Status = "PENDING",
            RequestedAt = ConsentApiTestHarness.FixedNow,
            RequestedValidTo = ConsentApiTestHarness.FixedNow.AddDays(14),
            Version = 1,
            WorkflowMode = ConsentWorkflowMode.Internal,
            RequestKind = ConsentRequestKind.ProfileAssignment,
            ProfileId = Guid.NewGuid(),
            ContextJson = JsonSerializer.Serialize(new ProfileAssignmentRequestContext(
                Guid.NewGuid(),
                "AnalystProfile",
                MaskingPolicyMode.Default,
                [],
                [new ProfileAssignmentSnapshotItem(0, "pg.public.*", true, "pg", [])],
                IncludesFutureObjects: true))
        };
        harness.Store.Requests.Add(req);

        var coveringGrantId = Guid.NewGuid();
        var fakePreview = new StubGrantEffectPreview(new GrantEffectPreviewResult(
            2, true, [new RedundantGrantFlag(Guid.NewGuid(), coveringGrantId)], false, ["pg.public.orders"]));

        var httpContext = new DefaultHttpContext
        {
            RequestServices = harness.Services,
            Response = { Body = new System.IO.MemoryStream() },
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim("tid", "tenant-a"),
                    new Claim(ClaimTypes.PrimarySid, Requester.Value)
                ],
                "test"))
        };

        var result = await ConsentApiEndpoints.GetRequestByIdAsync(
            req.Id,
            httpContext,
            harness.Store,
            harness.Store,
            effectPreview: fakePreview);

        await result.ExecuteAsync(httpContext);
        httpContext.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        fakePreview.CallCount.ShouldBe(1);

        httpContext.Response.Body.Position = 0;
        using var responseDoc = await JsonDocument.ParseAsync(httpContext.Response.Body);
        var previewElem = responseDoc.RootElement.GetProperty("effectPreview");
        previewElem.GetProperty("matchedObjectCount").GetInt32().ShouldBe(2);
        previewElem.GetProperty("includesFutureObjects").GetBoolean().ShouldBeTrue();
        previewElem.GetProperty("redundantGrant").GetBoolean().ShouldBeTrue();
        previewElem.GetProperty("redundantGrantIds").GetArrayLength().ShouldBe(1);

        var webhookPayload = JsonSerializer.Serialize(ConsentEventPayloads.ForRequest(req, ConsentApiTestHarness.FixedNow));
        webhookPayload.ShouldNotContain("effectPreview", Case.Insensitive);
        webhookPayload.ShouldNotContain("redundantGrant", Case.Insensitive);
    }

    [Fact]
    public async Task GraphQlPendingApprovalsForMe_IncludesEffectPreview_NonBlockingly()
    {
        var req = new ConsentRequest
        {
            Id = Guid.NewGuid(),
            TenantId = Tenant,
            RequesterSid = Requester,
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = Requester.Value,
            TableId = Guid.NewGuid(),
            TableIdentifier = new TableIdentifier("pg", "public", "orders"),
            Status = "PENDING",
            RequestedValidTo = ConsentApiTestHarness.FixedNow.AddDays(10)
        };

        await using var harness = ConsentApiTestHarness.Create();
        var repo = new PendingApprovalRepo(req);
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim("tid", "tenant-a"), new Claim(ClaimTypes.PrimarySid, Approver.Value)], "test"))
            }
        };
        var fakePreview = new StubGrantEffectPreview(new GrantEffectPreviewResult(
            1, false, [], true, ["pg.public.orders"]));

        var query = new Autheris.GraphQL.Types.Query();
        var list = await query.PendingApprovalsForMeAsync(null, null, repo, accessor, fakePreview);

        list.Count.ShouldBe(1);
        list[0].EffectPreview.ShouldNotBeNull();
        list[0].EffectPreview!.EmptyHardIntersection.ShouldBeTrue();
        list[0].EffectPreview!.Flags.ShouldContain("EMPTY_HARD_INTERSECTION");
    }

    private sealed class PendingApprovalRepo(ConsentRequest pending) : IConsentApprovalRepository
    {
        public Task<IReadOnlyList<ConsentRequest>> GetPendingRequestsForApproverAsync(Sid approverSid, TenantId tenantId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ConsentRequest>>([pending]);
        public Task<ConsentRequest> CreateConsentRequestAsync(ConsentRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ConsentRequest?> GetConsentRequestAsync(Guid requestId, CancellationToken ct = default) => Task.FromResult<ConsentRequest?>(pending);
        public Task<IReadOnlyList<ConsentRequest>> GetRequestsByRequesterSidAsync(Sid requesterSid, TenantId tenantId, string? status = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ConsentRequest>>([]);
        public Task<ConsentRequest> ApproveConsentRequestStepAsync(Guid requestId, Sid approverSid, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ConsentRequest> RejectConsentRequestAsync(Guid requestId, Sid approverSid, string reason, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ActivateConsentAsync(Guid requestId, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateRequestStatusAsync(Guid requestId, string status, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateRequestStatusAsync(Guid requestId, string status, string? itsmTicketId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ConsentRequest>> GetExpiredPendingRequestsAsync(DateTimeOffset beforeTime, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ConsentRequest>>([]);
        public Task<IReadOnlyList<ConsentRequest>> GetTimedOutRequestsAsync(DateTimeOffset olderThan, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ConsentRequest>>([]);
        public Task DeleteConsentRequestAsync(Guid requestId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StubGrantEffectPreview(GrantEffectPreviewResult result) : IGrantEffectPreview
    {
        public int CallCount { get; private set; }

        public Task<GrantEffectPreviewResult> PreviewAsync(
            SecurityPrincipalContext subject,
            IReadOnlyList<GrantPreviewRequestedItem> requestedItems,
            CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(result);
        }

        public Task<GrantEffectPreviewResult> PreviewAsync(
            SecurityPrincipalContext actor,
            SecurityPrincipalContext subject,
            IReadOnlyList<GrantPreviewRequestedItem> requestedItems,
            CancellationToken ct = default) =>
            PreviewAsync(subject, requestedItems, ct);
    }

    private sealed class ThrowingGrantEffectPreview : IGrantEffectPreview
    {
        public Task<GrantEffectPreviewResult> PreviewAsync(
            SecurityPrincipalContext subject,
            IReadOnlyList<GrantPreviewRequestedItem> requestedItems,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("Simulated catalog outage");
    }
}
