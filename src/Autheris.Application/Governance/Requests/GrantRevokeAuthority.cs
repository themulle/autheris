namespace Autheris.Application.Governance.Requests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.ConsentRequests;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Model;
using Autheris.Domain.Security;

/// <summary>How a caller is entitled to revoke a grant.</summary>
public enum GrantRevokeCapacity
{
    /// <summary>The caller is the (user or service principal) grantee.</summary>
    Grantee = 0,

    /// <summary>The caller is a member of the owner group of the grant's container.</summary>
    ContainerOwner = 1,

    /// <summary>The caller holds the Data Governance capacity.</summary>
    Governance = 2,

    /// <summary>The caller is an IT administrator and may decide requests for this target.</summary>
    ItAdmin = 3
}

/// <summary>A revocation the grant revoke authority verified: the capacity to audit and the repository authority to use.</summary>
public sealed record VerifiedGrantRevocation(GrantRevokeCapacity Capacity, ConsentRevocationAuthority Authority);

/// <summary>
/// Decides who may revoke a grant: the grantee, an owner group member of the container, Data Governance, or IT
/// only for grants it may also decide. Unknown or degraded state denies.
/// </summary>
public sealed class GrantRevokeAuthority(
    ApproverResolver approverResolver,
    ApproverEligibility approverEligibility,
    IGovernanceCapabilities capabilities)
{
    private readonly IGovernanceCapabilities _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
    private readonly ApproverResolver _approverResolver = approverResolver ?? throw new ArgumentNullException(nameof(approverResolver));
    private readonly ApproverEligibility _approverEligibility = approverEligibility ?? throw new ArgumentNullException(nameof(approverEligibility));

    /// <summary>Returns the capacity the caller revokes with, or <see langword="null"/> when the caller may not revoke the grant.</summary>
    public async ValueTask<GrantRevokeCapacity?> EvaluateAsync(
        SecurityPrincipalContext? caller,
        Consent grant,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(grant);
        if (caller is null || !caller.IsAuthenticated || caller.TenantClaimInvalid)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(grant.TenantId.Value) || TenantId.IsReserved(grant.TenantId))
        {
            return null;
        }

        // The caller must act in the tenant of the grant; a grant of another tenant is the same as a missing one.
        if (!string.Equals(caller.ResolvedTenant?.Value, grant.TenantId.Value, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (IsGrantee(caller, grant))
        {
            return GrantRevokeCapacity.Grantee;
        }

        try
        {
            if (HasDegradedGroupOrRoleResolution(caller))
            {
                return null;
            }

            if (_capabilities.IsGovernance(caller.Principal, grant.TenantId))
            {
                return GrantRevokeCapacity.Governance;
            }

            // A pattern grant spans objects of several containers; "no owner" for its (source, "", "") target must never
            // open the IT route. Only the grantee (above) and Data Governance may revoke it.
            if (grant.IsPatternGrant || string.IsNullOrWhiteSpace(grant.TableIdentifier.TableName))
            {
                return null;
            }

            var kind = grant.GrantKind == PermissionGrantKind.Unmask ? ConsentRequestKind.Unmask : ConsentRequestKind.Data;
            var set = await _approverResolver.ResolveAsync(
                grant.TenantId,
                kind,
                PermissionTarget.ForTable(grant.TableIdentifier),
                grant.GranteeSid ?? default,
                grant.GranteeSid?.Value ?? string.Empty,
                ct).ConfigureAwait(false);

            var capacity = await _approverEligibility.EvaluateAsync(caller, set, ct).ConfigureAwait(false);
            return capacity switch
            {
                ApproverCapacity.DataOwner => GrantRevokeCapacity.ContainerOwner,
                ApproverCapacity.Governance => GrantRevokeCapacity.Governance,
                ApproverCapacity.ItAdmin => GrantRevokeCapacity.ItAdmin,
                _ => null
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Authority that cannot be established never grants.
            return null;
        }
    }

    /// <summary>
    /// Verifies the revocation and returns the repository authority bound to the grant's tenant together with the capacity
    /// to audit; <see langword="null"/> when the caller may not revoke the grant.
    /// </summary>
    public async ValueTask<VerifiedGrantRevocation?> AuthorizeAsync(
        SecurityPrincipalContext? caller,
        Consent grant,
        CancellationToken ct = default)
    {
        var capacity = await EvaluateAsync(caller, grant, ct).ConfigureAwait(false);
        return capacity is { } verified
            ? new VerifiedGrantRevocation(verified, ConsentRevocationAuthority.ForVerifiedRevocation(grant.TenantId, verified.ToString()))
            : null;
    }

    /// <summary>
    /// A catalog visibility grant may be revoked by an owner group member of its container, Data Governance
    /// or IT. The caller must act in the grant's tenant; unknown or degraded state denies.
    /// </summary>
    public async ValueTask<GrantRevokeCapacity?> EvaluateCatalogAsync(
        SecurityPrincipalContext? caller,
        CatalogVisibilityGrant grant,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(grant);
        if (caller is null || !caller.IsAuthenticated || caller.TenantClaimInvalid ||
            HasDegradedGroupOrRoleResolution(caller) ||
            string.IsNullOrWhiteSpace(grant.TenantId.Value) || TenantId.IsReserved(grant.TenantId) ||
            grant.ContainerId == Guid.Empty ||
            !string.Equals(caller.ResolvedTenant?.Value, grant.TenantId.Value, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            if (_capabilities.IsGovernance(caller.Principal, grant.TenantId))
            {
                return GrantRevokeCapacity.Governance;
            }

            var set = await _approverResolver.ResolveAsync(
                grant.TenantId,
                ConsentRequestKind.Catalog,
                PermissionTarget.ForContainer(grant.ContainerKind, grant.ContainerId),
                grant.GranteeSid,
                grant.GranteeSid.Value ?? string.Empty,
                ct).ConfigureAwait(false);

            var capacity = await _approverEligibility.EvaluateAsync(caller, set, ct).ConfigureAwait(false);
            return capacity switch
            {
                ApproverCapacity.DataOwner => GrantRevokeCapacity.ContainerOwner,
                ApproverCapacity.Governance => GrantRevokeCapacity.Governance,
                ApproverCapacity.ItAdmin => GrantRevokeCapacity.ItAdmin,
                _ => null
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Authority that cannot be established never grants.
            return null;
        }
    }

    /// <summary>
    /// Evaluates revocation authority for a profile assignment: the grantee (user or SP), Data Governance (excluding plain
    /// ClusterAdmin/ItAdmin), or a ContainerOwner who owns every target in the assignment. Degraded resolution fails closed.
    /// </summary>
    public async ValueTask<GrantRevokeCapacity?> EvaluateProfileAssignmentAsync(
        SecurityPrincipalContext? caller,
        TenantId tenant,
        ConsentRequest? request,
        IReadOnlyList<Consent> grants,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(grants);
        if (caller is null || !caller.IsAuthenticated || caller.TenantClaimInvalid ||
            string.IsNullOrWhiteSpace(tenant.Value) || TenantId.IsReserved(tenant) ||
            !string.Equals(caller.ResolvedTenant?.Value, tenant.Value, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (grants.Count > 0 && grants.All(g => g.TenantId == tenant && IsGrantee(caller, g))) return GrantRevokeCapacity.Grantee;
        if (grants.Count == 0 && request is not null && request.TenantId == tenant &&
            MatchesSubject(caller, request.RequestedGranteeType, new Sid(request.RequestedGranteeRef))) return GrantRevokeCapacity.Grantee;
        if (HasDegradedGroupOrRoleResolution(caller)) return null;

        try
        {
            if (_capabilities.IsGovernance(caller.Principal, tenant)) return GrantRevokeCapacity.Governance;
            if (grants.Count == 0 || grants.Any(g => g.TenantId != tenant)) return null;

            var callerIds = SeparationOfDutiesService.CollectSubjectIdentifiers(caller.Principal, caller.UserSid.Value);
            var tableKinds = new Dictionary<TableIdentifier, ConsentRequestKind>();
            foreach (var grant in grants)
            {
                if (grant.IsPatternGrant || string.IsNullOrWhiteSpace(grant.TableIdentifier.TableName))
                {
                    if (!grant.ApprovingOwners.Any(o => SeparationOfDutiesService.AnyOverlap(callerIds, o) ||
                            caller.GroupSids.Any(gs => SeparationOfDutiesService.IdentifiersOverlap(gs.Value, o)))) return null;
                    continue;
                }

                tableKinds.TryAdd(grant.TableIdentifier, grant.GrantKind == PermissionGrantKind.Unmask
                    ? ConsentRequestKind.Unmask : ConsentRequestKind.Data);
            }

            var cache = new ApproverResolutionCache(tenant);
            foreach (var (table, kind) in tableKinds)
            {
                var set = await _approverResolver.ResolveAsync(tenant, kind, PermissionTarget.ForTable(table), default, string.Empty, cache, ct).ConfigureAwait(false);
                if (await _approverEligibility.EvaluateAsync(caller, set, ct).ConfigureAwait(false) != ApproverCapacity.DataOwner) return null;
            }

            return GrantRevokeCapacity.ContainerOwner;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static bool HasDegradedGroupOrRoleResolution(SecurityPrincipalContext caller) =>
        caller.GroupResolutionStatus != GroupResolutionStatus.Complete ||
        GroupResolutionClaims.HasAnyDegradedClaim(caller.Claims) ||
        SeparationOfDutiesService.IsDegradedGroupOrRoleResolution(caller.Principal);

    private static bool IsGrantee(SecurityPrincipalContext caller, Consent grant) =>
        grant.GranteeSid is { } granteeSid && MatchesSubject(caller, grant.GranteeType, granteeSid);

    private static bool MatchesSubject(SecurityPrincipalContext caller, GranteeType granteeType, Sid granteeSid)
    {
        if (granteeType is not (GranteeType.User or GranteeType.ServicePrincipal) || string.IsNullOrWhiteSpace(granteeSid.Value)) return false;
        var identifiers = granteeType == GranteeType.ServicePrincipal
            ? SeparationOfDutiesService.CollectServicePrincipalIdentifiers(
                caller.Principal, caller.UserSid.Value, string.IsNullOrWhiteSpace(caller.Caller?.ClientId) ? null : [caller.Caller.ClientId!])
            : SeparationOfDutiesService.CollectSubjectIdentifiers(caller.Principal, caller.UserSid.Value);
        return identifiers.Any(identifier => SeparationOfDutiesService.IdentifiersOverlap(identifier, granteeSid.Value));
    }
}
