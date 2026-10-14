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
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// SR-H-04: the classification workflow must enforce state preconditions, distinct approvers per stage, a
/// non-empty data-owner match and a verified step-up, based on the server-side proposal only.
/// </summary>
public sealed class SecurityReviewSrH04ClassificationWorkflowTests
{
    private const string Owner = "S-1-5-21-owner";
    private static readonly TableIdentifier Table = new("db", "finance", "invoices");

    private static ClassificationWorkflowStateMachine CreateMachine(
        InMemoryClassificationProposalStore? store,
        ITotpVerificationService? totp = null,
        ITotpSecretStore? secrets = null)
    {
        var options = new GatewayOptions { Classification = new ClassificationOptions() };
        return new ClassificationWorkflowStateMachine(
            new TestOptionsMonitor<GatewayOptions>(options),
            NullLogger<ClassificationWorkflowStateMachine>.Instance,
            store,
            hooks: null,
            totpService: totp,
            totpSecretStore: secrets);
    }

    private static TableClassificationProposal Proposal(
        string status, string? owner = Owner, int rank = 3, string? stage1 = null)
        => new(
            Guid.NewGuid(),
            Table,
            owner,
            status,
            new List<ColumnClassificationProposal>
            {
                new("iban", "varchar(34)", rank >= 4 ? "L4_STRICTLY_CONFIDENTIAL" : "L3_CONFIDENTIAL", rank, "IBAN", "MASK", 0.95, false)
            },
            DateTimeOffset.UtcNow,
            stage1);

    private static TableClassificationApprovalRequest Request(
        TableClassificationProposal p, string sid, string role, string action = "APPROVE",
        string? token = null, int? decisionRank = null)
        => new(
            p.ProposalId,
            p.TableIdentifier,
            new Sid(sid),
            role,
            action,
            decisionRank.HasValue
                ? new List<ColumnClassificationDecision> { new("iban", "OVERRIDE", "L0", decisionRank.Value, "NONE") }
                : new List<ColumnClassificationDecision>(),
            StepUpAuthToken: token);

    private static async Task<InMemoryClassificationProposalStore> StoreWith(TableClassificationProposal p)
    {
        var store = new InMemoryClassificationProposalStore();
        await store.SaveProposalAsync(p);
        return store;
    }

    [Fact]
    public async Task SR_H_04_SuppliedProposalIsIgnored_StoredStatusIsAuthoritative()
    {
        var stored = Proposal("CLASSIFIED");
        var store = await StoreWith(stored);
        var machine = CreateMachine(store);

        // The caller claims the proposal is still pending owner review.
        var forged = stored with { Status = "PENDING_OWNER_REVIEW" };
        var result = await machine.TransitionAsync(forged, Request(stored, Owner, "DATA_OWNER"));

        result.Success.ShouldBeFalse();
        result.ErrorCode.ShouldBe("INVALID_STATE_TRANSITION");
    }

    [Fact]
    public async Task SR_H_04_SuppliedDataOwnerIsIgnored_StoredOwnerIsAuthoritative()
    {
        var stored = Proposal("PENDING_OWNER_REVIEW", owner: Owner);
        var store = await StoreWith(stored);
        var machine = CreateMachine(store);

        var forged = stored with { DataOwnerSid = "S-1-5-21-attacker" };
        var result = await machine.TransitionAsync(forged, Request(stored, "S-1-5-21-attacker", "DATA_OWNER"));

        result.Success.ShouldBeFalse();
        result.ErrorCode.ShouldBe("DATA_OWNER_MISMATCH");
    }

    [Fact]
    public async Task SR_H_04_UnknownProposalInStore_IsDenied()
    {
        var machine = CreateMachine(new InMemoryClassificationProposalStore());
        var p = Proposal("PENDING_OWNER_REVIEW");

        var result = await machine.TransitionAsync(p, Request(p, Owner, "DATA_OWNER"));

        result.Success.ShouldBeFalse();
        result.ErrorCode.ShouldBe("PROPOSAL_NOT_FOUND");
    }

