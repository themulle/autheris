namespace Autheris.Tests.Unit.Governance;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Autheris.Application.Governance.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

public sealed class ClassificationWorkflowStateMachineTests
{
    [Fact]
    public async Task TransitionAsync_EnforcesSegregationOfDuties_AntiSelfApproval()
    {
        var options = new GatewayOptions { Classification = new ClassificationOptions() };
        var stateMachine = new ClassificationWorkflowStateMachine(
            new TestOptionsMonitor<GatewayOptions>(options),
            NullLogger<ClassificationWorkflowStateMachine>.Instance);

        var ownerSid = "S-1-5-21-finance-owner";
        var proposal = new TableClassificationProposal(
            Guid.NewGuid(),
            new TableIdentifier("db", "finance", "salaries"),
            ownerSid,
            "PENDING_GOVERNANCE_REVIEW",
            new List<ColumnClassificationProposal>
            {
                new("salary_amount", "decimal", "L4_STRICTLY_CONFIDENTIAL", 4, "SALARY", "REDACT", 0.95, false)
            },
            DateTimeOffset.UtcNow);

        // Data owner attempts to act as Governance Reviewer on their own proposal
        var request = new TableClassificationApprovalRequest(
            proposal.ProposalId,
            proposal.TableIdentifier,
            new Sid(ownerSid),
            ReviewerRole: "DATA_GOVERNANCE_REVIEWER",
            Action: "APPROVE",
            ColumnDecisions: new List<ColumnClassificationDecision>
            {
                new("salary_amount", "ACCEPT_PROPOSAL", "L4_STRICTLY_CONFIDENTIAL", 4, "REDACT")
            },
            StepUpAuthToken: "valid-stepup-token");

        var result = await stateMachine.TransitionAsync(proposal, request);

        result.Success.ShouldBeFalse();
        result.ErrorCode.ShouldBe("SOD_ANTI_SELF_APPROVAL_VIOLATION");
        result.Message.ShouldContain("SoD Violation");
    }

    [Fact]
    public async Task TransitionAsync_RequiresStepUpMfa_ForHighSensitivityRank()
    {
        var options = new GatewayOptions { Classification = new ClassificationOptions() };
        var stateMachine = new ClassificationWorkflowStateMachine(
            new TestOptionsMonitor<GatewayOptions>(options),
            NullLogger<ClassificationWorkflowStateMachine>.Instance);

        var proposal = new TableClassificationProposal(
            Guid.NewGuid(),
            new TableIdentifier("db", "hr", "payroll"),
            "owner@corp.local",
            "PENDING_OWNER_REVIEW",
            new List<ColumnClassificationProposal>
            {
                new("pan_card", "varchar(16)", "L4_STRICTLY_CONFIDENTIAL", 4, "CREDIT_CARD", "CREDIT_CARD_LAST_4", 0.95, false)
            },
            DateTimeOffset.UtcNow);

        // Approval attempt without Step-Up MFA token for Rank 4
        var requestWithoutMfa = new TableClassificationApprovalRequest(
            proposal.ProposalId,
            proposal.TableIdentifier,
            new Sid("owner@corp.local"),
            ReviewerRole: "DATA_OWNER",
            Action: "APPROVE",
            ColumnDecisions: new List<ColumnClassificationDecision>
            {
                new("pan_card", "ACCEPT_PROPOSAL", "L4_STRICTLY_CONFIDENTIAL", 4, "CREDIT_CARD_LAST_4")
            },
            StepUpAuthToken: null); // Missing token

        var resultWithoutMfa = await stateMachine.TransitionAsync(proposal, requestWithoutMfa);
        resultWithoutMfa.Success.ShouldBeFalse();
        resultWithoutMfa.RequiresStepUpAuth.ShouldBeTrue();
        resultWithoutMfa.ErrorCode.ShouldBe("STEP_UP_AUTH_REQUIRED");

        // Approval attempt with Step-Up MFA token
        var requestWithMfa = new TableClassificationApprovalRequest(
            proposal.ProposalId,
            proposal.TableIdentifier,
            new Sid("owner@corp.local"),
            ReviewerRole: "DATA_OWNER",
            Action: "APPROVE",
            ColumnDecisions: new List<ColumnClassificationDecision>
            {
                new("pan_card", "ACCEPT_PROPOSAL", "L4_STRICTLY_CONFIDENTIAL", 4, "CREDIT_CARD_LAST_4")
            },
            StepUpAuthToken: "valid-mfa-token-12345");

        var resultWithMfa = await stateMachine.TransitionAsync(proposal, requestWithMfa);
        resultWithMfa.Success.ShouldBeTrue();
        resultWithMfa.RequiresStepUpAuth.ShouldBeFalse();
    }

