namespace Autheris.Tests.Integration.Governance;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.ConsentRequests;
using Autheris.Application.ConsentRequests.Events;
using Autheris.Application.ConsentRequests.Routing;
using Autheris.Application.Governance.Grants;
using Autheris.Application.Governance.Grants.Records;
using Autheris.Application.Webhooks;
using Autheris.Domain.Common;
using Autheris.Domain.Grants;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Tests.Common;
using Autheris.Tests.Integration.Governance.Contract;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

[Collection(GovernancePostgresCollection.Name)]
public sealed class GrantRecordLifecycleE2ETests(GovernancePostgresFixture fixture)
{
    private readonly GovernancePostgresFixture _fixture = fixture;

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task FullExternalWorkflow_Issuance_Export_Verify_RevocationWebhook_AndExpirySweep_E2E()
    {
        await using var store = await GovernanceTestStore.CreatePostgreSqlAsync(
            _fixture,
            "grant_record_lifecycle_e2e");
        if (!store.IsAvailable)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var tenant = store.TenantA;
        var requesterSid = new Sid("S-1-5-21-1000-1000-1000-2001");
        var approverSid = new Sid("S-1-5-21-1000-1000-1000-3001");
        var owner1Sid = new Sid("S-1-5-21-1000-1000-1000-4001");
        var owner2Sid = new Sid("S-1-5-21-1000-1000-1000-4002");

        var tableId = await SeedTableAndOwnersAsync(store, owner1Sid, owner2Sid);

        // 1. Register External Workflow System + Tenant External Route
        var externalSystemId = Guid.NewGuid();
        await store.ConsentWorkflowAdmin.UpsertExternalWorkflowSystemAsync(new ExternalWorkflowSystem(
            externalSystemId,
            tenant.Value,
            "ServiceNow-E2E",
            "sn-e2e-client",
            "https://workflow.example.invalid/events",
            "plain-e2e-webhook-secret",
            Enabled: true,
            CreatedBy: "admin-e2e",
            CreatedAt: now,
            DisabledAt: null));

        await store.ConsentWorkflowAdmin.UpsertWorkflowRouteAsync(new ConsentWorkflowRoute(
            tenant.Value,
            WorkflowRouteScopeKind.Tenant,
            "*",
            ConsentWorkflowMode.External,
            externalSystemId,
            MaxDurationDays: 30,
            RequestTimeoutDays: 14,
            ExpiryNoticeLeadHours: 24,
            // The lifecycle test has no SCIM directory; approver resolution is covered by the ExternalDecisionGuard tests.
            RequireResolvableApprover: false,
            UpdatedBy: "admin-e2e",
            UpdatedAt: now));

        // 2. Submit consent request -> PENDING_EXTERNAL_APPROVAL + consent.request.created event
        var requestService = store.Services.GetRequiredService<IConsentRequestService>();
        var requesterPrincipal = BuildPrincipal(requesterSid.Value, tenant, "DataConsumer");
        var submission = await requestService.SubmitAsync(new ConsentRequestSubmission(
            tableId.Domain,
            tableId.Schema,
            tableId.TableName,
            "Need customer order totals for quarterly compliance reporting.",
            DurationDays: 10,
            IdempotencyKey: "e2e-req-1",
            UserSid: requesterSid,
            Principal: requesterPrincipal,
            ContextTenant: tenant));

        submission.Status.Should().Be("PENDING_EXTERNAL_APPROVAL");

        // 3. Approve via external decision pipeline -> Consent activated + signed ISSUED record
        var decisionPipeline = store.Services.GetRequiredService<IConsentDecisionPipeline>();
        var approved = await decisionPipeline.DecideAsync(new ConsentDecisionCommand(
            submission.RequestId,
            ConsentDecisionKind.Approve,
            ConsentDecisionChannel.External,
            Principal: null,
            ExternalAttribution: new ExternalDecisionAttribution(
                externalSystemId,
                "SNOW-CHG-1001",
                "APPROVED",
                new AssertedApprover("approver@corp.local", ResolvedSid: approverSid.Value),
                now)));

        approved.ActivatedConsent.Should().NotBeNull();
        var grantId = approved.ActivatedConsent!.Id;

        // Seed correlated data-access audit log entry for grantId
        await store.AuditLogs.RecordAuditEventAsync(new AuditLogEntry
        {
            Id = Guid.NewGuid(),
            TenantId = tenant,
            OccurredAt = now.AddMinutes(1),
            EventType = "DATA_READ",
            ActorSid = requesterSid,
            TargetTable = tableId.ToString(),
            Decision = "ALLOW",
            TraceId = "trace-e2e-read-1",
            DetailsJson = JsonSerializer.Serialize(new
            {
                grants = new[]
                {
                    new
                    {
                        grantId = grantId.ToString("D"),
                        grantValidFrom = approved.ActivatedConsent.ValidFrom,
                        grantValidUntil = approved.ActivatedConsent.ValidTo
                    }
                }
            })
        });

        // 4. Verify via endpoint handler & service -> signed ISSUED record + correlated audit entry
        var exportService = store.Services.GetRequiredService<IGrantRecordExportService>();
        var verifyCtx = BuildHttpContext(tenant, "Auditor");
        var verifyHttp = await ConsentWorkflowAdminEndpoints.VerifyGrantRecordsAsync(
            new GrantRecordVerifyRequest(GrantId: grantId),
            verifyCtx,
            exportService);
        await verifyHttp.ExecuteAsync(verifyCtx);
        verifyCtx.Response.StatusCode.Should().Be(StatusCodes.Status200OK);

        var initialBundle = await exportService.ExportForGrantAsync(tenant, grantId);
        initialBundle.Should().NotBeNull();
        initialBundle!.ChainVerified.Should().BeTrue();
        initialBundle.Records.Should().ContainSingle(r => r.RecordType == GrantRecordType.Issued);
        initialBundle.CorrelatedAuditEntries.Should().ContainSingle(e => e.TraceId == "trace-e2e-read-1");

        // 5. Revoke consent -> REVOKED ledger record + grant.revoked outbox webhook event
        await store.Consents.RevokeConsentAsync(
            grantId,
            approverSid,
            "Access no longer needed after compliance audit.",
            ConsentRevocationAuthority.FromPrincipal(BuildPrincipal(approverSid.Value, tenant, "GovernanceAdmin")));

        var webhookHandler = new RecordingWebhookHandler();
        var dispatcher = BuildDispatcher(store, webhookHandler);
        await dispatcher.RunOnceAsync("e2e-worker", CancellationToken.None);

        webhookHandler.DeliveredEventTypes.Should().Contain(ConsentEventTypes.RequestCreated);
        webhookHandler.DeliveredEventTypes.Should().Contain(ConsentEventTypes.GrantRevoked);

        // 6. Second grant expires -> GrantExpiryHostedService sweep records EXPIRED ledger row + grant.expired event
        var expiringSubmission = await requestService.SubmitAsync(new ConsentRequestSubmission(
            tableId.Domain,
            tableId.Schema,
            tableId.TableName,
            "Short-lived read request for expiry sweep verification.",
            DurationDays: 1,
            IdempotencyKey: "e2e-req-2",
            UserSid: requesterSid,
            Principal: requesterPrincipal,
            ContextTenant: tenant));

        var expiringApproved = await decisionPipeline.DecideAsync(new ConsentDecisionCommand(
            expiringSubmission.RequestId,
            ConsentDecisionKind.Approve,
            ConsentDecisionChannel.External,
            Principal: null,
            ExternalAttribution: new ExternalDecisionAttribution(
                externalSystemId,
                "SNOW-CHG-1002",
                "APPROVED",
                new AssertedApprover("approver@corp.local", ResolvedSid: approverSid.Value),
                now)));
        var expiringGrantId = expiringApproved.ActivatedConsent!.Id;

        var futureClock = new FakeTimeProvider(now.AddDays(5));
        var workflowRouter = store.Services.GetRequiredService<IConsentWorkflowRouter>();
        var expiryService = new GrantExpiryHostedService(
            store.GrantExpiry,
            workflowRouter,
            NullLogger<GrantExpiryHostedService>.Instance,
            timeProvider: futureClock);
        var sweepSummary = await expiryService.RunSweepAsync(CancellationToken.None);
        sweepSummary.GrantsExpired.Should().BeGreaterOrEqualTo(1);

        // 7. Re-verify full tenant chain, export revoked + expired bundles, and confirm audit event feed
        var tenantChainResult = await exportService.VerifyAsync(tenant, new GrantRecordVerifyRequest());
        tenantChainResult.Should().NotBeNull();
        tenantChainResult!.IsValid.Should().BeTrue();
        tenantChainResult.VerifiedCount.Should().Be(4);

        var revokedExport = await exportService.ExportForGrantAsync(tenant, grantId);
        revokedExport!.Records.Select(r => r.RecordType).Should().Equal(
            GrantRecordType.Issued,
            GrantRecordType.Revoked);
        revokedExport.ChainVerified.Should().BeTrue();

        var expiredExport = await exportService.ExportForGrantAsync(tenant, expiringGrantId);
        expiredExport!.Records.Select(r => r.RecordType).Should().Equal(
            GrantRecordType.Issued,
            GrantRecordType.Expired);
        expiredExport.ChainVerified.Should().BeTrue();

        var feed = await store.ConsentEventOutbox.ReadFeedAsync(tenant, 0, 50);
        feed.Events.Select(e => e.Type).Should()
            .Contain(ConsentEventTypes.RequestCreated)
            .And.Contain(ConsentEventTypes.GrantRevoked)
            .And.Contain(ConsentEventTypes.GrantExpired);
    }

