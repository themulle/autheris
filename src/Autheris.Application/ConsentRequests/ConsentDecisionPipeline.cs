namespace Autheris.Application.ConsentRequests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.ConsentRequests.ClassificationClear;
using Autheris.Application.ConsentRequests.Events;
using Autheris.Application.ConsentRequests.ProfileAssignments;
using Autheris.Application.Governance.ConsentRequests;
using Autheris.Application.Governance.Grants;
using Autheris.Application.Governance.Validation;
using Autheris.Application.Identity;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Domain.Tenancy;
using Microsoft.Extensions.Options;

public interface IConsentDecisionPipeline
{
    Task<ConsentDecisionResult> DecideAsync(ConsentDecisionCommand command, CancellationToken ct = default);
}

/// <summary>
/// Single unified decision pipeline for internal (GraphQL / UI) and external consent decisions.
/// Enforces channel guards, tenant boundaries, optimistic versioning, workflow mode isolation,
/// four-eyes distinctness, self-approval prohibition (including effective group membership),
/// grant validity bounds, and atomic persistence.
/// </summary>
public sealed partial class ConsentDecisionPipeline(
    IConsentApprovalRepository approvalRepository,
    ITableMetadataRepository? metadataRepository,
    IDataOwnershipRepository ownershipRepository,
    IConsentDecisionGuard decisionGuard,
    IGrantPolicy grantPolicy,
    IGrantValidityPolicy grantValidityPolicy,
    IExternalDecisionGuard externalDecisionGuard,
    IConsentDecisionDenialRecorder? denialRecorder = null,
    IConsentWorkflowConfigRepository? workflowConfigRepository = null,
    IScimGroupStore? groupStore = null,
    IIdentityRealmRegistry? realmRegistry = null,
    IOptions<GatewayOptions>? gatewayOptions = null,
    IOptions<GovernanceTextOptions>? textOptions = null,
    TimeProvider? timeProvider = null,
    TenantId? fallbackTenantId = null,
    IConsentEventOutbox? eventOutbox = null,
    IProfileAssignmentDecisionHandler? profileAssignmentHandler = null,
    IClassificationRepository? classificationRepository = null,
    IOptions<GrantValidityOptions>? grantOptions = null,
    ISeparationOfDutiesService? separationOfDutiesService = null) : IConsentDecisionPipeline
{
    private static readonly JsonSerializerOptions CaseInsensitiveJsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly IConsentApprovalRepository _approvalRepository = approvalRepository ?? throw new ArgumentNullException(nameof(approvalRepository));
    private readonly ITableMetadataRepository? _metadataRepository = metadataRepository;
    private readonly IDataOwnershipRepository _ownershipRepository = ownershipRepository ?? throw new ArgumentNullException(nameof(ownershipRepository));
    private readonly IConsentDecisionGuard _decisionGuard = decisionGuard ?? throw new ArgumentNullException(nameof(decisionGuard));
    private readonly IGrantPolicy _grantPolicy = grantPolicy ?? throw new ArgumentNullException(nameof(grantPolicy));
    private readonly IGrantValidityPolicy _grantValidityPolicy = grantValidityPolicy ?? throw new ArgumentNullException(nameof(grantValidityPolicy));
    private readonly IExternalDecisionGuard _externalDecisionGuard = externalDecisionGuard ?? throw new ArgumentNullException(nameof(externalDecisionGuard));
    private readonly IConsentWorkflowConfigRepository? _workflowConfigRepository = workflowConfigRepository ?? (approvalRepository as IConsentWorkflowConfigRepository);
    private readonly IConsentEventOutbox? _eventOutbox = eventOutbox ?? (approvalRepository as IConsentEventOutbox);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly int _descriptionMaxLength = textOptions?.Value?.ConsentDescriptionMaxLength ?? new GovernanceTextOptions().ConsentDescriptionMaxLength;
    private readonly ISeparationOfDutiesService _separationOfDuties = separationOfDutiesService ?? new SeparationOfDutiesService(groupStore, realmRegistry);

    public async Task<ConsentDecisionResult> DecideAsync(ConsentDecisionCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.Channel == ConsentDecisionChannel.Internal)
        {
            await EnforceInternalCallerGuardAsync(command.Principal, ct).ConfigureAwait(false);
        }

        var (actorSid, callerTenantId, isCrossTenantAdmin) = ResolveCallerContext(command);

        if (command.Channel == ConsentDecisionChannel.Internal)
        {
            EnsureReviewScope(command.Principal);
        }

        var req = await _approvalRepository.GetConsentRequestAsync(command.RequestId, ct).ConfigureAwait(false);
        if (req == null || (callerTenantId is { } expectedTenant && req.TenantId != expectedTenant && !isCrossTenantAdmin))
        {
            throw new ConsentDecisionException(
                ConsentDecisionErrorCodes.RequestNotFound,
                $"Consent request '{command.RequestId}' was not found.",
                statusCode: 404);
        }

        if (command.ExpectedVersion.HasValue && req.Version != command.ExpectedVersion.Value)
        {
            throw new ConsentDecisionException(
                ConsentDecisionErrorCodes.PreconditionFailed,
                $"Consent request '{req.Id}' version {req.Version} does not match expected version {command.ExpectedVersion.Value}.",
                statusCode: 412);
        }

        ValidateChannelMatchesWorkflowMode(command.Channel, req);

        if (!IsDecidableStatus(req.Status))
        {
            throw new ConsentDecisionException(
                ConsentDecisionErrorCodes.RequestNotDecidable,
                $"Consent request '{req.Id}' is in status '{req.Status}' and can no longer be decided.",
                statusCode: 409);
        }

        var clearPlan = await ResolveClearPlanAsync(req, command, ct).ConfigureAwait(false);

        IReadOnlyList<Sid> externalApproverSids = Array.Empty<Sid>();
        if (command.Channel == ConsentDecisionChannel.External)
        {
            var route = _workflowConfigRepository != null
                ? await _workflowConfigRepository.ResolveWorkflowRouteAsync(req.TenantId, req.TableIdentifier, ct).ConfigureAwait(false)
                : null;
            var externalSystemId = command.ExternalAttribution?.ExternalSystemId ?? Guid.Empty;
            var system = _workflowConfigRepository != null && externalSystemId != Guid.Empty
                ? await _workflowConfigRepository.GetExternalWorkflowSystemAsync(req.TenantId, externalSystemId, ct).ConfigureAwait(false)
                : null;

            externalApproverSids = await _externalDecisionGuard.ValidateAsync(command, req, route, system, ct).ConfigureAwait(false);
        }
        else
        {
            await ValidateInternalCallerAuthorizationAsync(req, command.Principal!, actorSid, command.Decision, clearPlan, ct).ConfigureAwait(false);
        }

        if (req.RequestKind == ConsentRequestKind.ProfileAssignment && profileAssignmentHandler != null)
        {
            return await profileAssignmentHandler.DecideAsync(command, req, actorSid, externalApproverSids, ct).ConfigureAwait(false);
        }

        if (command.Decision == ConsentDecisionKind.Reject)
        {
            return await ExecuteRejectAsync(command, req, actorSid, externalApproverSids, ct).ConfigureAwait(false);
        }

        return await ExecuteApproveAsync(command, req, actorSid, externalApproverSids, clearPlan, ct).ConfigureAwait(false);
    }

    private async Task EnforceInternalCallerGuardAsync(ClaimsPrincipal? principal, CancellationToken ct)
    {
        var effectivePrincipal = principal ?? new ClaimsPrincipal(new ClaimsIdentity());
        var verdict = _decisionGuard.Evaluate(effectivePrincipal);
        if (verdict.Allowed)
        {
            return;
        }

        if (denialRecorder != null)
        {
            var claim = TenantClaimReader.Read(effectivePrincipal);
            var tenantId = claim.Kind == ClaimTenantKind.Valid ? claim.Tenant : (fallbackTenantId ?? TenantId.Platform);
            await denialRecorder.RecordAsync(effectivePrincipal, verdict.ReasonClass, tenantId, ct).ConfigureAwait(false);
        }

        throw new ConsentDecisionException(
            ConsentDecisionErrorCodes.DecisionNotPermitted,
            "Only a person can approve or reject consent requests.",
            statusCode: 403);
    }

    private (Sid ActorSid, TenantId? TenantId, bool IsCrossTenantAdmin) ResolveCallerContext(ConsentDecisionCommand command)
    {
        if (command.Channel == ConsentDecisionChannel.External)
        {
            var attr = command.ExternalAttribution;
            var first = attr?.EffectiveApprovers.FirstOrDefault();
            var externalActor = first != null
                ? new Sid(string.IsNullOrWhiteSpace(first.ResolvedSid) ? first.Subject : first.ResolvedSid!)
                : new Sid("external-workflow");
            if (command.ContextTenantId is { } explicitExternalTenant)
            {
                return (externalActor, explicitExternalTenant, false);
            }

            var tenantClaim = TenantClaimReader.Read(command.Principal);
            TenantId? extTenant = tenantClaim.Kind == ClaimTenantKind.Valid ? tenantClaim.Tenant : (TenantId?)null;
            return (externalActor, extTenant, false);
        }

        var principal = command.Principal;
        var sid = principal.GetUserSid()
            ?? throw new ConsentDecisionException("FORBIDDEN", "User SID could not be resolved from token.", statusCode: 403);
        var isClusterAdmin = ClusterAdminPolicy.IsCanonicalClusterAdmin(principal);
        if (command.ContextTenantId is { } explicitTenant)
        {
            return (sid, explicitTenant, isClusterAdmin);
        }

        var claim = TenantClaimReader.Read(principal);
        if (claim.Kind == ClaimTenantKind.Valid)
        {
            return (sid, claim.Tenant, isClusterAdmin);
        }

        return (sid, fallbackTenantId ?? TenantId.Platform, isClusterAdmin);
    }

    private void EnsureReviewScope(ClaimsPrincipal? principal)
    {
        var entraId = gatewayOptions?.Value?.Authentication?.EntraId;
        if (entraId != null && (!entraId.SelfServiceEnabled || entraId.AccessReviewScopes is { Count: 0 }))
        {
            throw new ConsentDecisionException("FORBIDDEN", "Self-service access review is disabled.", statusCode: 403);
        }

        var scopes = TokenAccessScope.GetScopeSet(principal?.Claims);
        if (scopes.Count == 0)
        {
            return;
        }

        var configuredReviewScopes = entraId?.AccessReviewScopes;
        var reviewScopes = configuredReviewScopes is { Count: > 0 } ? configuredReviewScopes : ["data.access.review"];
        if (!reviewScopes.Any(s => scopes.Contains(s)))
        {
            var formattedScopes = string.Join("', '", reviewScopes);
            throw new ConsentDecisionException("FORBIDDEN", $"The '{formattedScopes}' scope is required to review consent requests.", statusCode: 403);
        }
    }

    private static void ValidateChannelMatchesWorkflowMode(ConsentDecisionChannel channel, ConsentRequest req)
    {
        if (channel == ConsentDecisionChannel.Internal && req.WorkflowMode == ConsentWorkflowMode.External)
        {
            throw new ConsentDecisionException(
                ConsentDecisionErrorCodes.WorkflowModeMismatch,
                $"Consent request '{req.Id}' uses external workflow mode and must be decided by its external workflow system.",
                statusCode: 409);
        }

        if (channel == ConsentDecisionChannel.External && req.WorkflowMode != ConsentWorkflowMode.External)
        {
            throw new ConsentDecisionException(
                ConsentDecisionErrorCodes.WorkflowModeMismatch,
                $"Consent request '{req.Id}' uses internal workflow mode and cannot be decided externally.",
                statusCode: 409);
        }
    }

    private static bool IsDecidableStatus(string? status) =>
        string.Equals(status, "PENDING", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "PENDING_SECOND_APPROVAL", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "PENDING_EXTERNAL_APPROVAL", StringComparison.OrdinalIgnoreCase);

    private async Task ValidateInternalCallerAuthorizationAsync(
        ConsentRequest req,
        ClaimsPrincipal principal,
        Sid approverSid,
        ConsentDecisionKind decision,
        ClearApprovalPlan? clearPlan,
        CancellationToken ct)
    {
        if (decision == ConsentDecisionKind.Approve)
        {
            var sod = await _separationOfDuties.CheckConsentApprovalAsync(
                req.TenantId, req, principal, approverSid, null, checkPriorApprovers: true, ct: ct).ConfigureAwait(false);
            if (!sod.Allowed)
            {
                throw new ConsentDecisionException(
                    sod.Kind == SodViolationKind.SameApprover
                        ? ConsentDecisionErrorCodes.FourEyesSameApprover
                        : ConsentDecisionErrorCodes.SelfApprovalForbidden,
                    sod.Reason,
                    statusCode: sod.Kind == SodViolationKind.SameApprover ? 409 : 403);
            }
        }

        var roles = principal.FindAll(ClaimTypes.Role).Select(r => r.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool isPrivilegedAdmin = roles.Contains("GovernanceAdmin") || ClusterAdminPolicy.IsCanonicalClusterAdmin(principal);
        if (!isPrivilegedAdmin && req.RequestKind != ConsentRequestKind.ProfileAssignment)
        {
            bool isAuthorized = await _ownershipRepository.IsAuthorizedApproverForTableAsync(req.TableIdentifier, approverSid, ct).ConfigureAwait(false);
            if (!isAuthorized)
            {
                // Only a steward of a requested classification may approve without owning the table (after the data owner).
                await EnsureStewardMayApproveAsync(req, clearPlan, approverSid, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task<ConsentDecisionResult> ExecuteRejectAsync(
        ConsentDecisionCommand command,
        ConsentRequest req,
        Sid actorSid,
        IReadOnlyList<Sid> externalApproverSids,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(command.Reason))
        {
            throw new ConsentDecisionException("VALIDATION_ERROR", "Rejection reason is required.", statusCode: 400);
        }

        var trimmedReason = command.Reason.Trim();
        try
        {
            if (command.Channel == ConsentDecisionChannel.External)
            {
                var effectiveApprover = externalApproverSids[^1];
                var rejectedExt = await _approvalRepository.RejectExternalConsentRequestAsync(
                    req.Id, command.ExternalAttribution!, effectiveApprover, trimmedReason, ct).ConfigureAwait(false);
                await EmitRequestEventAsync(rejectedExt, ConsentEventTypes.RequestDecided, _timeProvider.GetUtcNow(), ct).ConfigureAwait(false);
                return new ConsentDecisionResult(rejectedExt);
            }

            var rejected = await _approvalRepository.RejectConsentRequestAsync(req.Id, actorSid, trimmedReason, ct).ConfigureAwait(false);
            await EmitRequestEventAsync(rejected, ConsentEventTypes.RequestDecided, _timeProvider.GetUtcNow(), ct).ConfigureAwait(false);
            return new ConsentDecisionResult(rejected);
        }
        catch (ConsentRequestStatusConflictException ex)
        {
            throw new ConsentDecisionException(ConsentDecisionErrorCodes.RequestNotDecidable, ex.Message, statusCode: 409);
        }
    }

    private async Task<ConsentDecisionResult> ExecuteApproveAsync(
        ConsentDecisionCommand command,
        ConsentRequest req,
        Sid actorSid,
        IReadOnlyList<Sid> externalApproverSids,
        ClearApprovalPlan? clearPlan,
        CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow();
        DateTimeOffset effectiveValidTo;
        try
        {
            effectiveValidTo = _grantValidityPolicy.ResolveApprovedValidTo(req, command.ApprovedValidTo, now);
        }
        catch (GrantValidityException ex)
        {
            throw new ConsentDecisionException(ex.ErrorCode, ex.Message, ex.StatusCode);
        }

        var validatedDescription = ValidateOptionalDescription(command.Description);
        TableMetadata? meta = _metadataRepository != null
            ? await _metadataRepository.GetTableMetadataAsync(req.TableIdentifier, ct).ConfigureAwait(false)
            : null;

        var rowFilters = command.PrebuiltRowFilters != null
            ? command.PrebuiltRowFilters.ToList()
            : ParseRowFilters(command.RowFilterJson, meta, req.TableIdentifier);
        var columnRules = command.PrebuiltColumnRules != null
            ? command.PrebuiltColumnRules.ToList()
            : ParseColumnRules(command.ColumnMaskingJson, req, meta);

        var approversSoFar = (req.ApprovalSteps ?? [])
            .Where(s => string.Equals(s.Decision, "APPROVED", StringComparison.OrdinalIgnoreCase))
            .Select(s => s.ApproverSid.Value)
            .ToList();
        var currentApprovers = command.Channel == ConsentDecisionChannel.External
            ? externalApproverSids.Select(s => s.Value)
            : [actorSid.Value];
        var combinedApprovers = approversSoFar.Concat(currentApprovers).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        bool grantRequiresFourEyes = false;
        if (req.RequestedGranteeType is GranteeType.User or GranteeType.ServicePrincipal)
        {
            var grantDecision = _grantPolicy.Evaluate(new GrantRequest(
                req.RequestedGranteeType,
                req.RequestedGranteeRef,
                GrantKind.Consent,
                effectiveValidTo,
                now,
                GrantClassifier.Classify([meta]),
                IsUnmaskedApproval(columnRules, meta) || clearPlan is not null ? MaskingPolicyMode.Unmasked : MaskingPolicyMode.Default,
                Requester: req.RequesterSid.Value,
                Approvers: combinedApprovers,
                Justification: req.BusinessJustification));
            if (!grantDecision.Allowed && !string.Equals(grantDecision.ErrorCode, GrantErrorCodes.FourEyesRequired, StringComparison.Ordinal))
            {
                throw new ConsentDecisionException(
                    grantDecision.ErrorCode ?? GrantErrorCodes.Indeterminate,
                    grantDecision.Reason,
                    statusCode: 400);
            }

            grantRequiresFourEyes = grantDecision.RequiresFourEyes ||
                string.Equals(grantDecision.ErrorCode, GrantErrorCodes.FourEyesRequired, StringComparison.Ordinal);
        }

        bool tableSensitive = meta is not null && ClassificationMetadata.IsTableSensitive(meta);
        bool stewardGate = clearPlan is { NeedsSteward: true };
        bool requiresFourEyes = tableSensitive || stewardGate || grantRequiresFourEyes;
        bool needsSecondApproverOverride = requiresFourEyes;
        bool requireSecondApprover = needsSecondApproverOverride && command.Channel == ConsentDecisionChannel.Internal;

        var distinctIndependentApprovers = SeparationOfDutiesService.CountDistinctIndependentApprovers(
            combinedApprovers,
            req.RequesterSid.Value,
            req.RequestedGranteeType is GranteeType.User or GranteeType.ServicePrincipal ? req.RequestedGranteeRef : null);
        bool isFinalApproval = !requiresFourEyes
            || distinctIndependentApprovers >= 2
            || string.Equals(req.Status, "PENDING_SECOND_APPROVAL", StringComparison.OrdinalIgnoreCase);

        bool hasRestrictions = command.HasPrebuiltRestrictions
            || command.WriteRules != null
            || rowFilters.Count > 0
            || columnRules.Count > 0
            || !string.IsNullOrEmpty(command.ColumnMaskingJson)
            || !string.IsNullOrEmpty(command.RowFilterJson);

        if (!isFinalApproval && clearPlan is not null && command.ApprovedClearClassifications is not null)
        {
            // Only the final approval selects (narrows) the Clear items, so a later approver can never widen an earlier selection.
            throw new ConsentDecisionException(
                ConsentDecisionErrorCodes.DecisionExceedsRequest,
                "The classification Clear selection is only accepted on the final approval.",
                statusCode: 400);
        }

        if (!isFinalApproval && hasRestrictions)
        {
            throw new ConsentDecisionException(
                "BAD_REQUEST",
                "Cannot configure write rules, row filters, or column masking on the first step of a four-eyes approval.",
                statusCode: 400);
        }

        if (stewardGate && (isFinalApproval || command.Channel == ConsentDecisionChannel.External))
        {
            var ordered = (req.ApprovalSteps ?? [])
                .Where(static s => string.Equals(s.Decision, "APPROVED", StringComparison.OrdinalIgnoreCase))
                .OrderBy(static s => s.StepNumber)
                .Select(static s => s.ApproverSid.Value)
                .Concat(currentApprovers)
                .ToList();
            await EnsureStewardCoverageAsync(req, clearPlan!, ordered, ct).ConfigureAwait(false);
        }

        try
        {
            if (!isFinalApproval)
            {
                if (command.Channel == ConsentDecisionChannel.External)
                {
                    var extStep = needsSecondApproverOverride
                        ? await _approvalRepository.ApproveExternalConsentRequestStepAsync(
                            req.Id, command.ExternalAttribution!, externalApproverSids[0], requireSecondApprover: true, ct).ConfigureAwait(false)
                        : await _approvalRepository.ApproveExternalConsentRequestStepAsync(
                            req.Id, command.ExternalAttribution!, externalApproverSids[0], ct).ConfigureAwait(false);
                    await EmitRequestEventAsync(extStep, ConsentEventTypes.RequestUpdated, now, ct).ConfigureAwait(false);
                    return new ConsentDecisionResult(extStep);
                }

                var stepReq = requireSecondApprover
                    ? await _approvalRepository.ApproveConsentRequestStepAsync(req.Id, actorSid, requireSecondApprover: true, ct).ConfigureAwait(false)
                    : await _approvalRepository.ApproveConsentRequestStepAsync(req.Id, actorSid, ct).ConfigureAwait(false);
                await EmitRequestEventAsync(stepReq, ConsentEventTypes.RequestUpdated, now, ct).ConfigureAwait(false);
                return new ConsentDecisionResult(stepReq);
            }

            IReadOnlyList<WriteRule> approvedWriteRules;
            if (command.WriteRules != null)
            {
                try
                {
                    approvedWriteRules = WriteRuleWorkflowHelper.NarrowWriteRules(
                        req.RequestedWriteRules ?? [],
                        command.WriteRules,
                        req.IsWriteOnlyRequest,
                        meta,
                        req.TableIdentifier);
                }
                catch (ConsentRequestException ex)
                {
                    throw new ConsentDecisionException(ex.Code, ex.Message, statusCode: 400);
                }
            }
            else
            {
                approvedWriteRules = req.RequestedWriteRules;
            }

            var effectiveFinalSid = command.Channel == ConsentDecisionChannel.External ? externalApproverSids[^1] : actorSid;
            if (!req.ShouldGrantReadColumns && columnRules.Count == 0 && approvedWriteRules.Count == 0)
            {
                throw new ConsentDecisionException("VALIDATION_ERROR", "A write-only request cannot be approved with no write rules.", statusCode: 400);
            }

            if (columnRules.Count == 0 && req.ShouldGrantReadColumns && meta != null)
            {
                columnRules = ConsentColumnSnapshot.FromMetadata(meta).ToList();
            }

            if (clearPlan is not null)
            {
                columnRules = ApplyClearColumns(columnRules, clearPlan).ToList();
            }

            var effectiveValidFrom = req.RequestedValidFrom is { } requestedFrom && requestedFrom > now
                ? requestedFrom
                : now;
            var consentId = Guid.NewGuid();
            var finalConsent = new Consent
            {
                Id = consentId,
                TableId = req.TableId,
                TableIdentifier = req.TableIdentifier,
                ConsentRequestId = req.Id,
                Effect = ConsentEffect.Allow,
                TenantId = req.TenantId,
                GranteeType = req.RequestedGranteeType,
                GranteeSid = new Sid(req.RequestedGranteeRef),
                CreatedBySid = effectiveFinalSid,
                ValidFrom = effectiveValidFrom,
                ValidTo = effectiveValidTo,
                Description = validatedDescription,
                WriteRules = approvedWriteRules,
                ColumnRules = columnRules,
                RowFilters = rowFilters,
                GrantSource = req.RequestKind == ConsentRequestKind.ProfileAssignment
                    ? ConsentGrantSource.AccessProfile
                    : ConsentGrantSource.ConsentRequest,
                ProfileAssignmentId = req.RequestKind == ConsentRequestKind.ProfileAssignment ? req.Id : null,
                ClassificationAccess = clearPlan is null
                    ? []
                    : BuildClearAccess(req, consentId, clearPlan, effectiveFinalSid, effectiveValidFrom, now, effectiveValidTo, DefaultMaxClearDays)
            };

            var approvalStep = new ApprovalStep
            {
                Id = Guid.NewGuid(),
                ConsentRequestId = req.Id,
                ApproverSid = effectiveFinalSid,
                Decision = "APPROVED",
                DecidedAt = command.Channel == ConsentDecisionChannel.External ? command.ExternalAttribution!.DecidedAt : now
            };

            Consent activated;
            if (command.Channel == ConsentDecisionChannel.External)
            {
                activated = await _approvalRepository.ExecuteExternalAtomicApprovalWithConsentAsync(
                    req.Id, command.ExternalAttribution!, approvalStep, finalConsent, rowFilters, columnRules, ct).ConfigureAwait(false);
            }
            else
            {
                activated = requireSecondApprover
                    ? await _approvalRepository.ExecuteAtomicApprovalWithConsentAsync(
                        req.Id, approvalStep, finalConsent, rowFilters, columnRules, requireSecondApprover: true, ct).ConfigureAwait(false)
                    : await _approvalRepository.ExecuteAtomicApprovalWithConsentAsync(
                        req.Id, approvalStep, finalConsent, rowFilters, columnRules, ct).ConfigureAwait(false);
            }

            var approvedReq = CloneWithStatus(req, "APPROVED", now);
            await EmitRequestEventAsync(approvedReq, ConsentEventTypes.RequestDecided, now, ct).ConfigureAwait(false);
            return new ConsentDecisionResult(approvedReq, activated);
        }
        catch (ConsentRequestStatusConflictException ex)
        {
            throw new ConsentDecisionException(ConsentDecisionErrorCodes.RequestNotDecidable, ex.Message, statusCode: 409);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("FOUR_EYES_SAME_APPROVER", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("already approved", StringComparison.OrdinalIgnoreCase))
        {
            throw new ConsentDecisionException(ConsentDecisionErrorCodes.FourEyesSameApprover, ex.Message, statusCode: 409);
        }
    }

    private async Task EmitRequestEventAsync(
        ConsentRequest request,
        string eventType,
        DateTimeOffset occurredAt,
        CancellationToken ct)
    {
        if (_eventOutbox is null)
        {
            return;
        }

        var payload = ConsentEventPayloads.ForRequest(request, occurredAt);
        await _eventOutbox.AppendAsync(
            new ConsentEventWrite(
                request.TenantId.Value,
                Guid.NewGuid(),
                eventType,
                request.Id.ToString("D"),
                ConsentEventPayloads.Serialize(payload),
                occurredAt,
                request.ExternalSystemId),
            transaction: null,
            ct: ct).ConfigureAwait(false);
    }

    private string? ValidateOptionalDescription(string? description)
    {
        if (description is null) return null;
        var check = UntrustedText.Normalize(description, _descriptionMaxLength, allowLineFeed: false);
        if (!check.IsValid)
        {
            throw new ConsentDecisionException("VALIDATION_ERROR", "The description is not valid.", statusCode: 400);
        }

        return check.Value;
    }

    private static List<ConsentRowFilter> ParseRowFilters(string? rowFilterJson, TableMetadata? meta, TableIdentifier tableIdentifier)
    {
        var filters = new List<ConsentRowFilter>();
        if (string.IsNullOrWhiteSpace(rowFilterJson)) return filters;

        var trimmed = rowFilterJson.TrimStart();
        try
        {
            var inputs = trimmed.StartsWith('[', StringComparison.Ordinal)
                ? JsonSerializer.Deserialize<List<RowFilterInput>>(rowFilterJson, CaseInsensitiveJsonOptions)
                : (JsonSerializer.Deserialize<RowFilterInput>(rowFilterJson, CaseInsensitiveJsonOptions) is { } single ? [single] : null);
            if (inputs == null) return filters;

            var hasPredicates = inputs.Any(i => i.Predicates is { Count: > 0 });
            if (!hasPredicates) return filters;

            if (meta == null)
            {
                throw new ConsentDecisionException("BAD_REQUEST", $"Metadata for table '{tableIdentifier}' was not found.", statusCode: 400);
            }

            foreach (var input in inputs)
            {
                filters.AddRange(RowFilterAstValidator.BuildRowFilters(input, meta, Guid.NewGuid()));
            }
        }
        catch (ConsentRequestException ex)
        {
            throw new ConsentDecisionException(ex.Code, ex.Message, statusCode: 400);
        }
        catch (JsonException ex)
        {
            throw new ConsentDecisionException("BAD_REQUEST", $"Invalid rowFilter JSON: {ex.Message}", statusCode: 400);
        }

        return filters;
    }

    private static List<ConsentColumnRule> ParseColumnRules(string? columnMaskingJson, ConsentRequest req, TableMetadata? meta)
    {
        var rules = new List<ConsentColumnRule>();
        if (string.IsNullOrWhiteSpace(columnMaskingJson)) return rules;

        try
        {
            var inputs = JsonSerializer.Deserialize<List<ColumnMaskInput>>(columnMaskingJson, CaseInsensitiveJsonOptions);
            if (inputs == null || inputs.Count == 0) return rules;

            if (!req.ShouldGrantReadColumns)
            {
                throw new ConsentDecisionException("BAD_REQUEST", "Column masking cannot be supplied on a write-only consent request.", statusCode: 400);
            }

            if (meta == null)
            {
                throw new ConsentDecisionException("BAD_REQUEST", $"Metadata for table '{req.TableIdentifier}' was not found.", statusCode: 400);
            }

            var colMap = meta.Columns.ToDictionary(c => c.ColumnName, c => c.Id, StringComparer.OrdinalIgnoreCase);
            var parsedByCol = new Dictionary<string, ColumnMaskInput>(StringComparer.OrdinalIgnoreCase);
            foreach (var input in inputs)
            {
                if (!colMap.ContainsKey(input.ColumnName))
                {
                    throw new ConsentDecisionException("BAD_REQUEST", $"Column '{input.ColumnName}' not found on table.", statusCode: 400);
                }

                if (!Enum.TryParse<ColumnAccessLevel>(input.AccessLevel, true, out _))
                {
                    throw new ConsentDecisionException("BAD_REQUEST", $"Invalid accessLevel '{input.AccessLevel}'.", statusCode: 400);
                }

                // A blank scope entry is stored as no scope and would turn a scope rule into a base rule for every scope (fail closed).
                if (!ConsentRuleScopeValidator.AreValid(input.ApplicableScopes))
                {
                    throw new ConsentDecisionException(
                        "BAD_REQUEST",
                        $"Column '{input.ColumnName}' has a blank scope entry or a scope entry that contains a comma; list scope names only.",
                        statusCode: 400);
                }

                parsedByCol[input.ColumnName] = input;
            }

            var baseRules = ConsentColumnSnapshot.FromMetadata(meta);
            foreach (var baseRule in baseRules)
            {
                if (parsedByCol.TryGetValue(baseRule.ColumnName, out var maskInput))
                {
                    var level = Enum.Parse<ColumnAccessLevel>(maskInput.AccessLevel, true);
                    rules.Add(new ConsentColumnRule
                    {
                        Id = baseRule.Id,
                        ConsentId = baseRule.ConsentId,
                        TableColumnId = baseRule.TableColumnId,
                        ColumnName = baseRule.ColumnName,
                        AccessLevel = level,
                        ApplicableScopes = maskInput.ApplicableScopes ?? baseRule.ApplicableScopes
                    });
                }
                else
                {
                    rules.Add(baseRule);
                }
            }
        }
        catch (JsonException ex)
        {
            throw new ConsentDecisionException("BAD_REQUEST", $"Invalid columnMasking JSON: {ex.Message}", statusCode: 400);
        }

        return rules;
    }

    private static bool IsUnmaskedApproval(IReadOnlyList<ConsentColumnRule> columnRules, TableMetadata? meta)
    {
        if (columnRules.Count == 0)
        {
            return false;
        }

        if (meta is null)
        {
            return columnRules.Any(r => r.AccessLevel == ColumnAccessLevel.Clear);
        }

        var maskedByDefault = meta.Columns
            .Where(col => ClassificationMetadata.IsColumnSensitive(meta, col))
            .Select(col => col.ColumnName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return columnRules.Any(r => r.AccessLevel == ColumnAccessLevel.Clear && maskedByDefault.Contains(r.ColumnName));
    }

    private static ConsentRequest CloneWithStatus(ConsentRequest req, string status, DateTimeOffset updatedAt) => new()
    {
        Id = req.Id,
        TableId = req.TableId,
        TableIdentifier = req.TableIdentifier,
        RequesterSid = req.RequesterSid,
        RequestedGranteeType = req.RequestedGranteeType,
        RequestedGranteeRef = req.RequestedGranteeRef,
        BusinessJustification = req.BusinessJustification,
        Status = status,
        RequestedAt = req.RequestedAt,
        RequestedValidTo = req.RequestedValidTo,
        TenantId = req.TenantId,
        ContextJson = req.ContextJson,
        RequesterIdentifiers = [.. req.RequesterIdentifiers],
        ApprovalSteps = [.. req.ApprovalSteps],
        RequestedWriteRules = req.RequestedWriteRules,
        IncludeReadAccess = req.IncludeReadAccess,
        Version = req.Version,
        WorkflowMode = req.WorkflowMode,
        ExternalSystemId = req.ExternalSystemId,
        ExternalReference = req.ExternalReference,
        RequestedValidFrom = req.RequestedValidFrom,
        UpdatedAt = updatedAt,
        RequestKind = req.RequestKind,
        ProfileId = req.ProfileId
    };

    private sealed record ColumnMaskInput(string ColumnName, string AccessLevel, List<string>? ApplicableScopes);
}
