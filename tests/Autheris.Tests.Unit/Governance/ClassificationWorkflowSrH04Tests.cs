namespace Autheris.Tests.Unit.Governance;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Services;
using Autheris.Application.Security.Totp.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

/// <summary>
/// SR-H-04 regression tests: classification workflow preconditions, single approval (Amendment 2), fail-closed rank/step-up.
/// </summary>
public sealed class ClassificationWorkflowSrH04Tests
{
    private const string Owner = "S-owner";
    private const string Admin = "S-admin";
    private const string Compliance = "S-compliance";
    private const string GoodCode = "123456";

    private static (ClassificationWorkflowStateMachine Sm, InMemoryClassificationProposalStore Store) Create(bool totp = true)
    {
        var store = new InMemoryClassificationProposalStore();
        var sm = new ClassificationWorkflowStateMachine(
            new Monitor(new GatewayOptions { Classification = new ClassificationOptions() }),
            NullLogger<ClassificationWorkflowStateMachine>.Instance,
            store,
            totpService: totp ? new SrH04FakeTotpService(GoodCode) : null,
            totpSecretStore: totp ? new SrH04FakeTotpSecretStore() : null);
        return (sm, store);
    }

    private static TableClassificationProposal Proposal(int rank, string status = "PENDING_OWNER_REVIEW", string? owner = Owner, string? proposer = Admin) =>
        new(
            Guid.NewGuid(),
            new TableIdentifier("db", "hr", "payroll"),
            owner,
            status,
            new List<ColumnClassificationProposal> { new("salary", "decimal", "KEY", rank, null, "REDACT", 0.95, false) },
            DateTimeOffset.UtcNow,
            ProposedBySid: proposer);

    private static TableClassificationApprovalRequest Req(
        TableClassificationProposal p, string sid, string role, string action = "APPROVE",
        string? token = null, params ColumnClassificationDecision[] decisions) =>
        new(p.ProposalId, p.TableIdentifier, new Sid(sid), role, action, decisions, StepUpAuthToken: token);

    // Problem 1: client-supplied proposal is ignored; the stored one is authoritative.
    [Fact]
    public async Task SR_H_04_ForgedClientProposal_IsReplacedByStoredProposal()
    {
        var (sm, store) = Create();
        var real = Proposal(rank: 4);
        await store.SaveProposalAsync(real);

        // Forged copy: ranks lowered, owner cleared, already pending governance.
        var forged = real with
        {
            Status = "PENDING_GOVERNANCE_REVIEW",
            DataOwnerSid = null,
            Columns = new List<ColumnClassificationProposal> { new("salary", "decimal", "KEY", 0, null, "NONE", 0.99, false) }
        };

        var result = await sm.TransitionAsync(forged, Req(forged, Compliance, "DATA_GOVERNANCE_REVIEWER"));

        result.Success.ShouldBeFalse();
        result.ErrorCode.ShouldBe("STEP_UP_AUTH_REQUIRED");
        (await store.GetProposalByIdAsync(real.ProposalId))!.Status.ShouldBe("PENDING_OWNER_REVIEW");

        // Forged low ranks cannot dodge step-up either: stored rank 4 still requires the token.
        var ownerResult = await sm.TransitionAsync(forged, Req(forged, Owner, "DATA_OWNER"));
        ownerResult.ErrorCode.ShouldBe("STEP_UP_AUTH_REQUIRED");
    }

    [Fact]
    public async Task SR_H_04_UnknownProposal_FailsClosed()
    {
        var (sm, _) = Create();
        var p = Proposal(1);
        var result = await sm.TransitionAsync(p, Req(p, Owner, "DATA_OWNER"));
        result.Success.ShouldBeFalse();
        result.ErrorCode.ShouldBe("PROPOSAL_NOT_FOUND");
    }