    [Theory]
    [InlineData("CLASSIFIED", "APPROVE")]
    [InlineData("REJECTED", "APPROVE")]
    [InlineData("CLASSIFIED", "REJECT")]
    [InlineData("REJECTED", "REJECT")]
    public async Task SR_H_04_FinalizedProposal_CannotBeTransitionedAgain(string status, string action)
    {
        var p = Proposal(status);
        var machine = CreateMachine(await StoreWith(p));

        var result = await machine.TransitionAsync(p, Request(p, Owner, "DATA_OWNER", action));

        result.Success.ShouldBeFalse();
        result.ErrorCode.ShouldBe("INVALID_STATE_TRANSITION");
    }

    [Fact]
    public async Task SR_H_04_Reviewer_CannotFinalizeWithoutStage1()
    {
        var p = Proposal("PENDING_OWNER_REVIEW");
        var store = await StoreWith(p);
        var machine = CreateMachine(store);

        var result = await machine.TransitionAsync(p, Request(p, "S-1-5-21-reviewer", "DATA_GOVERNANCE_REVIEWER"));

        result.Success.ShouldBeFalse();
        result.ErrorCode.ShouldBe("INVALID_STATE_TRANSITION");
        (await store.GetProposalByIdAsync(p.ProposalId))!.Status.ShouldBe("PENDING_OWNER_REVIEW");
    }

    [Fact]
    public async Task SR_H_04_Reviewer_WithoutRecordedStage1Approver_IsDenied()
    {
        var p = Proposal("PENDING_GOVERNANCE_REVIEW", stage1: null);
        var machine = CreateMachine(await StoreWith(p));

        var result = await machine.TransitionAsync(p, Request(p, "S-1-5-21-reviewer", "DATA_GOVERNANCE_REVIEWER"));

        result.Success.ShouldBeFalse();
        result.ErrorCode.ShouldBe("STAGE1_APPROVAL_REQUIRED");
    }

    [Fact]
    public async Task SR_H_04_SameIdentityOnBothStages_IsDenied()
    {
        const string both = "S-1-5-21-admin";
        var p = Proposal("PENDING_GOVERNANCE_REVIEW", owner: Owner, stage1: both);
        var machine = CreateMachine(await StoreWith(p));

        var result = await machine.TransitionAsync(p, Request(p, both, "DATA_GOVERNANCE_REVIEWER"));

        result.Success.ShouldBeFalse();
        result.ErrorCode.ShouldBe("SOD_DISTINCT_APPROVER_VIOLATION");
    }

    [Fact]
    public async Task SR_H_04_TwoStageFlow_PersistsStage1ApproverAndAllowsDistinctReviewer()
    {
        var p = Proposal("PENDING_OWNER_REVIEW");
        var store = await StoreWith(p);
        var machine = CreateMachine(store);

        var stage1 = await machine.TransitionAsync(p, Request(p, Owner, "DATA_OWNER"));
        stage1.Success.ShouldBeTrue();
        stage1.NewStatus.ShouldBe("PENDING_GOVERNANCE_REVIEW");
        (await store.GetProposalByIdAsync(p.ProposalId))!.Stage1ApproverSid.ShouldBe(Owner);

        var stage2 = await machine.TransitionAsync(p, Request(p, "S-1-5-21-reviewer", "DATA_GOVERNANCE_REVIEWER"));
        stage2.Success.ShouldBeTrue();
        stage2.NewStatus.ShouldBe("CLASSIFIED");
    }

    [Theory]
    [InlineData("APPROVE")]
    [InlineData("REJECT")]
    public async Task SR_H_04_EmptyDataOwnerSid_DeniesDataOwnerActions(string action)
    {
        var p = Proposal("PENDING_OWNER_REVIEW", owner: "");
        var machine = CreateMachine(await StoreWith(p));

        var result = await machine.TransitionAsync(p, Request(p, "S-1-5-21-anyone", "DATA_OWNER", action));

        result.Success.ShouldBeFalse();
        result.ErrorCode.ShouldBe("DATA_OWNER_MISMATCH");
    }

    [Fact]
    public async Task SR_H_04_NullDataOwnerSid_DeniesDataOwnerReject()
    {
        var p = Proposal("PENDING_OWNER_REVIEW", owner: null);
        var machine = CreateMachine(await StoreWith(p));

        var result = await machine.TransitionAsync(p, Request(p, "S-1-5-21-anyone", "DATA_OWNER", "REJECT"));

        result.Success.ShouldBeFalse();
        result.ErrorCode.ShouldBe("DATA_OWNER_MISMATCH");
    }

