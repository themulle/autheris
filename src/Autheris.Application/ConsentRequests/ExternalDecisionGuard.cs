namespace Autheris.Application.ConsentRequests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.ConsentRequests;
using Autheris.Application.Identity;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Tenancy;

public interface IExternalDecisionGuard
{
    Task<IReadOnlyList<Sid>> ValidateAsync(
        ConsentDecisionCommand command,
        ConsentRequest request,
        ConsentWorkflowRoute? route,
        ExternalWorkflowSystem? system,
        CancellationToken ct = default);
}

/// <summary>
/// Fail-closed guard for external consent decisions. Validates system authorization, reference binding,
/// asserted approvers, optional SCIM directory resolution (<c>require_resolvable_approver</c>),
/// requester/group self-approval prohibition, and four-eyes distinctness.
/// </summary>
public sealed class ExternalDecisionGuard(
    IScimUserStore? userStore = null,
    IScimGroupStore? groupStore = null,
    IIdentityRealmRegistry? realmRegistry = null,
    ISeparationOfDutiesService? separationOfDutiesService = null) : IExternalDecisionGuard
{
    private readonly ISeparationOfDutiesService _sod = separationOfDutiesService ?? new SeparationOfDutiesService(groupStore, realmRegistry);
    public async Task<IReadOnlyList<Sid>> ValidateAsync(
        ConsentDecisionCommand command,
        ConsentRequest request,
        ConsentWorkflowRoute? route,
        ExternalWorkflowSystem? system,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(request);

        if (request.WorkflowMode != ConsentWorkflowMode.External)
        {
            throw new ConsentDecisionException(
                ConsentDecisionErrorCodes.WorkflowModeMismatch,
                $"Consent request '{request.Id}' uses internal workflow mode and cannot be decided externally.",
                statusCode: 409);
        }

        var attribution = command.ExternalAttribution
            ?? throw new ConsentDecisionException(
                "VALIDATION_ERROR",
                "External decision attribution is required for external workflow decisions.",
                statusCode: 400);

        if (string.IsNullOrWhiteSpace(attribution.ExternalReference))
        {
            throw new ConsentDecisionException(
                "VALIDATION_ERROR",
                "External decision attribution requires a non-empty external reference.",
                statusCode: 400);
        }

        ValidateExternalSystem(command.Principal, request, route, system, attribution);

        var assertedApprovers = attribution.EffectiveApprovers;
        if (assertedApprovers.Count == 0)
        {
            throw new ConsentDecisionException(
                ConsentDecisionErrorCodes.ApproverUnresolved,
                "External decision attribution must identify at least one approver.",
                statusCode: 400);
        }

        var requireResolvable = route?.RequireResolvableApprover ?? true;
        var realmId = realmRegistry?.GetDefaultRealm()?.Id ?? "default";
        if (request.RequestedGranteeType == GranteeType.Group &&
            SeparationOfDutiesService.IsDegradedGroupOrRoleResolution(command.Principal))
        {
            throw new ConsentDecisionException(
                ConsentDecisionErrorCodes.SelfApprovalForbidden,
                "Separation of duties violated: group or role resolution is degraded; denied fail-closed.",
                statusCode: 403);
        }

        var resolvedSids = new List<Sid>(assertedApprovers.Count);
        var seenInPayload = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var asserted in assertedApprovers)
        {
            var rawCandidates = CollectAssertedIdentifiers(asserted);
            if (rawCandidates.Count == 0)
            {
                throw new ConsentDecisionException(
                    ConsentDecisionErrorCodes.ApproverUnresolved,
                    "External approver attribution is missing an identifier.",
                    statusCode: 400);
            }

            ScimUser? scimUser = null;
            if (userStore != null)
            {
                scimUser = await ResolveScimUserAsync(request.TenantId, realmId, asserted, ct).ConfigureAwait(false);
            }

            if (requireResolvable && (userStore == null || scimUser == null))
            {
                throw new ConsentDecisionException(
                    ConsentDecisionErrorCodes.ApproverUnresolved,
                    $"Asserted external approver '{asserted.Subject}' could not be resolved in the identity directory.",
                    statusCode: 403);
            }

            if (scimUser != null && !scimUser.Active)
            {
                throw new ConsentDecisionException(
                    ConsentDecisionErrorCodes.ApproverUnresolved,
                    $"Asserted external approver '{asserted.Subject}' is inactive in the identity directory.",
                    statusCode: 403);
            }

            var allIds = new HashSet<string>(rawCandidates, StringComparer.OrdinalIgnoreCase);
            if (scimUser != null)
            {
                AddNonEmpty(allIds, scimUser.Id);
                AddNonEmpty(allIds, scimUser.ObjectSid);
                AddNonEmpty(allIds, scimUser.ExternalId);
                AddNonEmpty(allIds, scimUser.UserName);
                AddNonEmpty(allIds, scimUser.Email);
                AddNonEmpty(allIds, scimUser.UserPrincipalName);
                AddNonEmpty(allIds, scimUser.EntraObjectId);
            }

            var effectiveSidValue = FirstNonEmpty(scimUser?.ObjectSid, asserted.ResolvedSid, scimUser?.Id, asserted.Subject, asserted.Email);
            if (string.IsNullOrWhiteSpace(effectiveSidValue))
            {
                throw new ConsentDecisionException(
                    ConsentDecisionErrorCodes.ApproverUnresolved,
                    "External approver attribution could not be mapped to an approver SID.",
                    statusCode: 403);
            }

            var effectiveSid = new Sid(effectiveSidValue);

            var sod = await _sod.CheckConsentApprovalAsync(
                request.TenantId,
                request,
                principal: null,
                effectiveSid,
                additionalActorIdentifiers: allIds,
                checkPriorApprovers: true,
                groupLookupKeys: [scimUser?.UserKey ?? effectiveSid.Value],
                ct: ct).ConfigureAwait(false);
            if (!sod.Allowed)
            {
                throw new ConsentDecisionException(
                    sod.Kind == SodViolationKind.SameApprover
                        ? ConsentDecisionErrorCodes.FourEyesSameApprover
                        : ConsentDecisionErrorCodes.SelfApprovalForbidden,
                    sod.Reason,
                    statusCode: sod.Kind == SodViolationKind.SameApprover ? 409 : 403);
            }

            var normIds = SeparationOfDutiesService.CollectSubjectIdentifiers(null, effectiveSid.Value, allIds);
            if (normIds.Overlaps(seenInPayload))
            {
                throw new ConsentDecisionException(
                    ConsentDecisionErrorCodes.FourEyesSameApprover,
                    "Four-eyes principle violated: duplicate approvers in external decision payload.",
                    statusCode: 409);
            }

            seenInPayload.UnionWith(normIds);
            resolvedSids.Add(effectiveSid);
        }

        return resolvedSids;
    }

    private static void ValidateExternalSystem(
        ClaimsPrincipal? principal,
        ConsentRequest request,
        ConsentWorkflowRoute? route,
        ExternalWorkflowSystem? system,
        ExternalDecisionAttribution attribution)
    {
        if (request.ExternalSystemId.HasValue && request.ExternalSystemId.Value != attribution.ExternalSystemId)
        {
            throw new ConsentDecisionException(
                ConsentDecisionErrorCodes.ExternalSystemNotAuthorized,
                "External decision system does not match the request's assigned external system.",
                statusCode: 403);
        }

        if (route?.ExternalSystemId is { } routeSystemId && routeSystemId != attribution.ExternalSystemId)
        {
            throw new ConsentDecisionException(
                ConsentDecisionErrorCodes.ExternalSystemNotAuthorized,
                "External decision system does not match the active workflow route.",
                statusCode: 403);
        }

        if (!string.IsNullOrWhiteSpace(request.ExternalReference) &&
            !string.Equals(request.ExternalReference, attribution.ExternalReference, StringComparison.Ordinal))
        {
            throw new ConsentDecisionException(
                ConsentDecisionErrorCodes.ExternalSystemNotAuthorized,
                "External reference does not match the consent request.",
                statusCode: 403);
        }

        if (system is null || !system.Enabled || system.Id != attribution.ExternalSystemId || system.TenantId != request.TenantId)
        {
            throw new ConsentDecisionException(
                ConsentDecisionErrorCodes.ExternalSystemNotAuthorized,
                "External workflow system is missing, disabled, or belongs to another tenant.",
                statusCode: 403);
        }

        if (principal != null && !string.IsNullOrWhiteSpace(system.ClientId))
        {
            var callerClientId = principal.FindFirst("azp")?.Value
                ?? principal.FindFirst("appid")?.Value
                ?? principal.FindFirst("client_id")?.Value;
            if (!string.IsNullOrWhiteSpace(callerClientId) &&
                !string.Equals(callerClientId, system.ClientId, StringComparison.OrdinalIgnoreCase))
            {
                throw new ConsentDecisionException(
                    ConsentDecisionErrorCodes.ExternalSystemNotAuthorized,
                    "Caller client identity does not match the registered external workflow system.",
                    statusCode: 403);
            }
        }
    }

    private async Task<ScimUser?> ResolveScimUserAsync(
        TenantId tenantId,
        string realmId,
        AssertedApprover asserted,
        CancellationToken ct)
    {
        if (userStore == null)
        {
            return null;
        }

        foreach (var candidate in CollectAssertedIdentifiers(asserted))
        {
            var bySid = await userStore.GetUserByObjectSidAsync(tenantId, realmId, candidate, ct).ConfigureAwait(false);
            if (bySid != null) return bySid;

            var byId = await userStore.GetUserAsync(tenantId, realmId, candidate, ct).ConfigureAwait(false);
            if (byId != null) return byId;

            var byExt = await userStore.GetUserByExternalIdAsync(tenantId, realmId, candidate, ct).ConfigureAwait(false);
            if (byExt != null) return byExt;

            var byName = await userStore.GetUserByUserNameAsync(tenantId, realmId, candidate, ct).ConfigureAwait(false);
            if (byName != null) return byName;

            var byUpn = await userStore.GetUserByUpnAsync(tenantId, realmId, candidate, ct).ConfigureAwait(false);
            if (byUpn != null) return byUpn;

            var byUpnOrName = await userStore.GetUsersByUpnOrUserNameAsync(tenantId, realmId, candidate, ct).ConfigureAwait(false);
            if (byUpnOrName.Count > 1)
            {
                return null;
            }

            if (byUpnOrName.Count == 1)
            {
                return byUpnOrName[0];
            }
        }

        return null;
    }

    private static List<string> CollectAssertedIdentifiers(AssertedApprover asserted)
    {
        var list = new List<string>(3);
        void AddIfSet(string? val)
        {
            if (!string.IsNullOrWhiteSpace(val))
            {
                var trimmed = val.Trim();
                if (!list.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                {
                    list.Add(trimmed);
                }
            }
        }

        AddIfSet(asserted.ResolvedSid);
        AddIfSet(asserted.Subject);
        AddIfSet(asserted.Email);
        return list;
    }

    private static void AddNonEmpty(HashSet<string> set, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            set.Add(value.Trim());
        }
    }

    private static string? FirstNonEmpty(params string?[] candidates)
        => candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c))?.Trim();
}