    // Problem 2: status preconditions.
    [Theory]
    [InlineData("CLASSIFIED", "DATA_OWNER", "APPROVE")]
    [InlineData("CLASSIFIED", "DATA_GOVERNANCE_REVIEWER", "APPROVE")]
    [InlineData("REJECTED", "DATA_OWNER", "APPROVE")]
    [InlineData("REJECTED", "DATA_GOVERNANCE_REVIEWER", "APPROVE")]
    [InlineData("CLASSIFIED", "DATA_OWNER", "REJECT")]
    [InlineData("REJECTED", "DATA_GOVERNANCE_REVIEWER", "REJECT")]
    [InlineData("BOGUS", "DATA_OWNER", "APPROVE")]
    public async Task SR_H_04_InvalidStatusForTransition_IsDenied(string status, string role, string action)
    {
        var (sm, store) = Create();
        var p = Proposal(1, status) with { ApprovedBySid = "S-other" };
        await store.SaveProposalAsync(p);

        var sid = role == "DATA_OWNER" ? Owner : Compliance;
        var result = await sm.TransitionAsync(p, Req(p, sid, role, action));

        result.Success.ShouldBeFalse();
        result.ErrorCode.ShouldBe("INVALID_WORKFLOW_STATE");
        (await store.GetProposalByIdAsync(p.ProposalId))!.Status.ShouldBe(status);
    }

    [Fact]
    public async Task SR_H_04_UnknownAction_IsDenied()
    {
        var (sm, store) = Create();
        var p = Proposal(1);
        await store.SaveProposalAsync(p);
        var result = await sm.TransitionAsync(p, Req(p, Owner, "DATA_OWNER", "SEAL"));
        result.Success.ShouldBeFalse();
        result.ErrorCode.ShouldBe("INVALID_ACTION");
    }

    // Single approval (Amendment 2): one decision by a DG or owner member settles the proposal; no second stage.
    [Theory]
    [InlineData("PENDING_OWNER_REVIEW", "DATA_OWNER")]
    [InlineData("PENDING_OWNER_REVIEW", "DATA_GOVERNANCE_REVIEWER")]
    [InlineData("UNCLASSIFIED", "DATA_GOVERNANCE_REVIEWER")]
    [InlineData("PENDING_GOVERNANCE_REVIEW", "DATA_OWNER")]
    [InlineData("PENDING_GOVERNANCE_REVIEW", "DATA_GOVERNANCE_REVIEWER")]
    public async Task Single_approval_one_decision_classifies(string status, string role)
    {
        var (sm, store) = Create();
        var p = Proposal(3, status);
        await store.SaveProposalAsync(p);
        var sid = role == "DATA_OWNER" ? Owner : Compliance;

        var result = await sm.TransitionAsync(p, Req(p, sid, role));

        result.Success.ShouldBeTrue(result.Message);
        result.NewStatus.ShouldBe("CLASSIFIED");
        var stored = (await store.GetProposalByIdAsync(p.ProposalId))!;
        stored.Status.ShouldBe("CLASSIFIED");
        stored.ApprovedBySid.ShouldBe(sid);
    }

    [Fact]
    public async Task Single_approval_proposer_may_decide_a_proposal_that_lowers_nothing()
    {
        var (sm, store) = Create();
        var p = Proposal(3, proposer: Owner);
        await store.SaveProposalAsync(p);

        var result = await sm.TransitionAsync(p, Req(p, Owner, "DATA_OWNER"));

        result.Success.ShouldBeTrue(result.Message);
        result.NewStatus.ShouldBe("CLASSIFIED");
    }

    // Problem 5: decisions cannot lower ranks.
    [Fact]
    public async Task SR_H_04_LoweredColumnDecision_DoesNotBypassStepUp()
    {
        var (sm, store) = Create();
        var p = Proposal(4);
        await store.SaveProposalAsync(p);

        var lowered = new ColumnClassificationDecision("salary", "OVERRIDE", "L0", 0, "NONE");
        var result = await sm.TransitionAsync(p, Req(p, Owner, "DATA_OWNER", decisions: lowered));

        result.Success.ShouldBeFalse();
        result.ErrorCode.ShouldBe("STEP_UP_AUTH_REQUIRED");
    }

