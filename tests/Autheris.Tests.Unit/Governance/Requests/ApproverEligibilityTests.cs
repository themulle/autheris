using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Security;
using Autheris.Application.Audit;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.ConsentRequests;
using Autheris.Application.ConsentRequests.Routing;
using Autheris.Application.Governance.ConsentRequests;
using Autheris.Application.Governance.Grants;
using Autheris.Application.Governance.Requests;
using Autheris.Application.Security;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Autheris.Tests.Common.Fakes;
using Autheris.Tests.Unit.Security;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance.Requests;

[Trait("Category", "Security")]
public sealed class ApproverEligibilityTests
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly Guid DomainId = Guid.NewGuid();
    private static readonly Sid OwnerGroupSid = new("group:S-1-5-21-OWNER-1");
    private static readonly TableIdentifier OrdersTable = new("sales", "dbo", "orders");

    private static SecurityPrincipalContext ContextFor(string sid, string role, Sid[]? groups = null, GroupResolutionStatus groupStatus = GroupResolutionStatus.Complete)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.PrimarySid, sid),
            new("tenant_id", TenantA.Value),
            new(ClaimTypes.Role, role)
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
        return new SecurityPrincipalContext
        {
            UserSid = new Sid(sid),
            TenantId = TenantA,
            GroupSids = groups?.ToHashSet() ?? new HashSet<Sid>(),
            GroupResolutionStatus = groupStatus,
            TenantRoles = new HashSet<string>([role], StringComparer.OrdinalIgnoreCase),
            ClusterRoles = new HashSet<string>(),
            AuthenticationScheme = "Bearer",
            Principal = principal
        };
    }

    [Fact]
    public async Task It_cannot_decide_data_when_owner_exists()
    {
        var caps = new GovernanceCapabilities();
        var eligibility = new ApproverEligibility(caps);
        var set = new ApproverSet(TenantA, ConsentRequestKind.Data, new ContainerRef(ContainerKind.Domain, DomainId), OwnerGroupSid, OwnerGroupAssigned: true, GovernanceEligible: false, ItAdminEligible: false);

        var itContext = ContextFor("user:it-admin", "TenantAdmin");
        eligibility.Evaluate(itContext, set).ShouldBeNull();
        (await eligibility.EvaluateAsync(itContext, set)).ShouldBeNull();
    }

    [Fact]
    public async Task It_can_decide_catalog()
    {
        var caps = new GovernanceCapabilities();
        var eligibility = new ApproverEligibility(caps);
        var set = new ApproverSet(TenantA, ConsentRequestKind.Catalog, new ContainerRef(ContainerKind.Domain, DomainId), OwnerGroupSid, OwnerGroupAssigned: true, GovernanceEligible: true, ItAdminEligible: true);

        var itContext = ContextFor("user:it-admin", "TenantAdmin");
        eligibility.Evaluate(itContext, set).ShouldBe(ApproverCapacity.ItAdmin);
        (await eligibility.EvaluateAsync(itContext, set)).ShouldBe(ApproverCapacity.ItAdmin);
    }

    [Fact]
    public async Task Capacity_precedence_and_degraded_groups_return_null()
    {
        var domainRepo = NullFake.Create<IGovernanceDomainRepository>((m, _) =>
            m.Name == nameof(IGovernanceDomainRepository.GetAsync)
                ? Task.FromResult<GovernanceDomain?>(new GovernanceDomain(DomainId, TenantA, "Sales", null, null, OwnerGroupSid, ProtectionClass.Confidential, CatalogVisibilityMode.Open, false, 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch))
                : NullFake.UseDefault);

        var caps = new GovernanceCapabilities(domainRepository: domainRepo);
        var eligibility = new ApproverEligibility(caps);
        var set = new ApproverSet(TenantA, ConsentRequestKind.Catalog, new ContainerRef(ContainerKind.Domain, DomainId), OwnerGroupSid, OwnerGroupAssigned: true, GovernanceEligible: true, ItAdminEligible: true);

        var multiRoleOwner = ContextFor("user:alice", "GovernanceAdmin", [OwnerGroupSid]);
        eligibility.Evaluate(multiRoleOwner, set).ShouldBe(ApproverCapacity.DataOwner);
        (await eligibility.EvaluateAsync(multiRoleOwner, set)).ShouldBe(ApproverCapacity.DataOwner);

        var degraded = ContextFor("user:dg-degraded", "GovernanceAdmin", groupStatus: GroupResolutionStatus.Degraded);
        eligibility.Evaluate(degraded, set).ShouldBeNull();
        (await eligibility.EvaluateAsync(degraded, set)).ShouldBeNull();
    }

    [Fact]
    public async Task Eligibility_recomputed_in_transaction()
    {
        var now = DateTimeOffset.Parse("2026-11-22T10:00:00Z");
        var time = new FakeTimeProvider(now);
        var currentOwnerGroup = OwnerGroupSid;

        var domainRepo = NullFake.Create<IGovernanceDomainRepository>((m, _) =>
            m.Name == nameof(IGovernanceDomainRepository.GetAsync)
                ? Task.FromResult<GovernanceDomain?>(new GovernanceDomain(DomainId, TenantA, "Sales", null, null, currentOwnerGroup, ProtectionClass.Confidential, CatalogVisibilityMode.Open, false, 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch))
                : NullFake.UseDefault);

        var protection = new StubProtectionProvider(() => new EffectiveProtection(
            new ContainerRef(ContainerKind.Domain, DomainId),
            currentOwnerGroup,
            ProtectionClass.Confidential,
            ProtectionClass.Confidential,
            null,
            new Dictionary<string, ColumnProtection>(),
            CatalogVisibilityMode.Open));

        var pendingRequest = new ConsentRequest
        {
            Id = Guid.NewGuid(),
            TableId = Guid.NewGuid(),
            TableIdentifier = OrdersTable,
            RequesterSid = new Sid("user:bob"),
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = "user:bob",
            BusinessJustification = "Need data access for quarterly reporting.",
            Status = "PENDING",
            RequestedAt = now.AddHours(-1),
            RequestedValidFrom = now,
            RequestedValidTo = now.AddDays(30),
            TenantId = TenantA,
            Version = 1,
            RequestKind = ConsentRequestKind.Data,
            GrantKind = PermissionGrantKind.Data
        };

        var approvals = NullFake.Create<IConsentApprovalRepository>((m, args) =>
        {
            if (m.Name == nameof(IConsentApprovalRepository.TryDecideAsync))
            {
                var decideFn = (Func<IGovernanceTransaction, ConsentRequest, Task<PermissionDecisionRecord>>)args![2]!;
                return Task.Run(async () =>
                {
                    var tx = NullFake.Create<IGovernanceTransaction>();
                    await decideFn(tx, pendingRequest);
                    return new DecisionCasResult(DecisionCasOutcome.Applied, pendingRequest);
                });
            }

            return NullFake.UseDefault;
        });

        var caps = new GovernanceCapabilities(domainRepository: domainRepo);
        var engine = new PermissionRequestEngine(
            approvals,
            new InMemoryAuditWriter(),
            new ApproverResolver(domainRepository: domainRepo, protectionSnapshotProvider: protection),
            new ApproverEligibility(caps),
            new SeparationOfDutiesService(),
            new GrantPolicy(),
            GrantValidityPolicy.CreateDefault(),
            Array.Empty<IGrantEffectApplier>(),
            protectionProvider: protection,
            timeProvider: time);

        // Decider was removed from owner group before transaction runs: must refuse inside transaction.
        currentOwnerGroup = new Sid("group:S-1-5-21-NEW-OWNER-GROUP");
        var formerOwner = ContextFor("user:former-owner", "Reader", [OwnerGroupSid]);

        var ex = await Should.ThrowAsync<ConsentDecisionException>(() => engine.DecideAsync(
            new PermissionDecisionCommand(pendingRequest.Id, 1, formerOwner, Approve: true)));
        ex.StatusCode.ShouldBe(403);
    }

    [Fact]
    public async Task Route_with_removed_scope_kind_stays_pending()
    {
        var now = DateTimeOffset.Parse("2026-11-22T10:00:00Z");
        var time = new FakeTimeProvider(now);
        var audit = new InMemoryAuditWriter();
        var approvals = NullFake.Create<IConsentApprovalRepository>((m, args) =>
            m.Name == nameof(IConsentApprovalRepository.CreateConsentRequestAsync)
                ? Task.FromResult((ConsentRequest)args![0]!)
                : NullFake.UseDefault);

        var workflowConfig = new RemovedScopeRouteRepo(
        [
            new ConsentWorkflowRoute(TenantA.Value, WorkflowRouteScopeKind.DataOwner, "owner-x", ConsentWorkflowMode.External, Guid.NewGuid(), 30, 14, false, "admin", now),
            new ConsentWorkflowRoute(TenantA.Value, WorkflowRouteScopeKind.DataSource, "sales", ConsentWorkflowMode.External, Guid.NewGuid(), 30, 14, false, "admin", now)
        ]);

        var metadata = NullFake.Create<ITableMetadataRepository>((m, _) =>
            m.Name == nameof(ITableMetadataRepository.GetTableMetadataAsync)
                ? Task.FromResult<TableMetadata?>(new TableMetadata
                {
                    Identifier = OrdersTable,
                    Table = new Table { Id = Guid.NewGuid(), SourceName = "sales", SchemaName = "dbo", TableName = "orders", IsActive = true }
                })
                : NullFake.UseDefault);

        var engine = new PermissionRequestEngine(
            approvals,
            audit,
            new ApproverResolver(),
            new ApproverEligibility(new GovernanceCapabilities()),
            new SeparationOfDutiesService(),
            new GrantPolicy(),
            GrantValidityPolicy.CreateDefault(),
            Array.Empty<IGrantEffectApplier>(),
            metadataRepository: metadata,
            workflowConfigRepository: workflowConfig,
            timeProvider: time);

        var caller = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "user:alice"), new Claim("tenant_id", TenantA.Value)], "Bearer"));
        var res = await engine.SubmitAsync(new PermissionRequestSubmission(
            caller,
            TenantA,
            ConsentRequestKind.Data,
            PermissionTarget.ForTable(OrdersTable),
            GranteeType.User,
            "user:alice",
            "Business need for quarterly order reconciliation.",
            ValidTo: now.AddDays(30)));

        res.Request.Status.ShouldBe("PENDING");
        res.Request.WorkflowMode.ShouldBe(ConsentWorkflowMode.Internal);
        res.Request.ExternalSystemId.ShouldBeNull();
    }

    private sealed class StubProtectionProvider(Func<EffectiveProtection> get) : IProtectionSnapshotProvider
    {
        public ValueTask<EffectiveProtection> GetAsync(TenantId tenant, TableIdentifier table, CancellationToken ct = default) =>
            ValueTask.FromResult(get());
    }

    private sealed class RemovedScopeRouteRepo(IReadOnlyList<ConsentWorkflowRoute> routes) : IConsentWorkflowConfigRepository, IConsentWorkflowAdminRepository
    {
        public Task<ConsentWorkflowRoute?> ResolveWorkflowRouteAsync(TenantId tenantId, TableIdentifier table, CancellationToken ct = default) =>
            Task.FromResult<ConsentWorkflowRoute?>(null);
        public Task<ExternalWorkflowSystem?> GetExternalWorkflowSystemAsync(TenantId tenantId, Guid systemId, CancellationToken ct = default) =>
            Task.FromResult<ExternalWorkflowSystem?>(null);
        public Task<ConsentWorkflowTableInputs> GetRoutingInputsForTableAsync(TenantId tenantId, TableIdentifier table, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<ConsentWorkflowRoute>> ListWorkflowRoutesAsync(TenantId tenantId, CancellationToken ct = default) =>
            Task.FromResult(routes);
        public Task<ConsentWorkflowRoute?> GetWorkflowRouteAsync(TenantId tenantId, WorkflowRouteScopeKind scopeKind, string scopeRef, CancellationToken ct = default) =>
            Task.FromResult<ConsentWorkflowRoute?>(null);
        public Task<ConsentWorkflowRoute> UpsertWorkflowRouteAsync(ConsentWorkflowRoute route, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<bool> DeleteWorkflowRouteAsync(TenantId tenantId, WorkflowRouteScopeKind scopeKind, string scopeRef, CancellationToken ct = default) =>
            Task.FromResult(false);
        public Task<ExternalWorkflowSystem> UpsertExternalWorkflowSystemAsync(ExternalWorkflowSystem system, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<(ExternalWorkflowSystem? System, ConsentRerouteResult RerouteResult)> DisableExternalWorkflowSystemAsync(TenantId tenantId, Guid systemId, string actorSid, DateTimeOffset disabledAt, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<ConsentRerouteResult> RerouteOpenRequestsAsync(TenantId tenantId, Guid? fromSystemId, ConsentWorkflowMode? fromMode, ConsentWorkflowMode targetMode, Guid? targetSystemId, string actorSid, DateTimeOffset occurredAt, IReadOnlyList<Guid>? requestIds = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