    [Fact]
    public async Task TransitionAsync_TwoStageApproval_ProgressesFromOwnerToGovernanceReviewer()
    {
        var options = new GatewayOptions { Classification = new ClassificationOptions() };
        var stateMachine = new ClassificationWorkflowStateMachine(
            new TestOptionsMonitor<GatewayOptions>(options),
            NullLogger<ClassificationWorkflowStateMachine>.Instance);

        var proposal = new TableClassificationProposal(
            Guid.NewGuid(),
            new TableIdentifier("db", "finance", "invoices"),
            "owner@corp.local",
            "PENDING_OWNER_REVIEW",
            new List<ColumnClassificationProposal>
            {
                new("iban", "varchar(34)", "L3_CONFIDENTIAL", 3, "IBAN", "IBAN_STANDARD_4_4", 0.95, false)
            },
            DateTimeOffset.UtcNow);

        // Step 1: Data Owner approves (Rank 3 >= FourEyesThresholdRank 3) -> Advances to PENDING_GOVERNANCE_REVIEW
        var ownerRequest = new TableClassificationApprovalRequest(
            proposal.ProposalId,
            proposal.TableIdentifier,
            new Sid("owner@corp.local"),
            ReviewerRole: "DATA_OWNER",
            Action: "APPROVE",
            ColumnDecisions: new List<ColumnClassificationDecision>
            {
                new("iban", "ACCEPT_PROPOSAL", "L3_CONFIDENTIAL", 3, "IBAN_STANDARD_4_4")
            });

        var ownerResult = await stateMachine.TransitionAsync(proposal, ownerRequest);
        ownerResult.Success.ShouldBeTrue();
        ownerResult.NewStatus.ShouldBe("PENDING_GOVERNANCE_REVIEW");
        ownerResult.RequiresSecondStage.ShouldBeTrue();

        // Step 2: Governance Reviewer (independent SID) approves -> CLASSIFIED
        var reviewerRequest = new TableClassificationApprovalRequest(
            proposal.ProposalId,
            proposal.TableIdentifier,
            new Sid("dpo-reviewer@corp.local"),
            ReviewerRole: "DATA_GOVERNANCE_REVIEWER",
            Action: "APPROVE",
            ColumnDecisions: new List<ColumnClassificationDecision>
            {
                new("iban", "ACCEPT_PROPOSAL", "L3_CONFIDENTIAL", 3, "IBAN_STANDARD_4_4")
            });

        var reviewerResult = await stateMachine.TransitionAsync(proposal, reviewerRequest);
        reviewerResult.Success.ShouldBeTrue();
        reviewerResult.NewStatus.ShouldBe("CLASSIFIED");
    }

    [Fact]
    public async Task CanZeroTouchAutoApprove_ApprovesLowRiskPublicDataWhenEnabled()
    {
        var options = new GatewayOptions
        {
            Classification = new ClassificationOptions
            {
                Workflow = new WorkflowOptions
                {
                    ActiveProfile = "LEAN_DATA_MESH" // ZeroTouchAutoApprovePublicAndLowRisk is true, MaxRank is 2
                }
            }
        };

        var stateMachine = new ClassificationWorkflowStateMachine(
            new TestOptionsMonitor<GatewayOptions>(options),
            NullLogger<ClassificationWorkflowStateMachine>.Instance);

        var publicTable = new TableClassificationProposal(
            Guid.NewGuid(),
            new TableIdentifier("db", "public", "iso_currencies"),
            "steward@corp.local",
            "PENDING_OWNER_REVIEW",
            new List<ColumnClassificationProposal>
            {
                new("currency_code", "char(3)", "L1_PUBLIC", 1, null, "NONE", Confidence: 0.98, IsDisputed: false),
                new("currency_name", "varchar(50)", "L1_PUBLIC", 1, null, "NONE", Confidence: 0.99, IsDisputed: false)
            },
            DateTimeOffset.UtcNow);

        stateMachine.CanZeroTouchAutoApprove(publicTable).ShouldBeTrue();

        // If a column is disputed, it should NOT auto-approve
        var disputedTable = new TableClassificationProposal(
            Guid.NewGuid(),
            new TableIdentifier("db", "public", "ambiguous"),
            "steward@corp.local",
            "PENDING_OWNER_REVIEW",
            new List<ColumnClassificationProposal>
            {
                new("code", "varchar(10)", "L1_PUBLIC", 1, null, "NONE", Confidence: 0.60, IsDisputed: true)
            },
            DateTimeOffset.UtcNow);

        stateMachine.CanZeroTouchAutoApprove(disputedTable).ShouldBeFalse();
    }

    private sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public TestOptionsMonitor(T currentValue) => CurrentValue = currentValue;
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
