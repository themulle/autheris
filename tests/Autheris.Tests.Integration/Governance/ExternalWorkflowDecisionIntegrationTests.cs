namespace Autheris.Tests.Integration.Governance;

using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Autheris.Application.ConsentRequests;
using Autheris.Application.ConsentRequests.Routing;
using Autheris.Application.Governance.Requests;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Tests.Common;
using Autheris.Tests.Integration.Governance.Contract;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Xunit;

/// <summary>External decisions against the real persistence: forged evidence decides nothing and leaves the request pending.</summary>
[Collection(GovernancePostgresCollection.Name)]
public sealed class ExternalWorkflowDecisionIntegrationTests(GovernancePostgresFixture fixture)
{
    private const string Secret = "integration-evidence-secret";

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task Forged_decision_rejected()
    {
        await using var store = await GovernanceTestStore.CreatePostgreSqlAsync(fixture, "external_forged_decision");
        if (!store.IsAvailable)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var tenant = store.TenantA;
        var table = new TableIdentifier("sales", "dbo", "orders_forged_" + Guid.NewGuid().ToString("N")[..8]);
        await store.TableMetadata.UpsertTableMetadataAsync(new TableMetadata
        {
            Identifier = table,
            ReplaceKeyDefinition = true,
            Table = new Table { Id = Guid.NewGuid(), SourceName = table.Domain, SchemaName = table.Schema, TableName = table.TableName, IsActive = true },
            Columns = [new TableColumn { Id = Guid.NewGuid(), ColumnName = "id", DataType = "int" }],
            PrimaryKeyColumns = ["id"]
        });

        var systemId = Guid.NewGuid();
        await store.ConsentWorkflowAdmin.UpsertExternalWorkflowSystemAsync(new ExternalWorkflowSystem(
            systemId, tenant.Value, "ServiceNow-forged", "sn-forged-client", "https://workflow.example.invalid/events",
            Secret, Enabled: true, CreatedBy: "admin", CreatedAt: now, DisabledAt: null));

        var request = await store.ConsentApprovals.CreateConsentRequestAsync(new ConsentRequest
        {
            Id = Guid.NewGuid(),
            TableIdentifier = table,
            RequesterSid = new Sid("S-1-5-21-1000-1000-1000-2001"),
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = "S-1-5-21-1000-1000-1000-2001",
            BusinessJustification = "Quarterly compliance reporting of order totals.",
            RequestedValidTo = now.AddDays(5),
            WorkflowMode = ConsentWorkflowMode.External,
            ExternalSystemId = systemId,
            ExternalReference = "SNOW-CHG-FORGED",
            TenantId = tenant
        });

        var engine = Substitute.For<IPermissionRequestEngine>();
        var guard = new ExternalDecisionGuard(
            approverResolver: null,
            secretProtector: null,
            replayStore: new InMemoryEvidenceReplayStore(),
            timeProvider: new FakeTimeProvider(now),
            allowPlaintextSecret: true);
        var service = new ExternalDecisionService(
            engine, guard, store.ConsentApprovals, store.ConsentWorkflowConfig, new ConsentWorkflowRouter(store.AuditLogs, store.ConsentWorkflowConfig, store.ConsentWorkflowAdmin));

        var envelope = new ExternalDecisionEnvelope(
            request.Id, request.Version <= 0 ? 1 : request.Version, systemId, "SNOW-CHG-FORGED", true,
            new AssertedApprover("approver@corp.example"), now, "nonce-forged", string.Empty, null,
            new ClaimsPrincipal(new ClaimsIdentity([new Claim("azp", "sn-forged-client")], "OAuth")));
        var forged = envelope with { Signature = ExternalDecisionGuard.ComputeSignature("not-the-shared-secret", envelope) };

        var act = () => service.DecideAsync(forged, tenant);

        (await act.Should().ThrowAsync<ConsentDecisionException>()).Which.ErrorCode.Should().Be(ExternalDecisionGuard.InvalidEvidenceCode);
        await engine.DidNotReceiveWithAnyArgs().DecideAsync(default!, default);
        var reloaded = await store.ConsentApprovals.GetConsentRequestAsync(request.Id);
        reloaded!.Status.StartsWith("PENDING", StringComparison.Ordinal).Should().BeTrue();
    }

    private sealed class InMemoryEvidenceReplayStore : Autheris.Application.Common.Interfaces.IIdempotencyStore
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> _items = new();

        public Task<T?> GetAsync<T>(string key, System.Threading.CancellationToken ct = default) where T : class =>
            Task.FromResult(_items.TryGetValue(key, out var v) ? v as T : null);

        public Task<bool> SetIfNotExistsAsync<T>(string key, T value, TimeSpan ttl, System.Threading.CancellationToken ct = default) where T : class =>
            Task.FromResult(_items.TryAdd(key, value));
    }
}
