namespace Autheris.Application.Governance.ConsentRequests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Identity;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Security;

public enum SodViolationKind { None = 0, SelfApproval = 1, SameApprover = 2 }

public readonly record struct SodDecision(bool Allowed, SodViolationKind Kind = SodViolationKind.None, string Reason = "")
{
    public static SodDecision Allow() => new(true);
    public static SodDecision Deny(SodViolationKind kind, string reason) => new(false, kind, reason);
}

public interface ISeparationOfDutiesService
{
    Task<SodDecision> CheckConsentApprovalAsync(
        TenantId tenantId, ConsentRequest request, ClaimsPrincipal? principal, Sid approverSid,
        IReadOnlyCollection<string>? additionalActorIdentifiers = null, bool checkPriorApprovers = true,
        IReadOnlyCollection<string>? groupLookupKeys = null, CancellationToken ct = default);

    Task<SodDecision> CheckGroupMembershipAsync(
        TenantId tenantId, ClaimsPrincipal? principal, Sid actorSid, SecurityPrincipalContext? callerContext,
        IReadOnlyCollection<string>? lookupKeys, string? targetGroupRef, CancellationToken ct = default);
}

/// <summary>
/// Centralized separation-of-duties evaluator enforcing requester, direct grantee, prior-approver,
/// and fail-closed group membership checks across consent, grant, plan, and recertification flows.
/// </summary>
public sealed class SeparationOfDutiesService(
    IScimGroupStore? groupStore = null,
    IIdentityRealmRegistry? realmRegistry = null) : ISeparationOfDutiesService
{
    private static readonly string[] ExtraSubjectClaimTypes =
    [
        ClaimTypes.PrimarySid, ClaimTypes.NameIdentifier, "objectSid", "onprem_sid",
        "oid", "sub", ClaimTypes.Upn, ClaimTypes.Email, "email", ClaimTypes.Name,
        "appid", "azp", "client_id"
    ];

    public async Task<SodDecision> CheckConsentApprovalAsync(
        TenantId tenantId, ConsentRequest request, ClaimsPrincipal? principal, Sid approverSid,
        IReadOnlyCollection<string>? additionalActorIdentifiers = null, bool checkPriorApprovers = true,
        IReadOnlyCollection<string>? groupLookupKeys = null, CancellationToken ct = default)
    {
        var actorIds = CollectSubjectIdentifiers(principal, approverSid.Value, additionalActorIdentifiers);
        if (actorIds.Overlaps(CollectRequesterIdentifiers(request.RequesterSid, request.RequesterIdentifiers)))
            return SodDecision.Deny(SodViolationKind.SelfApproval, "Separation of duties violated: The requester cannot approve their own consent request.");

        if (request.RequestedGranteeType is GranteeType.User or GranteeType.ServicePrincipal &&
            AnyOverlap(actorIds, request.RequestedGranteeRef))
            return SodDecision.Deny(SodViolationKind.SelfApproval, "Separation of duties violated: The requested grantee cannot approve their own consent request.");

        if (request.RequestedGranteeType == GranteeType.Group)
        {
            var groupCheck = await CheckGroupMembershipAsync(
                tenantId, principal, approverSid, null, groupLookupKeys, request.RequestedGranteeRef, ct).ConfigureAwait(false);
            if (!groupCheck.Allowed) return groupCheck;
        }

        if (checkPriorApprovers && actorIds.Overlaps(CollectPriorApproverIdentifiers(request.ApprovalSteps)))
            return SodDecision.Deny(SodViolationKind.SameApprover, "Four-eyes principle violated: this approver has already approved this request.");

        return SodDecision.Allow();
    }

    public async Task<SodDecision> CheckGroupMembershipAsync(
        TenantId tenantId, ClaimsPrincipal? principal, Sid actorSid, SecurityPrincipalContext? callerContext,
        IReadOnlyCollection<string>? lookupKeys, string? targetGroupRef, CancellationToken ct = default)
    {
        var targetForms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddIdentifierForms(targetForms, targetGroupRef);
        if (targetForms.Count == 0) return SodDecision.Allow();

        if (IsDegradedGroupOrRoleResolution(principal))
            return SodDecision.Deny(SodViolationKind.SelfApproval, "Separation of duties violated: group or role resolution is degraded; denied fail-closed.");

        var directSids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (principal is not null)
            foreach (var g in principal.GetGroupSids()) AddIdentifierForms(directSids, g.Value);
        if (callerContext is not null)
            foreach (var g in callerContext.GroupSids) AddIdentifierForms(directSids, g.Value);
        if (directSids.Overlaps(targetForms))
            return SodDecision.Deny(SodViolationKind.SelfApproval, "Separation of duties violated: a member of the requested target group cannot approve the request.");

        if (groupStore is null) return SodDecision.Allow();

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (lookupKeys is { Count: > 0 })
        {
            foreach (var k in lookupKeys) if (!string.IsNullOrWhiteSpace(k)) keys.Add(k.Trim());
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(actorSid.Value)) keys.Add(actorSid.Value.Trim());
            if (!string.IsNullOrWhiteSpace(callerContext?.UserSid.Value)) keys.Add(callerContext.UserSid.Value.Trim());
        }

        var realmId = realmRegistry?.GetDefaultRealm()?.Id ?? "default";
        foreach (var key in keys)
        {
            var effective = await groupStore.GetEffectiveGroupSidsForUserAsync(tenantId, realmId, key, ct: ct).ConfigureAwait(false);
            if (effective is null || effective.MaxDepthExceeded || effective.HasNonCanonicalSid)
                return SodDecision.Deny(SodViolationKind.SelfApproval, "Effective group membership depth exceeded or non-canonical SID encountered; denied (fail closed).");
            if (effective.GroupObjectSids.Any(g => AnyOverlap(targetForms, g)))
                return SodDecision.Deny(SodViolationKind.SelfApproval, "Separation of duties violated: a member of the requested target group cannot approve the request.");

            var directGroups = await groupStore.GetGroupMembershipsForUserAsync(tenantId, realmId, key, ct).ConfigureAwait(false);
            if (directGroups is not null && directGroups.Any(g => AnyOverlap(targetForms, g)))
                return SodDecision.Deny(SodViolationKind.SelfApproval, "Separation of duties violated: a member of the requested target group cannot approve the request.");
        }

        return SodDecision.Allow();
    }

    public static bool IsDegradedGroupOrRoleResolution(ClaimsPrincipal? principal) =>
        principal is not null &&
        (GroupResolutionClaims.IsMarkedDegraded(principal) ||
         principal.HasClaim(c =>
             string.Equals(c.Type, GroupResolutionClaims.StatusClaimType, StringComparison.OrdinalIgnoreCase) &&
             string.Equals(c.Value, GroupResolutionClaims.DegradedValue, StringComparison.OrdinalIgnoreCase)));

    public static HashSet<string> CollectSubjectIdentifiers(
        ClaimsPrincipal? principal, string? fallbackSid = null, IEnumerable<string>? additional = null)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddIdentifierForms(ids, fallbackSid);
        if (principal is not null)
        {
            foreach (var id in principal.GetUserIdentifiers()) AddIdentifierForms(ids, id);
            foreach (var claim in principal.Claims)
                if (ExtraSubjectClaimTypes.Contains(claim.Type, StringComparer.OrdinalIgnoreCase)) AddIdentifierForms(ids, claim.Value);
        }

        if (additional is not null)
            foreach (var item in additional) AddIdentifierForms(ids, item);
        return ids;
    }

    public static HashSet<string> CollectActorIdentifiers(ClaimsPrincipal? principal, Sid actorSid, SecurityPrincipalContext? callerContext)
    {
        var ids = CollectSubjectIdentifiers(principal, actorSid.Value);
        if (principal is not null)
        {
            foreach (var g in principal.GetGroupSids()) AddIdentifierForms(ids, g.Value);
            foreach (var r in principal.GetUserRoles()) AddIdentifierForms(ids, r);
        }

        if (callerContext is not null)
        {
            AddIdentifierForms(ids, callerContext.UserSid.Value);
            foreach (var g in callerContext.GroupSids) AddIdentifierForms(ids, g.Value);
            foreach (var r in callerContext.TenantRoles.Concat(callerContext.ClusterRoles)) AddIdentifierForms(ids, r);
        }

        return ids;
    }

    public static HashSet<string> CollectRequesterIdentifiers(Sid requesterSid, IEnumerable<string>? requesterIdentifiers)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddIdentifierForms(set, requesterSid.Value);
        if (requesterIdentifiers is not null)
            foreach (var id in requesterIdentifiers) AddIdentifierForms(set, id);
        return set;
    }

    public static HashSet<string> CollectPriorApproverIdentifiers(IEnumerable<ApprovalStep>? steps)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (steps is not null)
            foreach (var step in steps.Where(s => string.Equals(s.Decision, "APPROVED", StringComparison.OrdinalIgnoreCase)))
                AddIdentifierForms(set, step.ApproverSid.Value);
        return set;
    }

    public static int CountDistinctIndependentApprovers(IEnumerable<string>? approvers, string? requester, string? granteeRef)
    {
        if (approvers is null) return 0;
        var requesterForms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddIdentifierForms(requesterForms, requester);
        var granteeForms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddIdentifierForms(granteeForms, granteeRef);

        var counted = new List<HashSet<string>>();
        foreach (var raw in approvers)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var forms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddIdentifierForms(forms, raw);
            if ((requesterForms.Count > 0 && forms.Overlaps(requesterForms)) ||
                (granteeForms.Count > 0 && forms.Overlaps(granteeForms)) ||
                counted.Any(existing => existing.Overlaps(forms)))
                continue;
            counted.Add(forms);
        }

        return counted.Count;
    }

    public static bool IdentifiersOverlap(string? a, string? b)
    {
        var left = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddIdentifierForms(left, a);
        return left.Count > 0 && AnyOverlap(left, b);
    }

    public static bool AnyOverlap(IReadOnlySet<string> left, string? right)
    {
        if (left.Count == 0 || string.IsNullOrWhiteSpace(right)) return false;
        var forms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddIdentifierForms(forms, right);
        return forms.Overlaps(left);
    }

    public static void AddIdentifierForms(ISet<string> target, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var v = value.Trim();
        AddSingleForm(target, v);
        var colon = v.LastIndexOf(':', StringComparison.Ordinal);
        if (colon >= 0 && colon + 1 < v.Length) AddSingleForm(target, v[(colon + 1)..]);
    }

    private static void AddSingleForm(ISet<string> target, string v)
    {
        v = v.Trim();
        if (v.Length == 0) return;
        target.Add(v);
        var slash = v.LastIndexOf('\\', StringComparison.Ordinal);
        if (slash >= 0 && slash < v.Length - 1) target.Add(v[(slash + 1)..]);
        var at = v.IndexOf('@', StringComparison.Ordinal);
        if (at > 0) target.Add(v[..at]);
    }
}
