namespace Autheris.Application.Governance.Services;

using System;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Implements the classification workflow state machine with SoD enforcement, Step-Up MFA validation,
/// conditional four-eyes rules, and zero-touch auto-approvals.
/// </summary>
public sealed class ClassificationWorkflowStateMachine : IClassificationWorkflowStateMachine
{
    private readonly IOptionsMonitor<GatewayOptions> _gatewayOptions;
    private readonly ILogger<ClassificationWorkflowStateMachine> _logger;

    public ClassificationWorkflowStateMachine(
        IOptionsMonitor<GatewayOptions> gatewayOptions,
        ILogger<ClassificationWorkflowStateMachine> logger)
    {
        _gatewayOptions = gatewayOptions ?? throw new ArgumentNullException(nameof(gatewayOptions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public bool CanZeroTouchAutoApprove(TableClassificationProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);

        var options = _gatewayOptions.CurrentValue.Classification ?? new ClassificationOptions();
        var profile = options.Workflow.GetActiveProfile();

        if (!profile.PreClassification.ZeroTouchAutoApprovePublicAndLowRisk)
        {
            return false;
        }

        if (proposal.Columns.Count == 0)
        {
            return false;
        }

        foreach (var col in proposal.Columns)
        {
            if (col.IsDisputed)
            {
                return false;
            }

            if (col.ProposedSensitivityRank > profile.PreClassification.AutoApproveMaxSensitivityRank)
            {
                return false;
            }

            if (col.Confidence < profile.PreClassification.AutoApproveConfidenceThreshold)
            {
                return false;
            }
        }

        return true;
    }

    public ValueTask<TableClassificationApprovalResult> TransitionAsync(
        TableClassificationProposal proposal,
        TableClassificationApprovalRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(request);

        var options = _gatewayOptions.CurrentValue.Classification ?? new ClassificationOptions();
        var profile = options.Workflow.GetActiveProfile();
        var pipeline = profile.ApprovalPipeline;

        // Action: REJECT
        if (string.Equals(request.Action, "REJECT", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("Proposal {ProposalId} rejected by {Reviewer}.", proposal.ProposalId, request.ReviewerSid);
            return ValueTask.FromResult(new TableClassificationApprovalResult(
                Success: true,
                NewStatus: "REJECTED",
                Message: "Classification proposal was rejected."));
        }

        // Action: APPROVE
        // 1. Segregation of Duties (SoD / Anti-Self-Approval)
        if (string.Equals(request.ReviewerRole, "DATA_GOVERNANCE_REVIEWER", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(proposal.DataOwnerSid) &&
                string.Equals(proposal.DataOwnerSid, request.ReviewerSid.Value, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("SoD Violation: Data Owner {Sid} attempted to act as Governance Reviewer on proposal {ProposalId}.",
                    proposal.DataOwnerSid, proposal.ProposalId);

                return ValueTask.FromResult(new TableClassificationApprovalResult(
                    Success: false,
                    NewStatus: proposal.Status,
                    Message: "SoD Violation: Data Owner cannot act as Governance Reviewer on their own proposal.",
                    ErrorCode: "SOD_ANTI_SELF_APPROVAL_VIOLATION"));
            }
        }

        // 2. Compute effective maximum rank and check for disputed columns
        var maxEffectiveRank = 0;
        foreach (var dec in request.ColumnDecisions)
        {
            if (dec.EffectiveSensitivityRank > maxEffectiveRank)
            {
                maxEffectiveRank = dec.EffectiveSensitivityRank;
            }
        }

        // 3. Step-Up 2FA / MFA Check
        if (maxEffectiveRank >= pipeline.RequireStepUpAuthThresholdRank)
        {
            if (string.IsNullOrWhiteSpace(request.StepUpAuthToken))
            {
                _logger.LogWarning("Step-Up 2FA required for high sensitivity rank {Rank} on proposal {ProposalId}.",
                    maxEffectiveRank, proposal.ProposalId);

                return ValueTask.FromResult(new TableClassificationApprovalResult(
                    Success: false,
                    NewStatus: proposal.Status,
                    Message: $"Step-Up 2FA/MFA token is required for sensitivity rank {maxEffectiveRank}.",
                    RequiresStepUpAuth: true,
                    ErrorCode: "STEP_UP_AUTH_REQUIRED"));
            }
        }

        // 4. Determine single-stage vs. two-stage progression
        var hasDisputed = HasDisputedColumns(proposal);
        var requiresFourEyes = pipeline.EnableFourEyes &&
                               (maxEffectiveRank >= pipeline.FourEyesThresholdRank ||
                                (pipeline.RequireSecondStageOnDisputed && hasDisputed));

        if (!requiresFourEyes)
        {
            // 1-Stage Mode: Immediately classified
            _logger.LogInformation("Proposal {ProposalId} approved in 1-stage mode (FourEyes disabled or threshold not reached).", proposal.ProposalId);
            return ValueTask.FromResult(new TableClassificationApprovalResult(
                Success: true,
                NewStatus: "CLASSIFIED",
                Message: "Classification activated and approved."));
        }

        // 2-Stage Mode: Check current step
        if (string.Equals(request.ReviewerRole, "DATA_OWNER", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("Proposal {ProposalId} approved by Data Owner; advancing to Compliance Reviewer.", proposal.ProposalId);
            return ValueTask.FromResult(new TableClassificationApprovalResult(
                Success: true,
                NewStatus: "PENDING_GOVERNANCE_REVIEW",
                Message: "Stage 1 approved. Awaiting final Governance Compliance review.",
                RequiresSecondStage: true));
        }
        else if (string.Equals(request.ReviewerRole, "DATA_GOVERNANCE_REVIEWER", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("Proposal {ProposalId} sealed and approved by Governance Reviewer.", proposal.ProposalId);
            return ValueTask.FromResult(new TableClassificationApprovalResult(
                Success: true,
                NewStatus: "CLASSIFIED",
                Message: "Classification sealed and activated cluster-wide."));
        }

        return ValueTask.FromResult(new TableClassificationApprovalResult(
            Success: false,
            NewStatus: proposal.Status,
            Message: $"Unknown reviewer role '{request.ReviewerRole}'.",
            ErrorCode: "INVALID_REVIEWER_ROLE"));
    }

    private static bool HasDisputedColumns(TableClassificationProposal proposal)
    {
        foreach (var col in proposal.Columns)
        {
            if (col.IsDisputed)
            {
                return true;
            }
        }
        return false;
    }
}