    private static async Task<TableIdentifier> SeedTableAndOwnersAsync(
        GovernanceTestStore store,
        Sid owner1Sid,
        Sid owner2Sid)
    {
        var tableId = new TableIdentifier("sales", "dbo", "orders_e2e_" + Guid.NewGuid().ToString("N")[..8]);
        await store.TableMetadata.UpsertTableMetadataAsync(new TableMetadata
        {
            Identifier = tableId,
            ReplaceKeyDefinition = true,
            Table = new Table
            {
                Id = Guid.NewGuid(),
                SourceName = tableId.Domain,
                SchemaName = tableId.Schema,
                TableName = tableId.TableName,
                Description = "E2E grant lifecycle test table",
                Sensitivity = "NORMAL",
                IsActive = true
            },
            Columns =
            [
                new TableColumn { Id = Guid.NewGuid(), ColumnName = "id", DataType = "int" },
                new TableColumn { Id = Guid.NewGuid(), ColumnName = "total", DataType = "decimal" }
            ],
            PrimaryKeyColumns = ["id"]
        });

        await store.DataOwnerAssignments.ApplyTableOwnerAssignmentsAsync(
            new DataOwnerAssignmentRequest(
                [
                    new TableOwnerAssignmentSpec(
                        tableId,
                        [
                            new DataOwnerSpec(owner1Sid.Value, "owner1", "Owner One", "owner1@example.com", "PRIMARY"),
                            new DataOwnerSpec(owner2Sid.Value, "owner2", "Owner Two", "owner2@example.com", "DELEGATE")
                        ])
                ],
                Replace: true,
                DryRun: false));

        return tableId;
    }