    [Fact]
    public async Task SR_H_04_RaisingColumnDecision_RaisesEffectiveRank()
    {
        var (sm, store) = Create();
        var p = Proposal(1);
        await store.SaveProposalAsync(p);

        var raised = new ColumnClassificationDecision("salary", "OVERRIDE", "L4", 4, "REDACT");
        var result = await sm.TransitionAsync(p, Req(p, Owner, "DATA_OWNER", decisions: raised));
        result.ErrorCode.ShouldBe("STEP_UP_AUTH_REQUIRED");
    }

    // Problem 6: step-up is verified, fail-closed without services.
    [Fact]
    public async Task SR_H_04_StepUpTokenWithoutVerificationService_FailsClosed()
    {
        var (sm, store) = Create(totp: false);
        var p = Proposal(4);
        await store.SaveProposalAsync(p);

        var result = await sm.TransitionAsync(p, Req(p, Owner, "DATA_OWNER", token: "anything"));
        result.Success.ShouldBeFalse();
        result.ErrorCode.ShouldBe("STEP_UP_AUTH_UNAVAILABLE");
    }

    [Fact]
    public async Task SR_H_04_WrongStepUpToken_IsRejected_CorrectOneAccepted()
    {
        var (sm, store) = Create();
        var p = Proposal(4);
        await store.SaveProposalAsync(p);

        var bad = await sm.TransitionAsync(p, Req(p, Owner, "DATA_OWNER", token: "000000"));
        bad.Success.ShouldBeFalse();
        bad.ErrorCode.ShouldBe("INVALID_STEP_UP_AUTH_TOKEN");

        var good = await sm.TransitionAsync(p, Req(p, Owner, "DATA_OWNER", token: GoodCode));
        good.Success.ShouldBeTrue();
    }

    // Happy path: one decider seals the proposal.
    [Fact]
    public async Task SR_H_04_SingleDecider_SealsProposal()
    {
        var (sm, store) = Create();
        var p = Proposal(4);
        await store.SaveProposalAsync(p);

        var decided = await sm.TransitionAsync(p, Req(p, Owner, "DATA_OWNER", token: GoodCode));
        decided.Success.ShouldBeTrue();
        decided.NewStatus.ShouldBe("CLASSIFIED");

        var stored = (await store.GetProposalByIdAsync(p.ProposalId))!;
        stored.Status.ShouldBe("CLASSIFIED");
        stored.ApprovedBySid.ShouldBe(Owner);

        // Sealed proposals cannot be re-approved.
        var again = await sm.TransitionAsync(p, Req(p, Compliance, "DATA_GOVERNANCE_REVIEWER", token: GoodCode));
        again.ErrorCode.ShouldBe("INVALID_WORKFLOW_STATE");
    }

    private sealed class Monitor(GatewayOptions value) : IOptionsMonitor<GatewayOptions>
    {
        public GatewayOptions CurrentValue { get; } = value;
        public GatewayOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<GatewayOptions, string?> listener) => null;
    }
}

internal sealed class SrH04FakeTotpSecretStore : ITotpSecretStore
{
    public Task SetSecretAsync(string userSid, string secretBase32, CancellationToken ct = default) => Task.CompletedTask;
    public Task<string?> GetSecretAsync(string userSid, CancellationToken ct = default) => Task.FromResult<string?>("SECRET");
    public Task<bool> HasSecretAsync(string userSid, CancellationToken ct = default) => Task.FromResult(true);
    public Task RemoveSecretAsync(string userSid, CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class SrH04FakeTotpService(string validCode) : ITotpVerificationService
{
    public TotpEnrollmentResult GenerateEnrollment(string userSid, string email, string issuer = "Autheris") => throw new NotSupportedException();
    public string BuildQrCodeUri(string issuer, string user, string secretBase32) => throw new NotSupportedException();
    public bool VerifyTotp(string secretBase32, string totpCode, int toleranceSteps = 1) => totpCode == validCode;
    public Task<bool> VerifyAndConsumeTotpAsync(string userSid, string secretBase32, string totpCode, CancellationToken ct = default) => Task.FromResult(totpCode == validCode);
    public string GenerateTotpCode(string secretBase32, DateTimeOffset? timestamp = null) => validCode;
}