    [Fact]
    public async Task SR_H_04_NonOwner_CannotActAsDataOwner()
    {
        var p = Proposal("PENDING_OWNER_REVIEW");
        var machine = CreateMachine(await StoreWith(p));

        var result = await machine.TransitionAsync(p, Request(p, "S-1-5-21-someone-else", "DATA_OWNER"));

        result.Success.ShouldBeFalse();
        result.ErrorCode.ShouldBe("DATA_OWNER_MISMATCH");
    }

    [Fact]
    public async Task SR_H_04_StepUpRequired_WithoutTotpServices_FailsClosed()
    {
        var p = Proposal("PENDING_OWNER_REVIEW", rank: 4);
        var store = await StoreWith(p);
        var machine = CreateMachine(store, totp: null, secrets: null);

        var result = await machine.TransitionAsync(p, Request(p, Owner, "DATA_OWNER", token: "any-non-empty-token"));

        result.Success.ShouldBeFalse();
        result.ErrorCode.ShouldBe("STEP_UP_AUTH_UNAVAILABLE");
        (await store.GetProposalByIdAsync(p.ProposalId))!.Status.ShouldBe("PENDING_OWNER_REVIEW");
    }

    [Fact]
    public async Task SR_H_04_StepUpRequired_InvalidTotp_IsDenied()
    {
        var p = Proposal("PENDING_OWNER_REVIEW", rank: 4);
        var totp = Substitute.For<ITotpVerificationService>();
        var secrets = Substitute.For<ITotpSecretStore>();
        secrets.GetSecretAsync(Owner, Arg.Any<CancellationToken>()).Returns("JBSWY3DPEHPK3PXP");
        totp.VerifyAndConsumeTotpAsync(Owner, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);
        var machine = CreateMachine(await StoreWith(p), totp, secrets);

        var result = await machine.TransitionAsync(p, Request(p, Owner, "DATA_OWNER", token: "000000"));

        result.Success.ShouldBeFalse();
        result.ErrorCode.ShouldBe("INVALID_STEP_UP_AUTH_TOKEN");
    }

    [Fact]
    public async Task SR_H_04_StepUpRequired_ValidTotp_IsVerifiedAndConsumed()
    {
        var p = Proposal("PENDING_OWNER_REVIEW", rank: 4);
        var totp = Substitute.For<ITotpVerificationService>();
        var secrets = Substitute.For<ITotpSecretStore>();
        secrets.GetSecretAsync(Owner, Arg.Any<CancellationToken>()).Returns("JBSWY3DPEHPK3PXP");
        totp.VerifyAndConsumeTotpAsync(Owner, "JBSWY3DPEHPK3PXP", "123456", Arg.Any<CancellationToken>()).Returns(true);
        var machine = CreateMachine(await StoreWith(p), totp, secrets);

        var result = await machine.TransitionAsync(p, Request(p, Owner, "DATA_OWNER", token: "123456"));

        result.Success.ShouldBeTrue();
        await totp.Received(1).VerifyAndConsumeTotpAsync(Owner, "JBSWY3DPEHPK3PXP", "123456", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SR_H_04_ColumnOverrideCannotLowerRank_StepUpStillRequired()
    {
        var p = Proposal("PENDING_OWNER_REVIEW", rank: 4);
        var machine = CreateMachine(await StoreWith(p));

        // The reviewer downgrades the column to rank 0 to dodge the step-up / four-eyes thresholds.
        var result = await machine.TransitionAsync(p, Request(p, Owner, "DATA_OWNER", token: null, decisionRank: 0));

        result.Success.ShouldBeFalse();
        result.ErrorCode.ShouldBe("STEP_UP_AUTH_REQUIRED");
    }

    [Fact]
    public async Task SR_H_04_SystemAutoApprover_OnIneligibleProposal_IsDenied()
    {
        var p = Proposal("UNCLASSIFIED", owner: null, rank: 4);
        var machine = CreateMachine(await StoreWith(p));

        var result = await machine.TransitionAsync(
            p, Request(p, ClassificationWorkflowStateMachine.SystemAutoApproverSid, "DATA_OWNER", token: "x"));

        result.Success.ShouldBeFalse();
    }

    private sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public TestOptionsMonitor(T currentValue) => CurrentValue = currentValue;
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
