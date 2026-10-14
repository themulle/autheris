namespace Autheris.Application.Governance.Services;

using System;
using System.Collections.Generic;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Autheris.Application.Security.Totp.Interfaces;

/// <summary>
/// Implements the classification workflow state machine with SoD enforcement, Step-Up MFA validation,
/// conditional four-eyes rules, and zero-touch auto-approvals.
/// </summary>
public sealed class ClassificationWorkflowStateMachine : IClassificationWorkflowStateMachine
{
    private readonly IOptionsMonitor<GatewayOptions> _gatewayOptions;
    private readonly IClassificationProposalStore? _proposalStore;
    private readonly IEnumerable<IGovernanceWorkflowHook>? _hooks;
    private readonly ITotpVerificationService? _totpService;
    private readonly ITotpSecretStore? _totpSecretStore;
    private readonly ILogger<ClassificationWorkflowStateMachine> _logger;

    public ClassificationWorkflowStateMachine(
        IOptionsMonitor<GatewayOptions> gatewayOptions,
        ILogger<ClassificationWorkflowStateMachine> logger,
        IClassificationProposalStore? proposalStore = null,
        IEnumerable<IGovernanceWorkflowHook>? hooks = null,
        ITotpVerificationService? totpService = null,
        ITotpSecretStore? totpSecretStore = null)
    {
        _gatewayOptions = gatewayOptions ?? throw new ArgumentNullException(nameof(gatewayOptions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _proposalStore = proposalStore;
        _hooks = hooks;
        _totpService = totpService;
        _totpSecretStore = totpSecretStore;
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

    public async ValueTask<TableClassificationApprovalResult> TransitionAsync(
        TableClassificationProposal proposal,
        TableClassificationApprovalRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(request);

        var options = _gatewayOptions.CurrentValue.Classification ?? new ClassificationOptions();
        var profile = options.Workflow.GetActiveProfile();
        var pipeline = profile.ApprovalPipeline;

        using var activity = Diagnostics.ClassificationDiagnostics.ActivitySource.StartActivity("autheris.governance.workflow_transition");
        activity?.SetTag("proposal.id", proposal.ProposalId);
        activity?.SetTag("table.id", proposal.TableIdentifier.ToQualifiedName());
        activity?.SetTag("reviewer.role", request.ReviewerRole);
        activity?.SetTag("action", request.Action);

        // The stored proposal is authoritative: a caller-supplied copy (status, owner, ranks, approvers) is never trusted.
        if (_proposalStore != null)
        {
            if (request.ProposalId != proposal.ProposalId)
            {
                return Fail(proposal, request, "PROPOSAL_ID_MISMATCH", "Request proposal ID does not match the proposal.");
            }

            var stored = await _proposalStore.GetProposalByIdAsync(proposal.ProposalId, ct).ConfigureAwait(false);
            if (stored == null)
            {
                return Fail(proposal, request, "PROPOSAL_NOT_FOUND", "Classification proposal was not found.");
            }

            proposal = stored;
        }

        TableClassificationApprovalResult result;
        var isReject = string.Equals(request.Action, "REJECT", StringComparison.OrdinalIgnoreCase);
        var isApprove = string.Equals(request.Action, "APPROVE", StringComparison.OrdinalIgnoreCase);
        var isOwnerRole = string.Equals(request.ReviewerRole, "DATA_OWNER", StringComparison.OrdinalIgnoreCase);
        var isGovRole = string.Equals(request.ReviewerRole, "DATA_GOVERNANCE_REVIEWER", StringComparison.OrdinalIgnoreCase);
        var reviewerSid = request.ReviewerSid.Value;

        if (!isReject && !isApprove)
        {
            return Fail(proposal, request, "INVALID_ACTION", $"Unknown action '{request.Action}'.");
        }

        if (string.IsNullOrWhiteSpace(reviewerSid))
        {
            return Fail(proposal, request, "INVALID_REVIEWER", "Reviewer identity is required.");
        }

        if (!isOwnerRole && !isGovRole)
        {
            return Fail(proposal, request, "INVALID_REVIEWER_ROLE", $"Unknown reviewer role '{request.ReviewerRole}'.");
        }

        var isStage1Status = IsStatus(proposal, "UNCLASSIFIED") || IsStatus(proposal, "PENDING_OWNER_REVIEW");
        var isStage2Status = IsStatus(proposal, "PENDING_GOVERNANCE_REVIEW");
        if (!isStage1Status && !isStage2Status)
        {
            // CLASSIFIED / REJECTED (and anything unknown) are terminal: sealed proposals cannot be re-approved or re-rejected.
            return Fail(proposal, request, "INVALID_WORKFLOW_STATE",
                $"Proposal in status '{proposal.Status}' cannot be transitioned.");
        }

        if (isReject)
        {
            _logger.LogInformation("Proposal {ProposalId} rejected by {Reviewer}.", proposal.ProposalId, request.ReviewerSid);
            result = new TableClassificationApprovalResult(
                Success: true,
                NewStatus: "REJECTED",
                Message: "Classification proposal was rejected.");
            Diagnostics.ClassificationDiagnostics.RecordTransition(request.Action, request.ReviewerRole, "REJECTED");
        }
        else if ((isOwnerRole && !isStage1Status) || (isGovRole && !isStage2Status))
        {
            // Stage 1 belongs to the Data Owner, Stage 2 only exists after Stage 1 (PENDING_GOVERNANCE_REVIEW).
            return Fail(proposal, request, "INVALID_WORKFLOW_STATE",
                $"Role '{request.ReviewerRole}' cannot approve a proposal in status '{proposal.Status}'.");
        }
        else if (isGovRole &&
                 !string.IsNullOrWhiteSpace(proposal.DataOwnerSid) &&
                 string.Equals(proposal.DataOwnerSid, reviewerSid, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("SoD Violation: Data Owner {Sid} attempted to act as Governance Reviewer on proposal {ProposalId}.",
                proposal.DataOwnerSid, proposal.ProposalId);

            result = new TableClassificationApprovalResult(
                Success: false,
                NewStatus: proposal.Status,
                Message: "SoD Violation: Data Owner cannot act as Governance Reviewer on their own proposal.",
                ErrorCode: "SOD_ANTI_SELF_APPROVAL_VIOLATION");

            Diagnostics.ClassificationDiagnostics.RecordSodViolation(proposal.TableIdentifier.ToQualifiedName());
            Diagnostics.ClassificationDiagnostics.RecordTransition(request.Action, request.ReviewerRole, "BLOCKED_SOD");
        }
        else
        {
            // Effective maximum rank across ALL proposal columns. Caller-supplied decisions may only raise a rank,
            // never lower it, so they cannot slip below the step-up / four-eyes thresholds.
            var maxEffectiveRank = 0;
            var overrideMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (request.ColumnDecisions != null)
            {
                foreach (var dec in request.ColumnDecisions)
                {
                    overrideMap[dec.ColumnName] = overrideMap.TryGetValue(dec.ColumnName, out var prev)
                        ? Math.Max(prev, dec.EffectiveSensitivityRank)
                        : dec.EffectiveSensitivityRank;
                }
            }

            foreach (var col in proposal.Columns)
            {
                var rank = col.ProposedSensitivityRank;
                if (overrideMap.TryGetValue(col.ColumnName, out var ovr) && ovr > rank)
                {
                    rank = ovr;
                }

                if (rank > maxEffectiveRank)
                {
                    maxEffectiveRank = rank;
                }
            }

            foreach (var ovr in overrideMap.Values)
            {
                if (ovr > maxEffectiveRank)
                {
                    maxEffectiveRank = ovr;
                }
            }

            var stepUpRequired = maxEffectiveRank >= pipeline.RequireStepUpAuthThresholdRank;
            var requiresFourEyes = RequiresFourEyes(proposal, pipeline, maxEffectiveRank);

            if (requiresFourEyes)
            {
                var distinctViolation = CheckDistinctApprovers(proposal, reviewerSid, isGovRole);
                if (distinctViolation != null)
                {
                    _logger.LogWarning("Four-eyes violation on proposal {ProposalId}: {Reason}", proposal.ProposalId, distinctViolation);
                    Diagnostics.ClassificationDiagnostics.RecordSodViolation(proposal.TableIdentifier.ToQualifiedName());
                    Diagnostics.ClassificationDiagnostics.RecordTransition(request.Action, request.ReviewerRole, "BLOCKED_SOD");
                    return Fail(proposal, request, "DISTINCT_APPROVER_REQUIRED", distinctViolation, recordTransition: false);
                }
            }

            if (stepUpRequired && string.IsNullOrWhiteSpace(request.StepUpAuthToken))
            {
                _logger.LogWarning("Step-Up 2FA required for high sensitivity rank {Rank} on proposal {ProposalId}.",
                    maxEffectiveRank, proposal.ProposalId);

                result = new TableClassificationApprovalResult(
                    Success: false,
                    NewStatus: proposal.Status,
                    Message: $"Step-Up 2FA/MFA token is required for sensitivity rank {maxEffectiveRank}.",
                    RequiresStepUpAuth: true,
                    ErrorCode: "STEP_UP_AUTH_REQUIRED");

                Diagnostics.ClassificationDiagnostics.RecordStepUpChallenge(maxEffectiveRank);
                Diagnostics.ClassificationDiagnostics.RecordTransition(request.Action, request.ReviewerRole, "STEP_UP_REQUIRED");
            }
            else if (stepUpRequired && (_totpService == null || _totpSecretStore == null))
            {
                // Fail closed: a non-empty token is never accepted without real verification.
                _logger.LogError("Step-Up 2FA is required for proposal {ProposalId} but no TOTP verification service is configured.", proposal.ProposalId);
                result = new TableClassificationApprovalResult(
                    Success: false,
                    NewStatus: proposal.Status,
                    Message: "Step-Up 2FA/MFA verification is unavailable.",
                    RequiresStepUpAuth: true,
                    ErrorCode: "STEP_UP_AUTH_UNAVAILABLE");
                Diagnostics.ClassificationDiagnostics.RecordTransition(request.Action, request.ReviewerRole, "STEP_UP_FAILED");
            }
            else if (stepUpRequired)
            {
                var secret = await _totpSecretStore!.GetSecretAsync(reviewerSid, ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(secret) ||
                    !await _totpService!.VerifyAndConsumeTotpAsync(reviewerSid, secret, request.StepUpAuthToken!, ct).ConfigureAwait(false))
                {
                    _logger.LogWarning("Invalid or consumed Step-Up 2FA token submitted by {Reviewer} for proposal {ProposalId}.",
                        request.ReviewerSid, proposal.ProposalId);

                    result = new TableClassificationApprovalResult(
                        Success: false,
                        NewStatus: proposal.Status,
                        Message: "Invalid or consumed Step-Up 2FA/MFA verification token.",
                        RequiresStepUpAuth: true,
                        ErrorCode: "INVALID_STEP_UP_AUTH_TOKEN");

                    Diagnostics.ClassificationDiagnostics.RecordTransition(request.Action, request.ReviewerRole, "STEP_UP_FAILED");
                    return result;
                }

                result = EvaluateApprovalProgression(proposal, request, pipeline, maxEffectiveRank);
            }
            else
            {
                result = EvaluateApprovalProgression(proposal, request, pipeline, maxEffectiveRank);
            }
        }

        if (result.Success && _proposalStore != null)
        {
            var updatedProposal = proposal with { Status = result.NewStatus };
            if (isApprove)
            {
                // Persist who approved each stage so the next stage can require a distinct principal.
                updatedProposal = string.Equals(result.NewStatus, "PENDING_GOVERNANCE_REVIEW", StringComparison.Ordinal)
                    ? updatedProposal with { Stage1ApproverSid = reviewerSid }
                    : isGovRole
                        ? updatedProposal with { Stage2ApproverSid = reviewerSid }
                        : updatedProposal with { Stage1ApproverSid = reviewerSid };
            }

            await _proposalStore.SaveProposalAsync(updatedProposal, ct).ConfigureAwait(false);
        }

        if (_hooks != null)
        {
            foreach (var hook in _hooks)
            {
                await hook.OnWorkflowTransitionAsync(proposal, request, result, ct).ConfigureAwait(false);
            }
        }

        return result;
    }

    private TableClassificationApprovalResult EvaluateApprovalProgression(
        TableClassificationProposal proposal,
        TableClassificationApprovalRequest request,
        ApprovalPipelineConfig pipeline,
        int maxEffectiveRank)
    {
        var requiresFourEyes = RequiresFourEyes(proposal, pipeline, maxEffectiveRank);

        // Stage 2 only exists after Stage 1; it always seals, regardless of recomputed thresholds.
        if (!requiresFourEyes && !IsStatus(proposal, "PENDING_GOVERNANCE_REVIEW"))
        {
            // 1-Stage Mode: Immediately classified
            _logger.LogInformation("Proposal {ProposalId} approved in 1-stage mode (FourEyes disabled or threshold not reached).", proposal.ProposalId);
            var res = new TableClassificationApprovalResult(
                Success: true,
                NewStatus: "CLASSIFIED",
                Message: "Classification activated and approved.");
            Diagnostics.ClassificationDiagnostics.RecordTransition(request.Action, request.ReviewerRole, "CLASSIFIED");
            return res;
        }

        if (string.Equals(request.ReviewerRole, "DATA_OWNER", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("Proposal {ProposalId} approved by Data Owner; advancing to Compliance Reviewer.", proposal.ProposalId);
            var res = new TableClassificationApprovalResult(
                Success: true,
                NewStatus: "PENDING_GOVERNANCE_REVIEW",
                Message: "Stage 1 approved. Awaiting final Governance Compliance review.",
                RequiresSecondStage: true);
            Diagnostics.ClassificationDiagnostics.RecordTransition(request.Action, request.ReviewerRole, "PENDING_GOVERNANCE_REVIEW");
            return res;
        }

        if (string.Equals(request.ReviewerRole, "DATA_GOVERNANCE_REVIEWER", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("Proposal {ProposalId} sealed and approved by Governance Reviewer.", proposal.ProposalId);
            var res = new TableClassificationApprovalResult(
                Success: true,
                NewStatus: "CLASSIFIED",
                Message: "Classification sealed and activated cluster-wide.");
            Diagnostics.ClassificationDiagnostics.RecordTransition(request.Action, request.ReviewerRole, "CLASSIFIED");
            return res;
        }

        var unknownRole = new TableClassificationApprovalResult(
            Success: false,
            NewStatus: proposal.Status,
            Message: $"Unknown reviewer role '{request.ReviewerRole}'.",
            ErrorCode: "INVALID_REVIEWER_ROLE");
        Diagnostics.ClassificationDiagnostics.RecordTransition(request.Action, request.ReviewerRole, "INVALID_ROLE");
        return unknownRole;
    }

    private static bool IsStatus(TableClassificationProposal proposal, string status) =>
        string.Equals(proposal.Status, status, StringComparison.OrdinalIgnoreCase);

    private static bool RequiresFourEyes(TableClassificationProposal proposal, ApprovalPipelineConfig pipeline, int maxEffectiveRank) =>
        pipeline.EnableFourEyes &&
        (maxEffectiveRank >= pipeline.FourEyesThresholdRank ||
         (pipeline.RequireSecondStageOnDisputed && HasDisputedColumns(proposal)));

    private static string? CheckDistinctApprovers(TableClassificationProposal proposal, string reviewerSid, bool isStage2)
    {
        if (!string.IsNullOrWhiteSpace(proposal.ProposedBySid) &&
            string.Equals(proposal.ProposedBySid, reviewerSid, StringComparison.OrdinalIgnoreCase))
        {
            return "Four-eyes: the approver must differ from the principal who proposed the classification.";
        }

        if (isStage2 &&
            (string.IsNullOrWhiteSpace(proposal.Stage1ApproverSid) ||
             string.Equals(proposal.Stage1ApproverSid, reviewerSid, StringComparison.OrdinalIgnoreCase)))
        {
            // Missing Stage 1 approver on a Stage 2 proposal means the record is untrustworthy: fail closed.
            return "Four-eyes: Stage 2 must be approved by a different principal than Stage 1.";
        }

        return null;
    }

    private TableClassificationApprovalResult Fail(
        TableClassificationProposal proposal,
        TableClassificationApprovalRequest request,
        string errorCode,
        string message,
        bool recordTransition = true)
    {
        _logger.LogWarning("Classification transition denied for proposal {ProposalId} ({ErrorCode}).", proposal.ProposalId, errorCode);
        if (recordTransition)
        {
            Diagnostics.ClassificationDiagnostics.RecordTransition(request.Action, request.ReviewerRole, "DENIED_" + errorCode);
        }

        return new TableClassificationApprovalResult(
            Success: false,
            NewStatus: proposal.Status,
            Message: message,
            ErrorCode: errorCode);
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