    private static ConsentWebhookDispatcherHostedService BuildDispatcher(
        GovernanceTestStore store,
        RecordingWebhookHandler handler)
    {
        var gatewayOptions = Options.Create(new GatewayOptions());
        var retryPolicy = new WebhookRetryPolicy(gatewayOptions.Value.Webhooks, TimeProvider.System);
        var client = new WebhookDeliveryClient(
            new HttpClient(handler),
            gatewayOptions,
            retryPolicy,
            NullLogger<WebhookDeliveryClient>.Instance,
            environment: null,
            timeProvider: TimeProvider.System,
            egressValidator: (_, _, _) => Task.CompletedTask);

        return new ConsentWebhookDispatcherHostedService(
            store.ConsentEventOutbox,
            store.ConsentWorkflowConfig,
            client,
            retryPolicy,
            Options.Create(new ConsentEventsOptions()),
            NullLogger<ConsentWebhookDispatcherHostedService>.Instance,
            secretProtector: null,
            timeProvider: TimeProvider.System);
    }

    private static ClaimsPrincipal BuildPrincipal(string sid, TenantId tenant, string role) =>
        new(new ClaimsIdentity(
        [
            new Claim("sub", sid),
            new Claim("tenant_id", tenant.Value),
            new Claim(ClaimTypes.Role, role)
        ], "Test"));

    private static DefaultHttpContext BuildHttpContext(TenantId tenant, string role)
    {
        var ctx = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
            User = BuildPrincipal("S-1-5-21-auditor", tenant, role)
        };
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private sealed class RecordingWebhookHandler : HttpMessageHandler
    {
        public List<string> DeliveredEventTypes { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Headers.TryGetValues("X-Autheris-Event-Type", out var values))
            {
                DeliveredEventTypes.AddRange(values);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
