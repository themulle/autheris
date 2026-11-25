namespace Autheris.Application.Governance.Services;

using System;
using System.Collections.Generic;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Domains;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Security;
using Autheris.Domain.Common;
using Autheris.Domain.Diagnostics;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Autheris.Application.Security.Totp.Interfaces;

/// <summary>
/// Implements the classification workflow state machine with SoD enforcement, Step-Up MFA validation,
/// protection-class / mask-category persistence, and zero-touch auto-approvals.
/// </summary>
public sealed class ClassificationWorkflowStateMachine : IClassificationWorkflowStateMachine
{
    private readonly IOptionsMonitor<GatewayOptions> _gatewayOptions;
    private readonly IClassificationProposalStore? _proposalStore;
    private readonly IEnumerable<IGovernanceWorkflowHook>? _hooks;
    private readonly ITotpVerificationService? _totpService;
    private readonly ITotpSecretStore? _totpSecretStore;
    private readonly GovernanceDomainService? _governanceDomainService;
    private readonly IGovernanceCapabilities? _capabilities;
    private readonly IGovernanceDomainRepository? _domainRepository;
    private readonly ITableMetadataRepository? _tableMetadataRepository;
    private readonly ILogger<ClassificationWorkflowStateMachine> _logger;

    public ClassificationWorkflowStateMachine(
        IOptionsMonitor<GatewayOptions> gatewayOptions,
        ILogger<ClassificationWorkflowStateMachine> logger,
        IClassificationProposalStore? proposalStore = null,
        IEnumerable<IGovernanceWorkflowHook>? hooks = null,
        ITotpVerificationService? totpService = null,
        ITotpSecretStore? totpSecretStore = null,
        GovernanceDomainService? governanceDomainService = null,
        IGovernanceCapabilities? capabilities = null,
        IGovernanceDomainRepository? domainRepository = null,
        ITableMetadataRepository? tableMetadataRepository = null)
    {
        _gatewayOptions = gatewayOptions ?? throw new ArgumentNullException(nameof(gatewayOptions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _proposalStore = proposalStore;
        _hooks = hooks;
        _totpService = totpService;
        _totpSecretStore = totpSecretStore;
        _governanceDomainService = governanceDomainService;
        _capabilities = capabilities;
        _domainRepository = domainRepository;
        _tableMetadataRepository = tableMetadataRepository;
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

        // Single approval (Amendment 2): one decision by a DG or owner member settles an open proposal. A legacy proposal still waiting
        // for the retired second stage (PENDING_GOVERNANCE_REVIEW) is open as well.
        var isOpenStatus = IsStatus(proposal, "UNCLASSIFIED") || IsStatus(proposal, "PENDING_OWNER_REVIEW") || IsStatus(proposal, "PENDING_GOVERNANCE_REVIEW");
        if (!isOpenStatus)
        {
            // CLASSIFIED / REJECTED (and anything unknown) are terminal: sealed proposals cannot be re-approved or re-rejected.
            return Fail(proposal, request, "INVALID_WORKFLOW_STATE",
                $"Proposal in status '{proposal.Status}' cannot be transitioned.");
        }

        if (isReject)
        {
            _logger.LogInformation("Proposal {ProposalId} rejected by {Reviewer}.", proposal.ProposalId, LogSanitizer.Sanitize(request.ReviewerSid));
            result = new TableClassificationApprovalResult(
                Success: true,
                NewStatus: "REJECTED",
                Message: "Classification proposal was rejected.");
            Diagnostics.ClassificationDiagnostics.RecordTransition(request.Action, request.ReviewerRole, "REJECTED");
        }
        else if (IsRequesterDecidingOwnDowngrade(proposal, request, reviewerSid, options))
        {
            _logger.LogWarning("SoD Violation: Requester {Sid} attempted to decide their own classification downgrade on proposal {ProposalId}.",
                LogSanitizer.Sanitize(reviewerSid), proposal.ProposalId);
            Diagnostics.ClassificationDiagnostics.RecordSodViolation(proposal.TableIdentifier.ToQualifiedName());
            Diagnostics.ClassificationDiagnostics.RecordTransition(request.Action, request.ReviewerRole, "BLOCKED_SOD");
            return Fail(proposal, request, "SOD_REQUESTER_DOWNGRADE_FORBIDDEN", "SoD Violation: Requester cannot decide their own classification downgrade.", recordTransition: false);
        }
        else
        {
            if (_capabilities is not null && request.Caller is not null && request.TenantId is { } tenant)
            {
                var containerRef = await ResolveContainerRefAsync(tenant, proposal.TableIdentifier, ct).ConfigureAwait(false);
                var callerCtx = SecurityPrincipalContext.FromPrincipal(request.Caller, tenant);
                if (!await _capabilities.CanClassifyAsync(callerCtx, containerRef, tenant, ct).ConfigureAwait(false))
                {
                    return Fail(proposal, request, "CLASSIFY_FORBIDDEN", "Caller is not authorized to approve classification for this dataset.");
                }
            }

            // Effective maximum rank across ALL proposal columns. Caller-supplied decisions may only raise a rank,
            // never lower it, so they cannot slip below the step-up threshold.
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
                        LogSanitizer.Sanitize(request.ReviewerSid), proposal.ProposalId);

                    result = new TableClassificationApprovalResult(
                        Success: false,
                        NewStatus: proposal.Status,
                        Message: "Invalid or consumed Step-Up 2FA/MFA verification token.",
                        RequiresStepUpAuth: true,
                        ErrorCode: "INVALID_STEP_UP_AUTH_TOKEN");

                    Diagnostics.ClassificationDiagnostics.RecordTransition(request.Action, request.ReviewerRole, "STEP_UP_FAILED");
                    return result;
                }

                result = Classified(request);
            }
            else
            {
                result = Classified(request);
            }
        }

