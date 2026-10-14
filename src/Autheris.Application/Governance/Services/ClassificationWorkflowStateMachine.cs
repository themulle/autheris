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

using Autheris.Application.Security.Totp.Interfaces;

/// <summary>
/// Implements the classification workflow state machine with SoD enforcement, Step-Up MFA validation,
/// conditional four-eyes rules, and zero-touch auto-approvals.
/// </summary>
public sealed class ClassificationWorkflowStateMachine : IClassificationWorkflowStateMachine
{
    /// <summary>Reviewer identity used by the zero-touch auto-approval path (only honored for eligible low-risk proposals).</summary>
    public const string SystemAutoApproverSid = "SYSTEM_AUTO_APPROVER";

    private readonly SemaphoreSlim _transitionLock = new(1, 1);
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

        // SR-H-04: load -> validate -> persist must be atomic, otherwise two concurrent calls could both pass the
        // state precondition (e.g. the same stage twice).
        await _transitionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await TransitionCoreAsync(proposal, request, ct).ConfigureAwait(false);
        }
        finally
        {
            _transitionLock.Release();
        }
    }

    private async ValueTask<TableClassificationApprovalResult> TransitionCoreAsync(
        TableClassificationProposal suppliedProposal,
        TableClassificationApprovalRequest request,
        CancellationToken ct)
    {
        var proposal = suppliedProposal;

        // SR-H-04: the authoritative proposal (status, data owner, stage-1 approver) comes from the store; a
        // caller-supplied copy is never trusted when a store is available.
        if (_proposalStore != null)
        {
            var stored = await _proposalStore.GetProposalByIdAsync(request.ProposalId, ct).ConfigureAwait(false);
            if (stored == null)
            {
                return Deny(suppliedProposal, request, "PROPOSAL_NOT_FOUND", "Classification proposal was not found.", "PROPOSAL_NOT_FOUND");
            }

            proposal = stored;
        }

        if (proposal.ProposalId != request.ProposalId)
        {
            return Deny(proposal, request, "PROPOSAL_MISMATCH", "The request does not belong to this proposal.", "PROPOSAL_MISMATCH");
        }

        var options = _gatewayOptions.CurrentValue.Classification ?? new ClassificationOptions();
        var profile = options.Workflow.GetActiveProfile();
        var pipeline = profile.ApprovalPipeline;

        using var activity = Diagnostics.ClassificationDiagnostics.ActivitySource.StartActivity("autheris.governance.workflow_transition");
        activity?.SetTag("proposal.id", proposal.ProposalId);
        activity?.SetTag("table.id", proposal.TableIdentifier.ToQualifiedName());
        activity?.SetTag("reviewer.role", request.ReviewerRole);
        activity?.SetTag("action", request.Action);

        var isReject = string.Equals(request.Action, "REJECT", StringComparison.OrdinalIgnoreCase);
        var isApprove = string.Equals(request.Action, "APPROVE", StringComparison.OrdinalIgnoreCase);
        var isOwnerRole = string.Equals(request.ReviewerRole, "DATA_OWNER", StringComparison.OrdinalIgnoreCase);
        var isReviewerRole = string.Equals(request.ReviewerRole, "DATA_GOVERNANCE_REVIEWER", StringComparison.OrdinalIgnoreCase);
        var reviewerSid = request.ReviewerSid.Value;
        var isSystemAutoApprover = string.Equals(reviewerSid, SystemAutoApproverSid, StringComparison.Ordinal);

        if (!isReject && !isApprove)
        {
            return Deny(proposal, request, "INVALID_ACTION", $"Unknown action '{request.Action}'.", "INVALID_ACTION");
        }

        if (!isOwnerRole && !isReviewerRole)
        {
            return Deny(proposal, request, "INVALID_REVIEWER_ROLE", $"Unknown reviewer role '{request.ReviewerRole}'.", "INVALID_ROLE");
        }

        if (string.IsNullOrWhiteSpace(reviewerSid))
        {
            return Deny(proposal, request, "REVIEWER_IDENTITY_REQUIRED", "A reviewer identity is required.", "NO_REVIEWER");
        }

        // SR-H-04: state precondition per transition (no re-approval of finalized proposals, no stage skipping).
        var isZeroTouch = isSystemAutoApprover && isApprove && isOwnerRole && CanZeroTouchAutoApprove(proposal);
        var status = proposal.Status;
        var statePermitted = isReject
            ? IsStatus(status, "UNCLASSIFIED") || IsStatus(status, "PENDING_OWNER_REVIEW") || IsStatus(status, "PENDING_GOVERNANCE_REVIEW")
            : isOwnerRole
                ? IsStatus(status, "PENDING_OWNER_REVIEW") || (isZeroTouch && IsStatus(status, "UNCLASSIFIED"))
                : IsStatus(status, "PENDING_GOVERNANCE_REVIEW");
        if (!statePermitted)
        {
            _logger.LogWarning("Transition {Action} by role {Role} rejected for proposal {ProposalId}: invalid status {Status}.",
                request.Action, request.ReviewerRole, proposal.ProposalId, status);
            return Deny(proposal, request, "INVALID_STATE_TRANSITION",
                $"The action '{request.Action}' is not permitted for a proposal in status '{status}'.", "INVALID_STATE");
        }

        // SR-H-04: SoD - the data owner can never act as governance reviewer on their own proposal.
        if (isReviewerRole &&
            !string.IsNullOrWhiteSpace(proposal.DataOwnerSid) &&
            string.Equals(proposal.DataOwnerSid, reviewerSid, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("SoD Violation: Data Owner {Sid} attempted to act as Governance Reviewer on proposal {ProposalId}.",
                proposal.DataOwnerSid, proposal.ProposalId);

            Diagnostics.ClassificationDiagnostics.RecordSodViolation(proposal.TableIdentifier.ToQualifiedName());
            Diagnostics.ClassificationDiagnostics.RecordTransition(request.Action, request.ReviewerRole, "BLOCKED_SOD");
            return new TableClassificationApprovalResult(
                Success: false,
                NewStatus: proposal.Status,
                Message: "SoD Violation: Data Owner cannot act as Governance Reviewer on their own proposal.",
                ErrorCode: "SOD_ANTI_SELF_APPROVAL_VIOLATION");
        }

        // SR-H-04: data-owner actions require a non-empty owner assignment that matches the (authenticated) reviewer.
        if (isOwnerRole && !isZeroTouch &&
            (string.IsNullOrWhiteSpace(proposal.DataOwnerSid) ||
             !string.Equals(proposal.DataOwnerSid, reviewerSid, StringComparison.OrdinalIgnoreCase)))
        {
            _logger.LogWarning("Data-owner action on proposal {ProposalId} denied: reviewer is not the assigned data owner.", proposal.ProposalId);
            return Deny(proposal, request, "DATA_OWNER_MISMATCH",
                "Only the assigned data owner can perform data-owner actions on this proposal.", "BLOCKED_NOT_OWNER");
        }

        // SR-H-04: stage 2 requires a recorded stage-1 approval by a different identity.
        if (isApprove && isReviewerRole)
        {
            if (string.IsNullOrWhiteSpace(proposal.Stage1ApproverSid))
            {
                return Deny(proposal, request, "STAGE1_APPROVAL_REQUIRED",
                    "Stage 1 (data owner) approval has not been recorded for this proposal.", "BLOCKED_NO_STAGE1");
            }

            if (string.Equals(proposal.Stage1ApproverSid, reviewerSid, StringComparison.OrdinalIgnoreCase))
            {
                Diagnostics.ClassificationDiagnostics.RecordSodViolation(proposal.TableIdentifier.ToQualifiedName());
                Diagnostics.ClassificationDiagnostics.RecordTransition(request.Action, request.ReviewerRole, "BLOCKED_SOD");
                return new TableClassificationApprovalResult(
                    Success: false,
                    NewStatus: proposal.Status,
                    Message: "SoD Violation: The stage 2 approver must differ from the stage 1 approver.",
                    ErrorCode: "SOD_DISTINCT_APPROVER_VIOLATION");
            }
        }

        TableClassificationApprovalResult result;

        if (isReject)
        {
            _logger.LogInformation("Proposal {ProposalId} rejected by {Reviewer}.", proposal.ProposalId, request.ReviewerSid);
            result = new TableClassificationApprovalResult(
                Success: true,
                NewStatus: "REJECTED",
                Message: "Classification proposal was rejected.");
            Diagnostics.ClassificationDiagnostics.RecordTransition(request.Action, request.ReviewerRole, "REJECTED");
        }
        else
        {
            // 2. Compute effective maximum rank across ALL columns in the proposal. A reviewer-supplied override can
            // raise but never lower the rank used for step-up / four-eyes thresholds (SR-H-04).
            var maxEffectiveRank = 0;
            var overrideMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (request.ColumnDecisions != null)
            {
                foreach (var dec in request.ColumnDecisions)
                {
                    overrideMap[dec.ColumnName] = dec.EffectiveSensitivityRank;
                }
            }

            foreach (var col in proposal.Columns)
            {
                var rank = overrideMap.TryGetValue(col.ColumnName, out var ovr)
                    ? Math.Max(ovr, col.ProposedSensitivityRank)
                    : col.ProposedSensitivityRank;
                if (rank > maxEffectiveRank)
                {
                    maxEffectiveRank = rank;
                }
            }

            // 3. Step-Up 2FA / MFA Check
            var stepUpRequired = maxEffectiveRank >= pipeline.RequireStepUpAuthThresholdRank;
            if (stepUpRequired && string.IsNullOrWhiteSpace(request.StepUpAuthToken))
            {
                _logger.LogWarning("Step-Up 2FA required for high sensitivity rank {Rank} on proposal {ProposalId}.",
                    maxEffectiveRank, proposal.ProposalId);

                Diagnostics.ClassificationDiagnostics.RecordStepUpChallenge(maxEffectiveRank);
                Diagnostics.ClassificationDiagnostics.RecordTransition(request.Action, request.ReviewerRole, "STEP_UP_REQUIRED");
                return new TableClassificationApprovalResult(
                    Success: false,
                    NewStatus: proposal.Status,
                    Message: $"Step-Up 2FA/MFA token is required for sensitivity rank {maxEffectiveRank}.",
                    RequiresStepUpAuth: true,
                    ErrorCode: "STEP_UP_AUTH_REQUIRED");
            }

            if (stepUpRequired)
            {
                // SR-H-04: fail closed - a non-empty token alone is never sufficient; it must verify as a TOTP.
                if (_totpService == null || _totpSecretStore == null)
                {
                    _logger.LogError("Step-Up 2FA is required for proposal {ProposalId} but no TOTP verification service is configured.", proposal.ProposalId);
                    Diagnostics.ClassificationDiagnostics.RecordTransition(request.Action, request.ReviewerRole, "STEP_UP_UNAVAILABLE");
                    return new TableClassificationApprovalResult(
                        Success: false,
                        NewStatus: proposal.Status,
                        Message: "Step-Up 2FA/MFA verification is not available.",
                        RequiresStepUpAuth: true,
                        ErrorCode: "STEP_UP_AUTH_UNAVAILABLE");
                }

                var secret = await _totpSecretStore.GetSecretAsync(reviewerSid, ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(secret) ||
                    !await _totpService.VerifyAndConsumeTotpAsync(reviewerSid, secret, request.StepUpAuthToken!, ct).ConfigureAwait(false))
                {
                    _logger.LogWarning("Invalid or consumed Step-Up 2FA token submitted by {Reviewer} for proposal {ProposalId}.",
                        request.ReviewerSid, proposal.ProposalId);

                    Diagnostics.ClassificationDiagnostics.RecordTransition(request.Action, request.ReviewerRole, "STEP_UP_FAILED");
                    return new TableClassificationApprovalResult(
                        Success: false,
                        NewStatus: proposal.Status,
                        Message: "Invalid or consumed Step-Up 2FA/MFA verification token.",
                        RequiresStepUpAuth: true,
                        ErrorCode: "INVALID_STEP_UP_AUTH_TOKEN");
                }
            }

            result = EvaluateApprovalProgression(proposal, request, pipeline, maxEffectiveRank);
        }

        if (result.Success && _proposalStore != null)
        {
            var updatedProposal = proposal with { Status = result.NewStatus };
            if (isApprove && isOwnerRole && string.Equals(result.NewStatus, "PENDING_GOVERNANCE_REVIEW", StringComparison.OrdinalIgnoreCase))
            {
                // SR-H-04: persist the stage-1 approver so stage 2 can enforce distinct approvers.
                updatedProposal = updatedProposal with { Stage1ApproverSid = reviewerSid };
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

    private static bool IsStatus(string status, string expected)
        => string.Equals(status, expected, StringComparison.OrdinalIgnoreCase);

    private static TableClassificationApprovalResult Deny(
        TableClassificationProposal proposal,
        TableClassificationApprovalRequest request,
        string errorCode,
        string message,
        string diagnosticOutcome)
    {
        Diagnostics.ClassificationDiagnostics.RecordTransition(request.Action, request.ReviewerRole, diagnosticOutcome);
        return new TableClassificationApprovalResult(
            Success: false,
            NewStatus: proposal.Status,
            Message: message,
            ErrorCode: errorCode);
    }

    private TableClassificationApprovalResult EvaluateApprovalProgression(
        TableClassificationProposal proposal,
        TableClassificationApprovalRequest request,
        ApprovalPipelineConfig pipeline,
        int maxEffectiveRank)
    {
        var hasDisputed = HasDisputedColumns(proposal);
        var requiresFourEyes = pipeline.EnableFourEyes &&
                               (maxEffectiveRank >= pipeline.FourEyesThresholdRank ||
                                (pipeline.RequireSecondStageOnDisputed && hasDisputed));

        if (!requiresFourEyes)
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
