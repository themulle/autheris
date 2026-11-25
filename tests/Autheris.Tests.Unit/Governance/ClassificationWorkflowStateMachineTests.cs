namespace Autheris.Tests.Unit.Governance;

using Autheris.Tests.Common;
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
    public async Task TransitionAsync_RequiresStepUpMfa_ForHighSensitivityRank()
    {
        var options = new GatewayOptions { Classification = new ClassificationOptions() };
        // Step-up tokens are verified (SR-H-04), so a verification service is mandatory here.
        var stateMachine = new ClassificationWorkflowStateMachine(
            new StaticOptionsMonitor<GatewayOptions>(options),
            NullLogger<ClassificationWorkflowStateMachine>.Instance,
            totpService: new SrH04FakeTotpService("valid-mfa-token-12345"),
            totpSecretStore: new SrH04FakeTotpSecretStore());

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
    public async Task TransitionAsync_SingleApproval_OneDecisionClassifies()
    {
        var options = new GatewayOptions { Classification = new ClassificationOptions() };
        var stateMachine = new ClassificationWorkflowStateMachine(
            new StaticOptionsMonitor<GatewayOptions>(options),
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

        // Single approval (Amendment 2): the Data Owner's decision classifies a rank-3 proposal immediately, without a second stage.
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
        ownerResult.NewStatus.ShouldBe("CLASSIFIED");

        // A Data Governance member decides alone as well, also on a legacy proposal still waiting for the retired second stage.
        var reviewerRequest = new TableClassificationApprovalRequest(
            proposal.ProposalId,
            proposal.TableIdentifier,
            new Sid("dg-member@corp.local"),
            ReviewerRole: "DATA_GOVERNANCE_REVIEWER",
            Action: "APPROVE",
            ColumnDecisions: new List<ColumnClassificationDecision>
            {
                new("iban", "ACCEPT_PROPOSAL", "L3_CONFIDENTIAL", 3, "IBAN_STANDARD_4_4")
            });

        var legacyPending = proposal with { Status = "PENDING_GOVERNANCE_REVIEW" };
        var reviewerResult = await stateMachine.TransitionAsync(legacyPending, reviewerRequest);
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
            new StaticOptionsMonitor<GatewayOptions>(options),
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
}
