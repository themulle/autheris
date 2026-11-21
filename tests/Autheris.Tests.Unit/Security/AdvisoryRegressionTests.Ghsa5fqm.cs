namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.ConsentRequests;
using Autheris.Application.ConsentRequests.Routing;
using Autheris.Application.Governance.Analysis;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Common.Fakes;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

public sealed partial class AdvisoryRegressionTests
{
    [Fact]
    [Trait("Category", "Security")]
    public async Task Ghsa_5fqm_mpc4_regression()
    {
        var ordersId = new TableIdentifier("sales", "dbo", "orders");
        var orders = new TableMetadata
        {
            Identifier = ordersId,
            Table = new Table { Id = Guid.NewGuid(), SourceName = "sales", SchemaName = "dbo", TableName = "orders", IsActive = true },
            Columns = [new TableColumn { ColumnName = "id", DataType = "int" }]
        };
        var tables = new InMemoryTableMetadataRepository(orders);
        var consents = new InMemoryConsentRepository();
        var approvals = new RecordingConsentApprovalRepository();

        var extSystemId = Guid.NewGuid();
        var router = new FixedWorkflowRouter(new ConsentWorkflowRouteResolution(
            ConsentWorkflowMode.External,
            extSystemId,
            MaxDurationDays: 30,
            RequestTimeoutDays: 14,
            ExpiryNoticeLeadHours: 24,
            RequireResolvableApprover: false,
            WorkflowRouteScopeKind.DataSource));

        var fixedNow = new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
        var clock = new FakeTimeProvider(fixedNow);
        var limiter = new ConfigurableRateLimiter { CostQuotaOutcome = new CostQuotaResult(true, 10, 0) };

        var svc = new BundledConsentRequestService(
            tables,
            consents,
            new InMemoryAuditLogRepository(),
            approvals,
            gatewayOptions: Options.Create(new GatewayOptions()),
            workflowRouter: router,
            rateLimiter: limiter,
            timeProvider: clock);

        var personUser = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-USER"),
            new Claim(ClaimTypes.Email, "user@example.com"),
            new Claim("tenant_id", "tenant-a")
        ], "Bearer"));

        // 1. External workflow routing and 30-day route duration cap are enforced on default and explicit durations.
        var clamped = await svc.SubmitBundledRequestAsync(
            personUser,
            new BundledConsentRequest(
                [new RequestableConsentItem("sales.dbo.orders", null, "READ", "Analytics query need")],
                Purpose: "Analytics query need",
                ConsumerReference: "PR-1"));
        clamped.Tickets.ShouldHaveSingleItem().Status.ShouldBe("PENDING_EXTERNAL_APPROVAL");
        approvals.Created.ShouldHaveSingleItem();
        approvals.Created[0].WorkflowMode.ShouldBe(ConsentWorkflowMode.External);
        approvals.Created[0].ExternalSystemId.ShouldBe(extSystemId);
        approvals.Created[0].Status.ShouldBe("PENDING_EXTERNAL_APPROVAL");
        approvals.Created[0].RequestedValidTo.ShouldBe(fixedNow.AddDays(30));
        approvals.Created[0].RequesterIdentifiers.ShouldNotBeNull();
        approvals.Created[0].RequesterIdentifiers.ShouldContain("S-1-5-21-USER");
        approvals.Created[0].RequesterIdentifiers.ShouldContain("user@example.com");

        var overCapEx = await Should.ThrowAsync<ConsentRequestException>(() => svc.SubmitBundledRequestAsync(
            personUser,
            new BundledConsentRequest(
                [new RequestableConsentItem("sales.dbo.orders", null, "READ", "Analytics query need")],
                Purpose: "Analytics query need",
                RequestedUntilUtc: fixedNow.AddDays(60))));
        overCapEx.Code.ShouldBe("VALIDITY_EXCEEDS_MAXIMUM");

        // 2. App-only tokens, short justifications, disabled self-service, and rate-limit exhaustion are rejected before creation.
        var appOnly = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-APP"),
            new Claim("idtyp", "app"),
            new Claim("tenant_id", "tenant-a")
        ], "Bearer"));
        (await Should.ThrowAsync<ConsentRequestException>(() => svc.SubmitBundledRequestAsync(
            appOnly,
            new BundledConsentRequest(
                [new RequestableConsentItem("sales.dbo.orders", null, "READ", "Analytics query need")],
                Purpose: "Analytics query need"))))
            .Code.ShouldBe("FORBIDDEN");

        (await Should.ThrowAsync<ConsentRequestException>(() => svc.SubmitBundledRequestAsync(
            personUser,
            new BundledConsentRequest(
                [new RequestableConsentItem("sales.dbo.orders", null, "READ", "Analytics query need")],
                Purpose: "ab"))))
            .Code.ShouldBe("VALIDATION_ERROR");

        var disabledSvc = new BundledConsentRequestService(
            tables,
            consents,
            new InMemoryAuditLogRepository(),
            approvals,
            gatewayOptions: Options.Create(new GatewayOptions
            {
                Authentication = new AuthenticationOptions { EntraId = new EntraIdAuthOptions { SelfServiceEnabled = false } }
            }));
        (await Should.ThrowAsync<ConsentRequestException>(() => disabledSvc.SubmitBundledRequestAsync(
            personUser,
            new BundledConsentRequest(
                [new RequestableConsentItem("sales.dbo.orders", null, "READ", "Analytics query need")],
                Purpose: "Analytics query need"))))
            .Code.ShouldBe("FORBIDDEN");

        limiter.CostQuotaOutcome = new CostQuotaResult(false, 0, 42);
        var rlEx = await Should.ThrowAsync<ConsentRequestException>(() => svc.SubmitBundledRequestAsync(
            personUser,
            new BundledConsentRequest(
                [new RequestableConsentItem("sales.dbo.orders", null, "READ", "Analytics query need")],
                Purpose: "Analytics query need")));
        rlEx.Code.ShouldBe("RATE_LIMIT_EXCEEDED");
        rlEx.RetryAfterSeconds.ShouldBe(42);
    }

    private sealed class RecordingConsentApprovalRepository : IConsentApprovalRepository
    {
        public List<ConsentRequest> Created { get; } = [];

        public Task<ConsentRequest> CreateConsentRequestAsync(ConsentRequest request, CancellationToken ct = default)
        {
            Created.Add(request);
            return Task.FromResult(request);
        }

        public Task<ConsentRequest?> GetConsentRequestAsync(Guid requestId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ConsentRequest>> GetPendingRequestsForApproverAsync(Sid approverSid, TenantId tenantId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ConsentRequest> ApproveConsentRequestStepAsync(Guid requestId, Sid approverSid, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ConsentRequest> RejectConsentRequestAsync(Guid requestId, Sid approverSid, string reason, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ActivateConsentAsync(Guid requestId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteConsentRequestAsync(Guid requestId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FixedWorkflowRouter(ConsentWorkflowRouteResolution resolution) : IConsentWorkflowRouter
    {
        public Task<ConsentWorkflowRouteResolution> ResolveForTableAsync(TenantId tenantId, TableIdentifier table, CancellationToken ct = default)
            => Task.FromResult(resolution);

        public Task<ConsentWorkflowRouteResolution> ResolveForTablesAsync(TenantId tenantId, IReadOnlyList<TableIdentifier> tables, CancellationToken ct = default)
            => Task.FromResult(resolution);
    }

    private sealed class ConfigurableRateLimiter : IRateLimiterService
    {
        public CostQuotaResult CostQuotaOutcome { get; set; } = new(true, 10, 0);

        public Task<CostQuotaResult> CheckCostQuotaAsync(string key, int requestedCost, ClientQuotaPolicy policy, CancellationToken ct = default)
            => Task.FromResult(CostQuotaOutcome);

        public Task<RateLimitResult> CheckPreAuthIpAsync(string ip, PreAuthIpRateLimitOptions options, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<RateLimitResult> CheckPostAuthSidAsync(string sid, PostAuthSidRateLimitOptions options, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<CostQuotaResult> ReserveCostQuotaAsync(string key, int reservedCost, ClientQuotaPolicy policy, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<CostQuotaResult> SettleCostQuotaAsync(string key, int reservedCost, int actualCost, ClientQuotaPolicy policy, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<CallerBucketState?> GetBucketStateAsync(string key, ClientQuotaPolicy policy, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<CallerBucketState?> GetBucketStateAsync(string key, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
