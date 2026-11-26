using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
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
public sealed class PermissionRequestEngineDecideTests
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly TableIdentifier OrdersTable = new("sales", "dbo", "orders");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-22T12:00:00Z");

    private static SecurityPrincipalContext ContextFor(string sid, string role = "GovernanceAdmin")
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new(ClaimTypes.PrimarySid, sid),
            new("tenant_id", TenantA.Value),
            new(ClaimTypes.Role, role)
        ], "Bearer"));
        return new SecurityPrincipalContext
        {
            UserSid = new Sid(sid),
            TenantId = TenantA,
            GroupSids = new HashSet<Sid>(),
            GroupResolutionStatus = GroupResolutionStatus.Complete,
            TenantRoles = new HashSet<string>([role], StringComparer.OrdinalIgnoreCase),
            ClusterRoles = new HashSet<string>(),
            AuthenticationScheme = "Bearer",
            Principal = principal
        };
    }

    private static ConsentRequest PendingRequest(Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        TableId = Guid.NewGuid(),
        TableIdentifier = OrdersTable,
        RequesterSid = new Sid("user:alice"),
        RequestedGranteeType = GranteeType.User,
        RequestedGranteeRef = "user:alice",
        BusinessJustification = "Quarterly financial order audit.",
        Status = "PENDING",
        RequestedAt = Now.AddHours(-1),
        RequestedValidFrom = Now,
        RequestedValidTo = Now.AddDays(30),
        TenantId = TenantA,
        Version = 1,
        RequestKind = ConsentRequestKind.Data,
        GrantKind = PermissionGrantKind.Data
    };

    [Fact]
    public void SelfGrant_flag_not_accepted_from_caller()
    {
        typeof(PermissionDecisionCommand).GetProperty("SelfGrant").ShouldBeNull();
        typeof(PermissionRequestSubmission).GetProperty("SelfGrant").ShouldBeNull();
    }

    [Fact]
    public async Task Audit_failure_aborts_grant()
    {
        var req = PendingRequest();
        var committed = false;

        var failingAudit = NullFake.Create<IAuditWriter>((m, _) =>
            m.Name == nameof(IAuditWriter.RecordRequiredAsync)
                ? Task.FromException(new AuditUnavailableException("Audit DB down"))
                : NullFake.UseDefault);

        var grantWriter = NullFake.Create<IGrantWriter>((m, args) =>
        {
            if (m.Name == nameof(IGrantWriter.InsertAndGrantAsync))
            {
                var c = (Consent)args![1]!;
                return Task.FromResult(new GrantRecord(c.TenantId.Value, 1, Guid.NewGuid(), GrantRecordType.Issued, c.Id, c.ConsentRequestId,
                    c.GrantSource, null, c.GranteeType.ToString(), c.GranteeSid!.Value.Value, OrdersTable.ToString(), "{}", c.ValidFrom, c.ValidTo, "user:alice", "{}", Now, Now, GrantRecordChain.GenesisHash));
            }

            return NullFake.UseDefault;
        });

        using var rawTx = new DummyDbTransaction();
        var approvals = NullFake.Create<IConsentApprovalRepository>((m, args) =>
        {
            if (m.Name == nameof(IConsentApprovalRepository.TryDecideAsync))
            {
                var decideFn = (Func<IGovernanceTransaction, ConsentRequest, Task<PermissionDecisionRecord>>)args![2]!;
                var applyFn = (Func<IGovernanceTransaction, PermissionDecisionRecord, ConsentRequest, Task<IReadOnlyList<GrantRef>>>)args[3]!;
                return Task.Run(async () =>
                {
                    var tx = NullFake.Create<IGovernanceTransaction>((tm, _) =>
                        tm.Name == "get_" + nameof(IGovernanceTransaction.Transaction) ? rawTx : NullFake.UseDefault);
                    var record = await decideFn(tx, req);
                    await applyFn(tx, record, req);
                    committed = true;
                    return new DecisionCasResult(DecisionCasOutcome.Applied, req);
                });
            }

            return NullFake.UseDefault;
        });

        var engine = new PermissionRequestEngine(
            approvals,
            failingAudit,
            new ApproverResolver(),
            new ApproverEligibility(new GovernanceCapabilities()),
            new SeparationOfDutiesService(),
            new GrantPolicy(),
            GrantValidityPolicy.CreateDefault(),
            [new DataGrantApplier(grantWriter)],
            timeProvider: new FakeTimeProvider(Now));

        await Should.ThrowAsync<AuditUnavailableException>(() => engine.DecideAsync(
            new PermissionDecisionCommand(req.Id, 1, ContextFor("user:dg-approver"), Approve: true)));

        committed.ShouldBeFalse("Audit failure must abort the transaction before commit.");
    }

    [Fact]
    public async Task Withdraw_audit_failure_keeps_the_request_pending()
    {
        var req = PendingRequest();
        var withdrawn = false;
        var failingAudit = NullFake.Create<IAuditWriter>((m, _) =>
            m.Name == nameof(IAuditWriter.RecordRequiredAsync)
                ? Task.FromException(new AuditUnavailableException("Audit DB down"))
                : NullFake.UseDefault);
        var approvals = NullFake.Create<IConsentApprovalRepository>((m, _) =>
        {
            if (m.Name == nameof(IConsentApprovalRepository.GetConsentRequestAsync))
            {
                return Task.FromResult<ConsentRequest?>(req);
            }

            if (m.Name == nameof(IConsentApprovalRepository.WithdrawConsentRequestAsync))
            {
                withdrawn = true;
                return Task.FromResult(true);
            }

            return NullFake.UseDefault;
        });
        var engine = new PermissionRequestEngine(
            approvals,
            failingAudit,
            new ApproverResolver(),
            new ApproverEligibility(new GovernanceCapabilities()),
            new SeparationOfDutiesService(),
            new GrantPolicy(),
            GrantValidityPolicy.CreateDefault(),
            Array.Empty<IGrantEffectApplier>(),
            timeProvider: new FakeTimeProvider(Now));
        var owner = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.PrimarySid, "user:alice"), new Claim("tenant_id", TenantA.Value)], "Bearer"));

        await Should.ThrowAsync<AuditUnavailableException>(() => engine.WithdrawAsync(req.Id, owner));

        withdrawn.ShouldBeFalse("The state change must not commit when the required audit cannot be written.");
    }

    private static PermissionRequestEngine WithdrawEngine(ConsentRequest req, Action onWithdraw)
    {
        var approvals = NullFake.Create<IConsentApprovalRepository>((m, _) =>
        {
            if (m.Name == nameof(IConsentApprovalRepository.GetConsentRequestAsync))
            {
                return Task.FromResult<ConsentRequest?>(req);
            }

            if (m.Name == nameof(IConsentApprovalRepository.WithdrawConsentRequestAsync))
            {
                onWithdraw();
                return Task.FromResult(true);
            }

            return NullFake.UseDefault;
        });
        return new PermissionRequestEngine(
            approvals,
            new InMemoryAuditWriter(),
            new ApproverResolver(),
            new ApproverEligibility(new GovernanceCapabilities()),
            new SeparationOfDutiesService(),
            new GrantPolicy(),
            GrantValidityPolicy.CreateDefault(),
            Array.Empty<IGrantEffectApplier>(),
            timeProvider: new FakeTimeProvider(Now));
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Withdraw_by_another_user_is_indistinguishable_from_missing()
    {
        var req = PendingRequest();
        var withdrawn = false;
        var engine = WithdrawEngine(req, () => withdrawn = true);
        var attacker = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.PrimarySid, "user:mallory"), new Claim("tenant_id", TenantA.Value)], "Bearer"));

        var ex = await Should.ThrowAsync<ConsentRequestException>(() => engine.WithdrawAsync(req.Id, attacker));

        ex.Code.ShouldBe("NOT_FOUND");
        withdrawn.ShouldBeFalse();
    }

    [Theory]
    [InlineData("APPROVED")]
    [InlineData("REJECTED")]
    [InlineData("WITHDRAWN")]
    [InlineData("EXPIRED")]
    [Trait("Category", "Security")]
    public async Task Withdraw_of_a_decided_request_is_refused(string status)
    {
        var req = PendingRequest();
        req.Status = status;
        var withdrawn = false;
        var engine = WithdrawEngine(req, () => withdrawn = true);
        var owner = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.PrimarySid, "user:alice"), new Claim("tenant_id", TenantA.Value)], "Bearer"));

        var ex = await Should.ThrowAsync<ConsentRequestException>(() => engine.WithdrawAsync(req.Id, owner));

        ex.Code.ShouldBe("INVALID_STATE");
        withdrawn.ShouldBeFalse();
    }

    [Fact]
    public async Task Withdraw_that_throws_audits_the_failure_and_rethrows()
    {
        var req = PendingRequest();
        var audit = new InMemoryAuditWriter();
        var approvals = NullFake.Create<IConsentApprovalRepository>((m, _) =>
        {
            if (m.Name == nameof(IConsentApprovalRepository.GetConsentRequestAsync))
            {
                return Task.FromResult<ConsentRequest?>(req);
            }

            if (m.Name == nameof(IConsentApprovalRepository.WithdrawConsentRequestAsync))
            {
                return Task.FromException<bool>(new InvalidOperationException("store down"));
            }

            return NullFake.UseDefault;
        });
        var engine = new PermissionRequestEngine(
            approvals,
            audit,
            new ApproverResolver(),
            new ApproverEligibility(new GovernanceCapabilities()),
            new SeparationOfDutiesService(),
            new GrantPolicy(),
            GrantValidityPolicy.CreateDefault(),
            Array.Empty<IGrantEffectApplier>(),
            timeProvider: new FakeTimeProvider(Now));
        var owner = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.PrimarySid, "user:alice"), new Claim("tenant_id", TenantA.Value)], "Bearer"));

        await Should.ThrowAsync<InvalidOperationException>(() => engine.WithdrawAsync(req.Id, owner));

        audit.Entries.Select(e => e.Decision).ShouldBe(["WITHDRAWN", "WITHDRAW_FAILED"]);
    }

    /// <summary>Withdrawal fake with a recording outbox: the engine must leave the withdrawal event to the repository transaction.</summary>
    private static (PermissionRequestEngine Engine, RecordingConsentEventOutbox Events, Func<bool> Withdrawn) WithdrawEventEngine(
        ConsentRequest req, bool withdrawSucceeds = true)
    {
        var withdrawn = false;
        var events = new RecordingConsentEventOutbox();
        var approvals = NullFake.Create<IConsentApprovalRepository>((m, _) =>
        {
            if (m.Name == nameof(IConsentApprovalRepository.GetConsentRequestAsync))
            {
                return Task.FromResult<ConsentRequest?>(req);
            }

            if (m.Name == nameof(IConsentApprovalRepository.WithdrawConsentRequestAsync))
            {
                withdrawn = withdrawSucceeds;
                return Task.FromResult(withdrawSucceeds);
            }

            return NullFake.UseDefault;
        });
        var engine = new PermissionRequestEngine(
            approvals,
            new InMemoryAuditWriter(),
            new ApproverResolver(),
            new ApproverEligibility(new GovernanceCapabilities()),
            new SeparationOfDutiesService(),
            new GrantPolicy(),
            GrantValidityPolicy.CreateDefault(),
            Array.Empty<IGrantEffectApplier>(),
            timeProvider: new FakeTimeProvider(Now),
            eventOutbox: events);
        return (engine, events, () => withdrawn);
    }

    private static ClaimsPrincipal RequesterAlice() => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.PrimarySid, "user:alice"), new Claim("tenant_id", TenantA.Value)], "Bearer"));

    [Theory]
    [InlineData("PENDING")]
    [InlineData("PENDING_SECOND_APPROVAL")]
    [InlineData("PENDING_EXTERNAL_APPROVAL")]
    public async Task Withdrawal_event_is_left_to_the_repository_transaction(string status)
    {
        // Issue #1165: request.updated (WITHDRAWN) commits with the withdrawal in the repository; the engine appends nothing of its own,
        // so a committed withdrawal never reports an error for a lost event and never emits twice.
        var req = PendingRequest();
        req.Status = status;
        var (engine, events, withdrawn) = WithdrawEventEngine(req);

        (await engine.WithdrawAsync(req.Id, RequesterAlice())).ShouldBeTrue();

        withdrawn().ShouldBeTrue();
        events.Appended.ShouldBeEmpty();
    }

    [Fact]
    public async Task Refused_or_lost_withdrawals_append_no_event()
    {
        var decided = PendingRequest();
        decided.Status = "APPROVED";
        var (refusedEngine, refusedEvents, _) = WithdrawEventEngine(decided);
        await Should.ThrowAsync<ConsentRequestException>(() => refusedEngine.WithdrawAsync(decided.Id, RequesterAlice()));
        refusedEvents.Appended.ShouldBeEmpty();

        var foreign = PendingRequest();
        var (foreignEngine, foreignEvents, _) = WithdrawEventEngine(foreign);
        var mallory = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.PrimarySid, "user:mallory"), new Claim("tenant_id", TenantA.Value)], "Bearer"));
        await Should.ThrowAsync<ConsentRequestException>(() => foreignEngine.WithdrawAsync(foreign.Id, mallory));
        foreignEvents.Appended.ShouldBeEmpty();

        var raced = PendingRequest();
        var (racedEngine, racedEvents, _) = WithdrawEventEngine(raced, withdrawSucceeds: false);
        await Should.ThrowAsync<ConsentRequestException>(() => racedEngine.WithdrawAsync(raced.Id, RequesterAlice()));
        racedEvents.Appended.ShouldBeEmpty();
    }

    [Fact]
    public async Task Decision_without_a_database_transaction_fails_closed_when_events_are_configured()
    {
        // Issue #1165: the decision event must commit with the decision; an outbox append without the decision transaction would open
        // its own connection, so the decision is refused before anything commits.
        var req = PendingRequest();
        var committed = false;
        var events = new RecordingConsentEventOutbox();
        var approvals = NullFake.Create<IConsentApprovalRepository>((m, args) =>
        {
            if (m.Name == nameof(IConsentApprovalRepository.TryDecideAsync))
            {
                var decideFn = (Func<IGovernanceTransaction, ConsentRequest, Task<PermissionDecisionRecord>>)args![2]!;
                var applyFn = (Func<IGovernanceTransaction, PermissionDecisionRecord, ConsentRequest, Task<IReadOnlyList<GrantRef>>>)args[3]!;
                return Task.Run(async () =>
                {
                    var tx = NullFake.Create<IGovernanceTransaction>();
                    var record = await decideFn(tx, req);
                    await applyFn(tx, record, req);
                    committed = true;
                    return new DecisionCasResult(DecisionCasOutcome.Applied, req);
                });
            }

            return NullFake.UseDefault;
        });
        var engine = new PermissionRequestEngine(
            approvals,
            new InMemoryAuditWriter(),
            new ApproverResolver(),
            new ApproverEligibility(new GovernanceCapabilities()),
            new SeparationOfDutiesService(),
            new GrantPolicy(),
            GrantValidityPolicy.CreateDefault(),
            [new StubGrantApplier(ConsentRequestKind.Data)],
            timeProvider: new FakeTimeProvider(Now),
            eventOutbox: events);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => engine.DecideAsync(
            new PermissionDecisionCommand(req.Id, 1, ContextFor("user:dg-approver"), Approve: true)));

        ex.Message.ShouldContain("cannot be appended atomically");
        committed.ShouldBeFalse();
        events.Appended.ShouldBeEmpty();
    }

    [Fact]
    public async Task Denied_attempt_audited()
    {
        var req = PendingRequest();
        var audit = new InMemoryAuditWriter();
        var approvals = NullFake.Create<IConsentApprovalRepository>((m, args) =>
        {
            if (m.Name == nameof(IConsentApprovalRepository.TryDecideAsync))
            {
                var decideFn = (Func<IGovernanceTransaction, ConsentRequest, Task<PermissionDecisionRecord>>)args![2]!;
                return Task.Run(async () =>
                {
                    var tx = NullFake.Create<IGovernanceTransaction>();
                    await decideFn(tx, req);
                    return new DecisionCasResult(DecisionCasOutcome.Applied, req);
                });
            }

            return NullFake.UseDefault;
        });

        var engine = new PermissionRequestEngine(
            approvals,
            audit,
            new ApproverResolver(),
            new ApproverEligibility(new GovernanceCapabilities()),
            new SeparationOfDutiesService(),
            new GrantPolicy(),
            GrantValidityPolicy.CreateDefault(),
            Array.Empty<IGrantEffectApplier>(),
            timeProvider: new FakeTimeProvider(Now));

        // Requester/grantee attempting to self-approve is refused by SoD and audited fail-closed.
        var ex = await Should.ThrowAsync<ConsentDecisionException>(() => engine.DecideAsync(
            new PermissionDecisionCommand(req.Id, 1, ContextFor("user:alice"), Approve: true)));
        ex.ErrorCode.ShouldBe("SOD_VIOLATION");
        audit.Entries.ShouldContain(e => e.EventType == AuditEventTypes.ConsentDenied && e.Decision == "DENY");
    }

    private sealed class DummyDbTransaction : DbTransaction
    {
        public override System.Data.IsolationLevel IsolationLevel => System.Data.IsolationLevel.ReadCommitted;
        protected override DbConnection? DbConnection => null;
        public override void Commit() { }
        public override void Rollback() { }
    }
}
