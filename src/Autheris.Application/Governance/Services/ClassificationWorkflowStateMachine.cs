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

        TableClassificationApprovalResult result;

        // Action: REJECT
        if (string.Equals(request.Action, "REJECT", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("Proposal {ProposalId} rejected by {Reviewer}.", proposal.ProposalId, request.ReviewerSid);
            result = new TableClassificationApprovalResult(
                Success: true,
                NewStatus: "REJECTED",
                Message: "Classification proposal was rejected.");
            Diagnostics.ClassificationDiagnostics.RecordTransition(request.Action, request.ReviewerRole, "REJECTED");
        }
        else if (string.Equals(request.ReviewerRole, "DATA_GOVERNANCE_REVIEWER", StringComparison.OrdinalIgnoreCase) &&
                 !string.IsNullOrWhiteSpace(proposal.DataOwnerSid) &&
                 string.Equals(proposal.DataOwnerSid, request.ReviewerSid.Value, StringComparison.OrdinalIgnoreCase))
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
            // 2. Compute effective maximum rank across ALL columns in the proposal
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
                var rank = overrideMap.TryGetValue(col.ColumnName, out var ovr) ? ovr : col.ProposedSensitivityRank;
                if (rank > maxEffectiveRank)
                {
                    maxEffectiveRank = rank;
                }
            }

            // 3. Step-Up 2FA / MFA Check
            if (maxEffectiveRank >= pipeline.RequireStepUpAuthThresholdRank && string.IsNullOrWhiteSpace(request.StepUpAuthToken))
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
            else if (maxEffectiveRank >= pipeline.RequireStepUpAuthThresholdRank &&
                     _totpService != null && _totpSecretStore != null)
            {
                var secret = await _totpSecretStore.GetSecretAsync(request.ReviewerSid.Value, ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(secret) ||
                    !await _totpService.VerifyAndConsumeTotpAsync(request.ReviewerSid.Value, secret, request.StepUpAuthToken!, ct).ConfigureAwait(false))
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
                else
                {
                    result = EvaluateApprovalProgression(proposal, request, pipeline, maxEffectiveRank);
                }
            }
            else
            {
                result = EvaluateApprovalProgression(proposal, request, pipeline, maxEffectiveRank);
            }
        }

        if (result.Success && _proposalStore != null)
        {
            var updatedProposal = proposal with { Status = result.NewStatus };
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
