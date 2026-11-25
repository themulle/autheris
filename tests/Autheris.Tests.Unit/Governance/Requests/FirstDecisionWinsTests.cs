using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Security;
using Autheris.Application.Audit;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.ConsentRequests;
using Autheris.Application.Governance.ConsentRequests;
using Autheris.Application.Governance.Grants;
using Autheris.Application.Governance.Grants.Records;
using Autheris.Application.Governance.Requests;
using Autheris.Application.Security;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Grants;
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
public sealed class FirstDecisionWinsTests
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly TableIdentifier OrdersTable = new("sales", "dbo", "orders");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-22T12:00:00Z");

    private static SecurityPrincipalContext Approver(string sid) => new()
    {
        UserSid = new Sid(sid),
        TenantId = TenantA,
        GroupSids = new HashSet<Sid>(),
        GroupResolutionStatus = GroupResolutionStatus.Complete,
        TenantRoles = new HashSet<string>(["GovernanceAdmin"], StringComparer.OrdinalIgnoreCase),
        ClusterRoles = new HashSet<string>(),
        AuthenticationScheme = "Bearer",
        Principal = new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.PrimarySid, sid), new("tenant_id", TenantA.Value), new(ClaimTypes.Role, "GovernanceAdmin")], "Bearer"))
    };

    private static (PermissionRequestEngine Engine, InMemoryCasApprovalRepo Repo, InMemoryAuditWriter Audit, List<Consent> CreatedConsents) CreateHarness(
        IProtectionSnapshotProvider? protection = null,
        IGrantEffectApplier? applier = null,
        Autheris.Application.ConsentRequests.ProfileAssignments.IProfileAssignmentStore? assignmentStore = null)
    {
        var req = new ConsentRequest
        {
            Id = Guid.NewGuid(),
            TableId = Guid.NewGuid(),
            TableIdentifier = OrdersTable,
            RequesterSid = new Sid("user:requester"),
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = "user:requester",
            BusinessJustification = "Need order table access for finance close.",
            Status = "PENDING",
            RequestedAt = Now.AddMinutes(-10),
            RequestedValidFrom = Now,
            RequestedValidTo = Now.AddDays(30),
            TenantId = TenantA,
            Version = 1,
            RequestKind = ConsentRequestKind.Data,
            GrantKind = PermissionGrantKind.Data
        };

        var repo = new InMemoryCasApprovalRepo(req);
        var audit = new InMemoryAuditWriter();
        var consents = new List<Consent>();
        var grantWriter = NullFake.Create<IGrantWriter>((m, args) =>
        {
            if (m.Name == nameof(IGrantWriter.InsertAndGrantAsync))
            {
                var c = (Consent)args![1]!;
                consents.Add(c);
                return Task.FromResult(new GrantRecord(c.TenantId.Value, consents.Count, Guid.NewGuid(), GrantRecordType.Issued, c.Id, c.ConsentRequestId,
                    c.GrantSource, null, c.GranteeType.ToString(), c.GranteeSid!.Value.Value, OrdersTable.ToString(), "{}", c.ValidFrom, c.ValidTo, "user:requester", "{}", Now, Now, GrantRecordChain.GenesisHash));
            }

            return NullFake.UseDefault;
        });

        var engine = new PermissionRequestEngine(
            repo,
            audit,
            new ApproverResolver(),
            new ApproverEligibility(new GovernanceCapabilities()),
            new SeparationOfDutiesService(),
            new GrantPolicy(),
            GrantValidityPolicy.CreateDefault(),
            [applier ?? new DataGrantApplier(grantWriter)],
            protectionProvider: protection,
            timeProvider: new FakeTimeProvider(Now),
            assignmentStore: assignmentStore);

        return (engine, repo, audit, consents);
    }

    [Fact]
    public async Task Second_approve_409_and_no_second_grant_and_conflict_audited()
    {
        var (engine, repo, audit, consents) = CreateHarness();

        var first = await engine.DecideAsync(new PermissionDecisionCommand(repo.Current.Id, 1, Approver("user:approver-1"), Approve: true));
        first.Request.Status.ShouldBe("APPROVED");
        consents.Count.ShouldBe(1);

        var conflict = await Should.ThrowAsync<ConsentDecisionException>(() =>
            engine.DecideAsync(new PermissionDecisionCommand(repo.Current.Id, 1, Approver("user:approver-2"), Approve: true)));
        conflict.StatusCode.ShouldBe(409);
        conflict.ErrorCode.ShouldBe("REQUEST_ALREADY_DECIDED");
        consents.Count.ShouldBe(1);
        audit.Entries.ShouldContain(e => e.EventType == AuditEventTypes.PermissionDecisionConflict && e.Decision == "CONFLICT");
    }

    [Fact]
    public async Task Approval_invalidates_every_granted_table_and_signals_pattern_grants_after_commit()
    {
        // Issue #1044: an approval can create grants beyond the request's own table (access profiles span tables and patterns).
        var invoices = new TableIdentifier("sales", "dbo", "invoices");
        var applier = NullFake.Create<IGrantEffectApplier>((m, _) => m.Name switch
        {
            "get_" + nameof(IGrantEffectApplier.RequestKind) => ConsentRequestKind.Data,
            nameof(IGrantEffectApplier.ApplyAsync) => Task.FromResult<IReadOnlyList<GrantRef>>(
            [
                new GrantRef(Guid.NewGuid(), PermissionGrantKind.Data, Table: OrdersTable),
                new GrantRef(Guid.NewGuid(), PermissionGrantKind.Data, Table: invoices),
                new GrantRef(Guid.NewGuid(), PermissionGrantKind.Data),
                new GrantRef(Guid.NewGuid(), PermissionGrantKind.Data, ContainerKind: ContainerKind.Domain, ContainerId: Guid.NewGuid())
            ]),
            _ => NullFake.UseDefault
        });
        var invalidated = new List<TableIdentifier>();
        var store = NullFake.Create<Autheris.Application.ConsentRequests.ProfileAssignments.IProfileAssignmentStore>((m, args) =>
        {
            if (m.Name == nameof(Autheris.Application.ConsentRequests.ProfileAssignments.IProfileAssignmentStore.InvalidateTablesAfterCommitAsync))
            {
                invalidated.AddRange((IReadOnlyCollection<TableIdentifier>)args![0]!);
                return Task.CompletedTask;
            }

            return NullFake.UseDefault;
        });
        var (engine, repo, _, _) = CreateHarness(applier: applier, assignmentStore: store);

        await engine.DecideAsync(new PermissionDecisionCommand(repo.Current.Id, 1, Approver("user:approver-1"), Approve: true));

        invalidated.ShouldContain(invoices);
        invalidated.ShouldContain(t => string.IsNullOrWhiteSpace(t.TableName), "a pattern grant signals a catalog version change");
        invalidated.ShouldNotContain(OrdersTable, "the request table is already advanced through the policy epoch repository");
        invalidated.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Reject_then_approve_409_and_reject_terminal()
    {
        var (engine, repo, audit, consents) = CreateHarness();

        var rejected = await engine.DecideAsync(new PermissionDecisionCommand(
            repo.Current.Id, 1, Approver("user:approver-1"), Approve: false, Reason: "Access scope is overly broad for this task."));
        rejected.Request.Status.ShouldBe("REJECTED");
        consents.ShouldBeEmpty();

        var conflict = await Should.ThrowAsync<ConsentDecisionException>(() =>
            engine.DecideAsync(new PermissionDecisionCommand(repo.Current.Id, 1, Approver("user:approver-2"), Approve: true)));
        conflict.StatusCode.ShouldBe(409);
        conflict.ErrorCode.ShouldBe("REQUEST_ALREADY_DECIDED");
        consents.ShouldBeEmpty();
        audit.Entries.ShouldContain(e => e.EventType == AuditEventTypes.PermissionDecisionConflict);
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
}
