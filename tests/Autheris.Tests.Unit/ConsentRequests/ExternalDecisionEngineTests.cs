using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.ConsentRequests;
using Autheris.Application.ConsentRequests.Routing;
using Autheris.Application.Governance.ConsentRequests;
using Autheris.Application.Governance.Grants;
using Autheris.Application.Governance.Grants.Records;
using Autheris.Application.Governance.Requests;
using Autheris.Application.Security;
using Autheris.Domain.Common;
using Autheris.Domain.Grants;
using Autheris.Domain.Identity;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Autheris.Domain.Tenancy;
using Autheris.Tests.Common.Fakes;
using Autheris.Tests.Unit.Security;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.ConsentRequests;

/// <summary>External decisions: evidence is validated, then the engine decides as the realm-mapped person.</summary>
[Trait("Category", "Security")]
public sealed class ExternalDecisionEngineTests
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly TableIdentifier OrdersTable = new("sales", "dbo", "orders");
    private static readonly Guid SystemId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-22T12:00:00Z");
    private const string Secret = "shared-evidence-secret";
    private const string Requester = "user:requester";

    private static SecurityPrincipalContext Person(string sid, params string[] roles) => new()
    {
        UserSid = new Sid(sid),
        TenantId = TenantA,
        GroupSids = new HashSet<Sid>(),
        GroupResolutionStatus = GroupResolutionStatus.Complete,
        TenantRoles = new HashSet<string>(roles, StringComparer.OrdinalIgnoreCase),
        ClusterRoles = new HashSet<string>(),
        AuthenticationScheme = "ExternalWorkflow",
        Principal = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.PrimarySid, sid), new Claim("tenant_id", TenantA.Value) }
                .Concat(roles.Select(r => new Claim(ClaimTypes.Role, r))), "ExternalWorkflow"))
    };

    private static ConsentWorkflowRouteResolution DomainRoute(Guid systemId) =>
        new(ConsentWorkflowMode.External, systemId, 60, 30, true, WorkflowRouteScopeKind.Domain);

    private static Harness Create(
        Dictionary<string, SecurityPrincipalContext> directory,
        bool withReplayStore = true,
        bool withSecret = true,
        ConsentWorkflowRouteResolution? route = null)
    {
        var req = new ConsentRequest
        {
            Id = Guid.NewGuid(),
            TableId = Guid.NewGuid(),
            TableIdentifier = OrdersTable,
            RequesterSid = new Sid(Requester),
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = Requester,
            BusinessJustification = "Need order table access for finance close.",
            Status = "PENDING",
            RequestedAt = Now.AddMinutes(-10),
            RequestedValidFrom = Now,
            RequestedValidTo = Now.AddDays(30),
            TenantId = TenantA,
            Version = 1,
            RequestKind = ConsentRequestKind.Data,
            GrantKind = PermissionGrantKind.Data,
            WorkflowMode = ConsentWorkflowMode.External,
            ExternalSystemId = SystemId,
            ExternalReference = "CHG-001"
        };

        var repo = new InMemoryCasApprovalRepo(req);
        var consents = new List<Consent>();
        var grantWriter = NullFake.Create<IGrantWriter>((m, args) =>
        {
            if (m.Name == nameof(IGrantWriter.InsertAndGrantAsync))
            {
                var c = (Consent)args![1]!;
                consents.Add(c);
                return Task.FromResult(new GrantRecord(c.TenantId.Value, consents.Count, Guid.NewGuid(), GrantRecordType.Issued, c.Id, c.ConsentRequestId,
                    c.GrantSource, null, c.GranteeType.ToString(), c.GranteeSid!.Value.Value, OrdersTable.ToString(), "{}", c.ValidFrom, c.ValidTo, Requester, "{}", Now, Now, GrantRecordChain.GenesisHash));
            }

            return NullFake.UseDefault;
        });

        var time = new FakeTimeProvider(Now);
        var engine = new PermissionRequestEngine(
            repo,
            new InMemoryAuditWriter(),
            new ApproverResolver(),
            new ApproverEligibility(new GovernanceCapabilities()),
            new SeparationOfDutiesService(),
            new GrantPolicy(),
            GrantValidityPolicy.CreateDefault(),
            [new DataGrantApplier(grantWriter)],
            timeProvider: time);

        var system = new ExternalWorkflowSystem(SystemId, TenantA.Value, "ServiceNow", "client-snow", "https://snow.local/cb", withSecret ? Secret : string.Empty, true, "admin", Now, null);
        var config = NullFake.Create<IConsentWorkflowConfigRepository>((m, _) =>
            m.Name == nameof(IConsentWorkflowConfigRepository.GetExternalWorkflowSystemAsync)
                ? Task.FromResult<ExternalWorkflowSystem?>(system)
                : NullFake.UseDefault);

        var guard = new ExternalDecisionGuard(new MapResolver(directory), null, withReplayStore ? new MemoryReplayStore() : null, time, allowPlaintextSecret: true);
        var service = new ExternalDecisionService(engine, guard, repo, config, new StubRouter(route ?? DomainRoute(SystemId)));

        ExternalDecisionEnvelope Sign(ExternalDecisionEnvelope e) => e with { Signature = ExternalDecisionGuard.ComputeSignature(Secret, e) };
        return new Harness(engine, service, repo, consents, Sign);
    }

    private static ExternalDecisionEnvelope Envelope(Harness h, string approver, bool approve = true, string nonce = "n-1", string? reason = null) =>
        new(h.Repo.Current.Id, 1, SystemId, "CHG-001", approve, new AssertedApprover(approver), Now, nonce, string.Empty, reason,
            new ClaimsPrincipal(new ClaimsIdentity([new Claim("azp", "client-snow")], "OAuth")));

    private static Task<PermissionDecisionResult> DecideAsync(Harness h, ExternalDecisionEnvelope e) =>
        h.Service.DecideAsync(e, TenantA);

    [Fact]
    public async Task Mapped_eligible_approver_decides_through_engine()
    {
        var h = Create(new() { ["alice"] = Person("user:alice", "GovernanceAdmin") });

        var result = await DecideAsync(h, h.Sign(Envelope(h, "alice")));

        result.Request.Status.ShouldBe("APPROVED");
        result.Request.DecidedBy.ShouldBe(new Sid("user:alice"));
        h.Consents.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Bad_signature_rejected()
    {
        var h = Create(new() { ["alice"] = Person("user:alice", "GovernanceAdmin") });
        var forged = Envelope(h, "alice") with { Signature = ExternalDecisionGuard.ComputeSignature("another-secret", Envelope(h, "alice")) };

        var ex = await Should.ThrowAsync<ConsentDecisionException>(() => DecideAsync(h, forged));

        ex.ErrorCode.ShouldBe(ExternalDecisionGuard.InvalidEvidenceCode);
        h.Repo.Current.Status.ShouldBe("PENDING");
        h.Consents.ShouldBeEmpty();

        // A signature made for another request version does not carry over either.
        var otherVersion = h.Sign(Envelope(h, "alice") with { ExpectedVersion = 2 });
        var tampered = otherVersion with { ExpectedVersion = 1 };
        (await Should.ThrowAsync<ConsentDecisionException>(() => DecideAsync(h, tampered))).ErrorCode.ShouldBe(ExternalDecisionGuard.InvalidEvidenceCode);
    }

    [Fact]
    public async Task Replay_rejected()
    {
        var h = Create(new() { ["alice"] = Person("user:alice", "GovernanceAdmin") });
        var signed = h.Sign(Envelope(h, "alice", approve: false, reason: "Not needed for this business case."));

        (await DecideAsync(h, signed)).Request.Status.ShouldBe("REJECTED");
        var ex = await Should.ThrowAsync<ConsentDecisionException>(() => DecideAsync(h, signed));

        ex.ErrorCode.ShouldBe(ExternalDecisionGuard.ReplayCode);
        ex.StatusCode.ShouldBe(409);
    }

    [Fact]
    public async Task Stale_timestamp_rejected()
    {
        var h = Create(new() { ["alice"] = Person("user:alice", "GovernanceAdmin") });
        var old = h.Sign(Envelope(h, "alice") with { Timestamp = Now.AddHours(-1) });

        (await Should.ThrowAsync<ConsentDecisionException>(() => DecideAsync(h, old))).ErrorCode.ShouldBe(ExternalDecisionGuard.InvalidEvidenceCode);
        h.Repo.Current.Status.ShouldBe("PENDING");
    }

    [Fact]
    public async Task Unmapped_identity_pending()
    {
        var h = Create(new());

        var ex = await Should.ThrowAsync<ConsentDecisionException>(() => DecideAsync(h, h.Sign(Envelope(h, "nobody@corp.example"))));

        ex.ErrorCode.ShouldBe(ConsentDecisionErrorCodes.ApproverUnresolved);
        h.Repo.Current.Status.ShouldBe("PENDING");
        h.Consents.ShouldBeEmpty();
    }

    [Fact]
    public async Task Ineligible_external_approver_rejected()
    {
        var h = Create(new() { ["bob"] = Person("user:bob") });

        var ex = await Should.ThrowAsync<ConsentDecisionException>(() => DecideAsync(h, h.Sign(Envelope(h, "bob"))));

        ex.StatusCode.ShouldBe(403);
        h.Repo.Current.Status.ShouldBe("PENDING");
        h.Consents.ShouldBeEmpty();
    }

    [Fact]
    public async Task Sod_applies()
    {
        // The requester is mapped and even holds the governance role: separation of duties still denies.
        var h = Create(new() { ["requester"] = Person(Requester, "GovernanceAdmin") });

        var ex = await Should.ThrowAsync<ConsentDecisionException>(() => DecideAsync(h, h.Sign(Envelope(h, "requester"))));

        ex.ErrorCode.ShouldBe("SOD_VIOLATION");
        h.Repo.Current.Status.ShouldBe("PENDING");
    }

    [Fact]
    public async Task Capacity_claim_ignored()
    {
        // The envelope has no field for capacity, validity or grant shape; the engine derives them.
        typeof(ExternalDecisionEnvelope).GetProperties().ShouldNotContain(
            p => p.Name.Contains("Capacity", StringComparison.OrdinalIgnoreCase)
                || p.Name.Contains("Valid", StringComparison.OrdinalIgnoreCase)
                || p.Name.Contains("SelfGrant", StringComparison.OrdinalIgnoreCase));

        var h = Create(new() { ["alice"] = Person("user:alice", "GovernanceAdmin") });
        var result = await DecideAsync(h, h.Sign(Envelope(h, "alice")));

        result.Request.ApproverCapacity.ShouldBe(ApproverCapacity.Governance);
        result.Request.RequestedValidTo.ShouldBe(Now.AddDays(30));
    }

    [Fact]
    public async Task First_wins_vs_internal()
    {
        var h = Create(new() { ["alice"] = Person("user:alice", "GovernanceAdmin") });
        var signed = h.Sign(Envelope(h, "alice"));

        var internalResult = await h.Engine.DecideAsync(new PermissionDecisionCommand(h.Repo.Current.Id, 1, Person("user:carol", "GovernanceAdmin"), Approve: true));
        internalResult.Request.Status.ShouldBe("APPROVED");

        var ex = await Should.ThrowAsync<ConsentDecisionException>(() => DecideAsync(h, signed));

        ex.StatusCode.ShouldBe(409);
        ex.ErrorCode.ShouldBe("REQUEST_ALREADY_DECIDED");
        h.Consents.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Unavailable_transport_no_fallback_approval()
    {
        // Replay store or secret unavailable: the evidence cannot be verified, so nothing is decided and no other path approves.
        var noReplayStore = Create(new() { ["alice"] = Person("user:alice", "GovernanceAdmin") }, withReplayStore: false);
        var ex1 = await Should.ThrowAsync<ConsentDecisionException>(() => DecideAsync(noReplayStore, noReplayStore.Sign(Envelope(noReplayStore, "alice"))));
        ex1.StatusCode.ShouldBe(403);
        noReplayStore.Repo.Current.Status.ShouldBe("PENDING");
        noReplayStore.Consents.ShouldBeEmpty();

        var noSecret = Create(new() { ["alice"] = Person("user:alice", "GovernanceAdmin") }, withSecret: false);
        var ex2 = await Should.ThrowAsync<ConsentDecisionException>(() => DecideAsync(noSecret, noSecret.Sign(Envelope(noSecret, "alice"))));
        ex2.StatusCode.ShouldBe(403);
        noSecret.Repo.Current.Status.ShouldBe("PENDING");
        noSecret.Consents.ShouldBeEmpty();
    }

    [Fact]
    public async Task Domain_routed_request_is_decided_by_its_external_system()
    {
        // The effective route is the domain route (not only the tenant route); its system is the request's system.
        var h = Create(new() { ["alice"] = Person("user:alice", "GovernanceAdmin") }, route: DomainRoute(SystemId));

        (await DecideAsync(h, h.Sign(Envelope(h, "alice")))).Request.Status.ShouldBe("APPROVED");

        // A domain route that now names another system refuses the decision.
        var other = Create(new() { ["alice"] = Person("user:alice", "GovernanceAdmin") }, route: DomainRoute(Guid.NewGuid()));
        var ex = await Should.ThrowAsync<ConsentDecisionException>(() => DecideAsync(other, other.Sign(Envelope(other, "alice"))));
        ex.ErrorCode.ShouldBe(ConsentDecisionErrorCodes.ExternalSystemNotAuthorized);
        other.Repo.Current.Status.ShouldBe("PENDING");
    }

    private sealed class DummyDbTransaction : DbTransaction
    {
        public override System.Data.IsolationLevel IsolationLevel => System.Data.IsolationLevel.ReadCommitted;
        protected override DbConnection? DbConnection => null;
        public override void Commit() { }
        public override void Rollback() { }
    }

    private sealed class InMemoryCasApprovalRepo(ConsentRequest initial) : IConsentApprovalRepository
    {
        public ConsentRequest Current { get; private set; } = initial;

        public Task<ConsentRequest> CreateConsentRequestAsync(ConsentRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<ConsentRequest?> GetConsentRequestAsync(Guid requestId, CancellationToken ct = default) => Task.FromResult<ConsentRequest?>(Current);
        public Task<IReadOnlyList<ConsentRequest>> GetPendingRequestsForApproverAsync(Sid approverSid, TenantId tenantId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ConsentRequest>>([]);
        public Task<ConsentRequest> RejectConsentRequestAsync(Guid requestId, Sid approverSid, string reason, CancellationToken ct = default) => throw new NotImplementedException();
        public Task ActivateConsentAsync(Guid requestId, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteConsentRequestAsync(Guid requestId, CancellationToken ct = default) => Task.CompletedTask;

        public async Task<DecisionCasResult> TryDecideAsync(
            Guid requestId,
            int expectedVersion,
            Func<IGovernanceTransaction, ConsentRequest, Task<PermissionDecisionRecord>> decideInTransaction,
            Func<IGovernanceTransaction, PermissionDecisionRecord, ConsentRequest, Task<IReadOnlyList<GrantRef>>> applyEffect,
            CancellationToken ct = default)
        {
            if (Current.Id != requestId)
            {
                return new DecisionCasResult(DecisionCasOutcome.NotFound);
            }

            if (!string.Equals(Current.Status, "PENDING", StringComparison.OrdinalIgnoreCase) || Current.Version != expectedVersion)
            {
                return new DecisionCasResult(DecisionCasOutcome.AlreadyDecided, Current);
            }

            using var rawTx = new DummyDbTransaction();
            var tx = NullFake.Create<IGovernanceTransaction>((m, _) =>
                m.Name == "get_" + nameof(IGovernanceTransaction.Transaction) ? rawTx : NullFake.UseDefault);
            var decision = await decideInTransaction(tx, Current);

            var updated = new ConsentRequest
            {
                Id = Current.Id,
                TableId = Current.TableId,
                TableIdentifier = Current.TableIdentifier,
                RequesterSid = Current.RequesterSid,
                RequestedGranteeType = Current.RequestedGranteeType,
                RequestedGranteeRef = Current.RequestedGranteeRef,
                BusinessJustification = Current.BusinessJustification,
                Status = decision.NewStatus,
                RequestedAt = Current.RequestedAt,
                RequestedValidFrom = Current.RequestedValidFrom,
                RequestedValidTo = decision.ApprovedValidTo,
                UpdatedAt = decision.DecidedAt,
                TenantId = Current.TenantId,
                Version = Current.Version + 1,
                RequestKind = Current.RequestKind,
                GrantKind = Current.GrantKind,
                WorkflowMode = Current.WorkflowMode,
                ExternalSystemId = Current.ExternalSystemId,
                ExternalReference = Current.ExternalReference,
                ApproverCapacity = decision.Capacity,
                SelfGrant = decision.SelfGrant,
                DecidedBy = decision.DecidedBy,
                DecidedAt = decision.DecidedAt,
                ApprovalSteps =
                [
                    new ApprovalStep { ConsentRequestId = Current.Id, StepNumber = 1, ApproverSid = decision.DecidedBy, Decision = decision.StepDecision, DecidedAt = decision.DecidedAt, RejectionReason = decision.Reason }
                ]
            };

            var grants = await applyEffect(tx, decision, updated);
            Current = updated;
            return new DecisionCasResult(DecisionCasOutcome.Applied, updated, grants);
        }
    }

    private sealed class MapResolver(Dictionary<string, SecurityPrincipalContext> map) : IExternalApproverResolver
    {
        public Task<SecurityPrincipalContext?> ResolveAsync(TenantId tenant, AssertedApprover asserted, CancellationToken ct = default) =>
            Task.FromResult(map.GetValueOrDefault(asserted.Subject));
    }

    private sealed class MemoryReplayStore : IIdempotencyStore
    {
        private readonly ConcurrentDictionary<string, object> _items = new();
        public Task<T?> GetAsync<T>(string key, CancellationToken ct = default) where T : class =>
            Task.FromResult(_items.TryGetValue(key, out var v) ? v as T : null);
        public Task<bool> SetIfNotExistsAsync<T>(string key, T value, TimeSpan ttl, CancellationToken ct = default) where T : class =>
            Task.FromResult(_items.TryAdd(key, value));
    }

    private sealed class StubRouter(ConsentWorkflowRouteResolution resolution) : IConsentWorkflowRouter
    {
        public Task<ConsentWorkflowRouteResolution> ResolveForTableAsync(TenantId tenantId, TableIdentifier table, CancellationToken ct = default) =>
            Task.FromResult(resolution);

        public Task<ConsentWorkflowRouteResolution> ResolveForTablesAsync(TenantId tenantId, IReadOnlyList<TableIdentifier> tables, CancellationToken ct = default) =>
            Task.FromResult(resolution);
    }

    private sealed record Harness(
        PermissionRequestEngine Engine,
        ExternalDecisionService Service,
        InMemoryCasApprovalRepo Repo,
        List<Consent> Consents,
        Func<ExternalDecisionEnvelope, ExternalDecisionEnvelope> Sign);
}
