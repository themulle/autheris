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
using Autheris.Domain.Tenancy;

/// <summary>
/// Evaluates the highest-precedence approver capacity held by a principal for a resolved <see cref="ApproverSet"/>.
/// Degraded group resolution always denies (<see langword="null"/>).
/// </summary>
public sealed class ApproverEligibility(IGovernanceCapabilities capabilities)
{
    private readonly IGovernanceCapabilities _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));

    /// <summary>
    /// Evaluates decider eligibility synchronously using the pre-resolved <see cref="SecurityPrincipalContext"/> group SIDs.
    /// Capacity precedence: <see cref="ApproverCapacity.DataOwner"/> &gt; <see cref="ApproverCapacity.Governance"/> &gt; <see cref="ApproverCapacity.ItAdmin"/>.
    /// </summary>
    public ApproverCapacity? Evaluate(SecurityPrincipalContext? decider, ApproverSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        if (!TryValidateBaseDecider(decider, set.Tenant))
        {
            return null;
        }

        if (set.OwnerGroupAssigned &&
            set.OwnerGroupSid is { } ownerGroupSid &&
            !string.IsNullOrWhiteSpace(ownerGroupSid.Value) &&
            IsTenantMatchingContext(decider!, set.Tenant) &&
            decider!.GroupSids.Contains(ownerGroupSid))
        {
            return ApproverCapacity.DataOwner;
        }

        if (set.GovernanceEligible && _capabilities.IsGovernance(decider!.Principal, set.Tenant))
        {
            return ApproverCapacity.Governance;
        }

        if (set.ItAdminEligible && _capabilities.IsItAdmin(decider!.Principal, set.Tenant))
        {
            return ApproverCapacity.ItAdmin;
        }

        return null;
    }

    /// <summary>
    /// Evaluates decider eligibility with live container ownership verification through <see cref="IGovernanceCapabilities.IsOwnerMemberAsync"/>.
    /// </summary>
    public async ValueTask<ApproverCapacity?> EvaluateAsync(
        SecurityPrincipalContext? decider,
        ApproverSet set,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(set);
        if (!TryValidateBaseDecider(decider, set.Tenant))
        {
            return null;
        }

        if (set.OwnerGroupAssigned && set.Container is { } container && container.Id != Guid.Empty)
        {
            if (IsTenantMatchingContext(decider!, set.Tenant) &&
                await _capabilities.IsOwnerMemberAsync(decider!, container, ct).ConfigureAwait(false))
            {
                return ApproverCapacity.DataOwner;
            }
        }
        else if (set.OwnerGroupAssigned &&
                 set.OwnerGroupSid is { } ownerGroupSid &&
                 !string.IsNullOrWhiteSpace(ownerGroupSid.Value) &&
                 IsTenantMatchingContext(decider!, set.Tenant) &&
                 decider!.GroupSids.Contains(ownerGroupSid))
        {
            return ApproverCapacity.DataOwner;
        }

        if (set.GovernanceEligible && _capabilities.IsGovernance(decider!.Principal, set.Tenant))
        {
            return ApproverCapacity.Governance;
        }

        if (set.ItAdminEligible && _capabilities.IsItAdmin(decider!.Principal, set.Tenant))
        {
            return ApproverCapacity.ItAdmin;
        }

        return null;
    }

    private static bool TryValidateBaseDecider(SecurityPrincipalContext? decider, TenantId targetTenant)
    {
        if (decider is null ||
            !decider.IsAuthenticated ||
            decider.TenantClaimInvalid ||
            string.IsNullOrWhiteSpace(targetTenant.Value) ||
            TenantId.IsReserved(targetTenant))
        {
            return false;
        }

        if (decider.GroupResolutionStatus != GroupResolutionStatus.Complete ||
            GroupResolutionClaims.HasAnyDegradedClaim(decider.Claims) ||
            SeparationOfDutiesService.IsDegradedGroupOrRoleResolution(decider.Principal))
        {
            return false;
        }

        return true;
    }

    private static bool IsTenantMatchingContext(SecurityPrincipalContext decider, TenantId objectTenant)
    {
        if (decider.Principal is null)
        {
            return decider.ResolvedTenant is { } resolvedNoPrincipal &&
                   string.Equals(resolvedNoPrincipal.Value, objectTenant.Value, StringComparison.OrdinalIgnoreCase);
        }

        var tenantClaim = TenantClaimReader.Read(decider.Principal);
        if (tenantClaim.Kind == ClaimTenantKind.Invalid)
        {
            return false;
        }

        if (tenantClaim.Kind == ClaimTenantKind.Valid)
        {
            return string.Equals(tenantClaim.Tenant.Value, objectTenant.Value, StringComparison.OrdinalIgnoreCase);
        }

        if (decider.TenantSelectedByHeader)
        {
            return false;
        }

        return decider.ResolvedTenant is { } resolved &&
               string.Equals(resolved.Value, objectTenant.Value, StringComparison.OrdinalIgnoreCase);
    }
}
