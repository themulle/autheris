namespace Autheris.Application.Governance.Requests;

using System;
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
            if (caller.GroupResolutionStatus == GroupResolutionStatus.Complete &&
                _capabilities.IsGovernance(caller.Principal, grant.TenantId))
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
            caller.GroupResolutionStatus != GroupResolutionStatus.Complete ||
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

    private static bool IsGrantee(SecurityPrincipalContext caller, Consent grant)
    {
        if (grant.GranteeType is not (GranteeType.User or GranteeType.ServicePrincipal) ||
            grant.GranteeSid is not { } granteeSid ||
            string.IsNullOrWhiteSpace(granteeSid.Value))
        {
            return false;
        }

        var identifiers = SeparationOfDutiesService.CollectSubjectIdentifiers(caller.Principal, caller.UserSid.Value);
        foreach (var identifier in identifiers)
        {
            if (SeparationOfDutiesService.IdentifiersOverlap(identifier, granteeSid.Value))
            {
                return true;
            }
        }

        return false;
    }
}
