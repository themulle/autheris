namespace Autheris.Application.Governance.Requests;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.ConsentRequests;
using Autheris.Application.Governance.Ownership;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Autheris.Domain.Tenancy;

/// <summary>
/// Evaluates the highest-precedence approver capacity held by a principal for a resolved <see cref="ApproverSet"/>.
/// Degraded group resolution always denies (<see langword="null"/>).
/// </summary>
/// <param name="capabilities">Governance, IT and owner-group capabilities.</param>
/// <param name="deputies">
/// Externally sourced deputy relations (issue #1126). A caller acting for a principal under an active relation decides data and unmask
/// requests with <see cref="ApproverCapacity.DataOwner"/> capacity (never more) where that principal is an owner-group member of the
/// request's container; outside the window, or without this seam, a deputy holds nothing.
/// </param>
public sealed class ApproverEligibility(IGovernanceCapabilities capabilities, IDeputyAuthority? deputies = null)
{
    private readonly IGovernanceCapabilities _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
    private readonly IDeputyAuthority? _deputies = deputies;

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
        CancellationToken ct = default) =>
        (await EvaluateDetailedAsync(decider, set, ct).ConfigureAwait(false))?.Capacity;

    /// <summary>
    /// Like <see cref="EvaluateAsync"/>, and tells on whose behalf the owner capacity is held when it is held only as a deputy (issue #1126).
    /// For four-eyes (#1121) the deputy acting for a principal and that principal are the same decision authority.
    /// </summary>
    public async ValueTask<ApproverEligibilityResult?> EvaluateDetailedAsync(
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
            if (IsTenantMatchingContext(decider!, set.Tenant))
            {
                if (await _capabilities.IsOwnerMemberAsync(decider!, container, ct).ConfigureAwait(false))
                {
                    return new ApproverEligibilityResult(ApproverCapacity.DataOwner);
                }

                if (await ActingForAsync(decider!, set, ct).ConfigureAwait(false) is { } principal)
                {
                    return new ApproverEligibilityResult(ApproverCapacity.DataOwner, OnBehalfOf: principal.PrincipalRef)
                    {
                        OnBehalfOfIdentifiers = DeputyAuthority.PrincipalIdentifierForms(principal)
                    };
                }
            }
        }
        else if (set.OwnerGroupAssigned &&
                 set.OwnerGroupSid is { } ownerGroupSid &&
                 !string.IsNullOrWhiteSpace(ownerGroupSid.Value) &&
                 IsTenantMatchingContext(decider!, set.Tenant) &&
                 decider!.GroupSids.Contains(ownerGroupSid))
        {
            return new ApproverEligibilityResult(ApproverCapacity.DataOwner);
        }

        if (set.GovernanceEligible && _capabilities.IsGovernance(decider!.Principal, set.Tenant))
        {
            return new ApproverEligibilityResult(ApproverCapacity.Governance);
        }

        if (set.ItAdminEligible && _capabilities.IsItAdmin(decider!.Principal, set.Tenant))
        {
            return new ApproverEligibilityResult(ApproverCapacity.ItAdmin);
        }

        return null;
    }

    /// <summary>
    /// The principal the decider acts for as an active deputy (issue #1126): a principal that is an owner-group member of the request's
    /// container and is neither the requester nor a beneficiary. Data and unmask requests only; catalog visibility and profile
    /// assignments stay with the owner group, Data Governance and IT. Null when there is none.
    /// </summary>
    private async ValueTask<DeputyRelation?> ActingForAsync(SecurityPrincipalContext decider, ApproverSet set, CancellationToken ct)
    {
        if (_deputies is null || set.Kind is not (ConsentRequestKind.Data or ConsentRequestKind.Unmask) ||
            set.OwnerGroupSid is not { } ownerGroup || string.IsNullOrWhiteSpace(ownerGroup.Value))
        {
            return null;
        }

        foreach (var relation in await _deputies.GetActiveRelationsAsync(decider, set.Tenant, ct).ConfigureAwait(false))
        {
            if (await _deputies.IsPrincipalOwnerMemberAsync(set.Tenant, relation, ownerGroup, ct).ConfigureAwait(false) &&
                !await _deputies.IsPrincipalSubjectAsync(set.Tenant, relation, set.RequesterRef, ct).ConfigureAwait(false) &&
                !await _deputies.IsPrincipalSubjectAsync(set.Tenant, relation, set.GranteeRef, ct).ConfigureAwait(false))
            {
                return relation;
            }
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

/// <summary>
/// The capacity a decider holds. <paramref name="OnBehalfOf"/> is set when the owner capacity is held only as an active deputy (issue
/// #1126): the realm-qualified principal the decider acts for. Four-eyes (#1121) counts the decider and that principal as one authority.
/// </summary>
public readonly record struct ApproverEligibilityResult(ApproverCapacity Capacity, string? OnBehalfOf = null)
{
    public bool ViaDeputy => OnBehalfOf is not null;

    /// <summary>Every identifier form of the principal acted for (four-eyes compares them with the recorded approvers, #1121).</summary>
    public IReadOnlyList<string>? OnBehalfOfIdentifiers { get; init; }
}