        if (result.Success &&
            string.Equals(result.NewStatus, "CLASSIFIED", StringComparison.OrdinalIgnoreCase) &&
            _governanceDomainService is not null)
        {
            var writeFailure = await WriteEffectiveProtectionAsync(proposal, request, options, ct).ConfigureAwait(false);
            if (writeFailure is not null)
            {
                result = writeFailure;
            }
        }

        if (result.Success && _proposalStore != null)
        {
            var updatedProposal = proposal with { Status = result.NewStatus };
            if (isApprove)
            {
                // The single decider is recorded on the proposal.
                updatedProposal = updatedProposal with { ApprovedBySid = reviewerSid };
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

    private static TableClassificationApprovalResult Classified(TableClassificationApprovalRequest request)
    {
        Diagnostics.ClassificationDiagnostics.RecordTransition(request.Action, request.ReviewerRole, "CLASSIFIED");
        return new TableClassificationApprovalResult(
            Success: true,
            NewStatus: "CLASSIFIED",
            Message: "Classification activated and approved.");
    }

    private static bool IsStatus(TableClassificationProposal proposal, string status) =>
        string.Equals(proposal.Status, status, StringComparison.OrdinalIgnoreCase);

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

    private static bool IsRequesterDecidingOwnDowngrade(
        TableClassificationProposal proposal,
        TableClassificationApprovalRequest request,
        string reviewerSid,
        ClassificationOptions options)
    {
        if (string.IsNullOrWhiteSpace(proposal.ProposedBySid) ||
            !string.Equals(proposal.ProposedBySid, reviewerSid, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var proposedByCol = new Dictionary<string, ColumnClassificationProposal>(StringComparer.OrdinalIgnoreCase);
        foreach (var col in proposal.Columns)
        {
            proposedByCol[col.ColumnName] = col;
        }

        var maxEffective = request.ColumnsCountOrPublicFallback(options, proposal);
        if (request.ExistingTableClass is { } existingClass && maxEffective < existingClass.NormalizeOrConfidential())
        {
            return true;
        }

        if (request.ColumnDecisions is null)
        {
            return false;
        }

        foreach (var dec in request.ColumnDecisions)
        {
            if (!proposedByCol.TryGetValue(dec.ColumnName, out var proposed))
            {
                continue;
            }

            var proposedClass = options.ResolveProtectionClass(proposed.ProposedSensitivityKey, proposed.ProposedSensitivityRank);
            var effectiveClass = dec.EffectiveProtectionClass
                ?? options.ResolveProtectionClass(dec.EffectiveSensitivityKey, dec.EffectiveSensitivityRank);
            if (effectiveClass < proposedClass || dec.EffectiveSensitivityRank < proposed.ProposedSensitivityRank)
            {
                return true;
            }
        }

        return false;
    }

    private async Task<ContainerRef> ResolveContainerRefAsync(TenantId tenant, TableIdentifier tableId, CancellationToken ct)
    {
        if (_tableMetadataRepository is not null && _domainRepository is not null)
        {
            var meta = await _tableMetadataRepository.GetTableMetadataAsync(tableId, ct).ConfigureAwait(false);
            if (meta is not null)
            {
                var defaults = await _domainRepository.GetContainerForTableAsync(tenant, meta.Table.Id, ct).ConfigureAwait(false);
                if (defaults.IsValid)
                {
                    return defaults.Container;
                }
            }
        }

        return ContainerDefaults.Unassigned.Container;
    }

    private async Task<TableClassificationApprovalResult?> WriteEffectiveProtectionAsync(
        TableClassificationProposal proposal,
        TableClassificationApprovalRequest request,
        ClassificationOptions options,
        CancellationToken ct)
    {
        if (_governanceDomainService is null || request.TenantId is not { } tenant)
        {
            return null;
        }
        var callerCtx = request.Caller is not null
            ? SecurityPrincipalContext.FromPrincipal(request.Caller, tenant)
            : new SecurityPrincipalContext
            {
                UserSid = request.ReviewerSid,
                ResolvedTenant = tenant,
                GroupSids = new HashSet<Sid>(),
                GroupResolutionStatus = Autheris.Domain.Identity.GroupResolutionStatus.Degraded,
                TenantRoles = new HashSet<string>(),
                ClusterRoles = new HashSet<string>(),
                AuthenticationScheme = "Workflow",
                IsAuthenticated = true
            };

        var decisionByCol = new Dictionary<string, ColumnClassificationDecision>(StringComparer.OrdinalIgnoreCase);
        if (request.ColumnDecisions is not null)
        {
            foreach (var dec in request.ColumnDecisions)
            {
                decisionByCol[dec.ColumnName] = dec;
            }
        }

        var resolvedColumns = new List<(string ColumnName, ProtectionClass Class, string? MaskCategory)>(proposal.Columns.Count);
        foreach (var col in proposal.Columns)
        {
            decisionByCol.TryGetValue(col.ColumnName, out var dec);
            var colClass = dec?.EffectiveProtectionClass
                ?? (dec is not null && !string.IsNullOrWhiteSpace(dec.EffectiveSensitivityKey)
                    ? options.ResolveProtectionClass(dec.EffectiveSensitivityKey, dec.EffectiveSensitivityRank)
                    : options.ResolveProtectionClass(col.ProposedSensitivityKey, col.ProposedSensitivityRank));
            var maskCategory = NormalizeMaskCategory(dec?.EffectiveMaskCategory)
                ?? NormalizeMaskCategory(col.ProposedMaskCategory)
                ?? ResolveMaskCategoryFromPiiKey(col.DetectedPiiCategoryKey);
            resolvedColumns.Add((col.ColumnName, colClass, maskCategory));
        }

        var baselineClass = resolvedColumns.Count > 0
            ? resolvedColumns[0].Class
            : options.ResolveProtectionClass(options.DefaultSensitivityKey);
        foreach (var item in resolvedColumns)
        {
            if (item.Class < baselineClass)
            {
                baselineClass = item.Class;
            }
        }

        var datasetId = proposal.TableIdentifier.ToString();
        var atomic = _domainRepository as IAtomicGovernanceDomainRepository ?? _governanceDomainService!.AtomicRepository;
        if (atomic is null) return await WriteProtectionBatchAsync(proposal, request, callerCtx, tenant, datasetId, baselineClass, resolvedColumns, ct).ConfigureAwait(false);
        try
        {
            await atomic.ExecuteInTransactionAsync(async innerCt =>
            {
                if (await WriteProtectionBatchAsync(proposal, request, callerCtx, tenant, datasetId, baselineClass, resolvedColumns, innerCt).ConfigureAwait(false) is { } f)
                    throw new ProtectionBatchRollbackException(f);
            }, ct).ConfigureAwait(false);
            return null;
        }
        catch (ProtectionBatchRollbackException ex) { return ex.Failure; }
    }

    private async Task<TableClassificationApprovalResult?> WriteProtectionBatchAsync(
        TableClassificationProposal proposal,
        TableClassificationApprovalRequest request,
        SecurityPrincipalContext callerCtx,
        TenantId tenant,
        string datasetId,
        ProtectionClass baselineClass,
        List<(string ColumnName, ProtectionClass Class, string? MaskCategory)> resolvedColumns,
        CancellationToken ct)
    {
        var tableOutcome = await _governanceDomainService!.SetTableProtectionAsync(callerCtx, tenant, datasetId, baselineClass, requestedMaskCategory: null, ct).ConfigureAwait(false);
        if (tableOutcome.Status == DomainServiceStatus.NotFound) return null;
        if (tableOutcome.Status != DomainServiceStatus.Ok)
            return Fail(proposal, request, tableOutcome.ErrorCode ?? "PROTECTION_WRITE_FAILED", tableOutcome.ErrorMessage ?? "Failed to write table protection.");

        foreach (var (colName, colClass, colMask) in resolvedColumns)
        {
            if (colClass == baselineClass && colMask is null) continue;
            var colOutcome = await _governanceDomainService!.SetColumnProtectionAsync(callerCtx, tenant, datasetId, colName, colClass, colMask, ct).ConfigureAwait(false);
            if (colOutcome.Status != DomainServiceStatus.Ok && colOutcome.Status != DomainServiceStatus.NotFound)
                return Fail(proposal, request, colOutcome.ErrorCode ?? "PROTECTION_WRITE_FAILED", colOutcome.ErrorMessage ?? $"Failed to write column protection for '{colName}'.");
        }

        return null;
    }

    private sealed class ProtectionBatchRollbackException(TableClassificationApprovalResult failure) : Exception { public TableClassificationApprovalResult Failure { get; } = failure; }

    private static string? NormalizeMaskCategory(string? maskCategory)
    {
        if (string.IsNullOrWhiteSpace(maskCategory) ||
            string.Equals(maskCategory.Trim(), "NONE", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return maskCategory.Trim();
    }

    private static string? ResolveMaskCategoryFromPiiKey(string? piiKey)
    {
        if (string.IsNullOrWhiteSpace(piiKey))
        {
            return null;
        }

        return piiKey.Trim().ToUpperInvariant() switch
        {
            "IBAN" or "CREDIT_CARD" => ClassificationScopes.FinancialCode,
            "HEALTH_DATA" => ClassificationScopes.PiiSpecialCode,
            _ => ClassificationScopes.PiiCode
        };
    }
}

internal static class ClassificationApprovalRequestExtensions
{
    public static ProtectionClass ColumnsCountOrPublicFallback(
        this TableClassificationApprovalRequest request,
        ClassificationOptions options,
        TableClassificationProposal proposal)
    {
        var max = ProtectionClass.Public;
        var decMap = new Dictionary<string, ColumnClassificationDecision>(StringComparer.OrdinalIgnoreCase);
        if (request.ColumnDecisions is not null)
        {
            foreach (var dec in request.ColumnDecisions)
            {
                decMap[dec.ColumnName] = dec;
            }
        }

        foreach (var col in proposal.Columns)
        {
            var cls = decMap.TryGetValue(col.ColumnName, out var dec)
                ? (dec.EffectiveProtectionClass ?? options.ResolveProtectionClass(dec.EffectiveSensitivityKey, dec.EffectiveSensitivityRank))
                : options.ResolveProtectionClass(col.ProposedSensitivityKey, col.ProposedSensitivityRank);
            if (cls > max)
            {
                max = cls;
            }
        }

        return max;
    }
}
