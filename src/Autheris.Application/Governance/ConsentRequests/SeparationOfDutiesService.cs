namespace Autheris.Application.Governance.ConsentRequests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Identity;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Security;

public enum SodViolationKind { None = 0, SelfApproval = 1, SameApprover = 2, SelfGrant = 3 }

public enum SodDecisionOutcome { Allow = 0, Deny = 1, SelfGrant = 2 }

public readonly record struct SodDecision(
    bool Allowed,
    SodViolationKind Kind = SodViolationKind.None,
    string Reason = "",
    SodDecisionOutcome Outcome = SodDecisionOutcome.Deny)
{
    public static SodDecision Allow() => new(true, SodViolationKind.None, string.Empty, SodDecisionOutcome.Allow);

    public static SodDecision Deny(SodViolationKind kind, string reason) =>
        new(false, kind, reason, SodDecisionOutcome.Deny);

    public static SodDecision SelfGrant(string reason = "Separation of duties detected grantee group membership; requires explicit IT self-grant flow.") =>
        new(false, SodViolationKind.SelfGrant, reason, SodDecisionOutcome.SelfGrant);
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

    SodDecision CheckPermissionDecision(
        SecurityPrincipalContext? decider,
        ConsentRequest request,
        IReadOnlySet<Sid>? granteeGroupMembersOfDecider) =>
        SodDecision.Deny(SodViolationKind.SelfApproval, "Separation-of-duties service must implement CheckPermissionDecision.");

    Task<SodDecision> CheckPermissionDecisionAsync(
        TenantId tenantId,
        SecurityPrincipalContext? decider,
        ConsentRequest request,
        CancellationToken ct = default) =>
        Task.FromResult(CheckPermissionDecision(decider, request, granteeGroupMembersOfDecider: null));
}

/// <summary>
/// Centralized separation-of-duties evaluator enforcing requester, direct grantee, prior-approver,
/// and fail-closed group membership checks across permission requests, decisions, self-grants, and renewals.
/// </summary>
public sealed class SeparationOfDutiesService(
    IScimGroupStore? groupStore = null,
    IIdentityRealmRegistry? realmRegistry = null) : ISeparationOfDutiesService
{
    private readonly IScimGroupStore? _groupStore = groupStore;
    private readonly IIdentityRealmRegistry? _realmRegistry = realmRegistry;
    private static readonly string[] ExtraSubjectClaimTypes =
    [
        ClaimTypes.PrimarySid, ClaimTypes.NameIdentifier, "objectSid", "onprem_sid",
        "oid", "sub", ClaimTypes.Upn, ClaimTypes.Email, "email", ClaimTypes.Name
    ];

    private static readonly string[] AppIdClaimTypes = ["appid", "azp", "client_id"];

    /// <summary>
    /// Evaluates per-person separation of duties for a unified permission request decision without consulting prior approval steps.
    /// Returns <see cref="SodDecisionOutcome.Allow"/>, <see cref="SodDecisionOutcome.Deny"/>, or
    /// <see cref="SodDecisionOutcome.SelfGrant"/> when the decider belongs to a requested grantee group.
    /// Degraded group resolution always denies.
    /// </summary>
    public SodDecision CheckPermissionDecision(
        SecurityPrincipalContext? decider,
        ConsentRequest request,
        IReadOnlySet<Sid>? granteeGroupMembersOfDecider)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (decider is null || !decider.IsAuthenticated)
        {
            return SodDecision.Deny(SodViolationKind.SelfApproval, "Separation of duties violated: authenticated decider context is required.");
        }

        if (decider.GroupResolutionStatus != GroupResolutionStatus.Complete ||
            GroupResolutionClaims.HasAnyDegradedClaim(decider.Claims) ||
            IsDegradedGroupOrRoleResolution(decider.Principal))
        {
            return SodDecision.Deny(SodViolationKind.SelfApproval, "Separation of duties violated: group resolution is degraded; denied fail-closed.");
        }

        var actorIds = CollectActorIdentifiers(decider.Principal, decider.UserSid, decider);
        var spIds = CollectServicePrincipalIdentifiers(
            decider.Principal,
            decider.UserSid.Value,
            string.IsNullOrWhiteSpace(decider.Caller?.ClientId) ? null : [decider.Caller.ClientId!]);

        foreach (var id in spIds)
        {
            actorIds.Add(id);
        }

        var requesterIds = CollectRequesterIdentifiers(request.RequesterSid, request.RequesterIdentifiers);
        if (actorIds.Overlaps(requesterIds))
        {
            return SodDecision.Deny(SodViolationKind.SelfApproval, "Separation of duties violated: the requester cannot decide their own request.");
        }

        if (request.RequestedGranteeType == GranteeType.User && AnyOverlap(actorIds, request.RequestedGranteeRef))
        {
            return SodDecision.Deny(SodViolationKind.SelfApproval, "Separation of duties violated: the requested user grantee cannot decide their own request.");
        }

        if (request.RequestedGranteeType == GranteeType.ServicePrincipal && AnyOverlap(spIds, request.RequestedGranteeRef))
        {
            return SodDecision.Deny(SodViolationKind.SelfApproval, "Separation of duties violated: the requested service principal grantee cannot decide their own request.");
        }

        if (request.RequestedGranteeType == GranteeType.Group)
        {
            if (granteeGroupMembersOfDecider is null)
            {
                return SodDecision.Deny(SodViolationKind.SelfApproval, "Separation of duties violated: group membership set is unavailable; denied fail-closed.");
            }

            var targetForms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddIdentifierForms(targetForms, request.RequestedGranteeRef);
            if (targetForms.Count == 0)
            {
                return SodDecision.Deny(SodViolationKind.SelfApproval, "Separation of duties violated: target group reference is missing.");
            }

            var combinedGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in decider.GroupSids)
            {
                AddIdentifierForms(combinedGroups, g.Value);
            }

            if (decider.Principal is not null)
            {
                foreach (var g in decider.Principal.GetGroupSids())
                {
                    AddIdentifierForms(combinedGroups, g.Value);
                }
            }

            foreach (var g in granteeGroupMembersOfDecider)
            {
                AddIdentifierForms(combinedGroups, g.Value);
            }

            if (combinedGroups.Overlaps(targetForms))
            {
                return SodDecision.SelfGrant("Decider belongs to the requested grantee group.");
            }
        }

        return SodDecision.Allow();
    }

    public async Task<SodDecision> CheckPermissionDecisionAsync(
        TenantId tenantId,
        SecurityPrincipalContext? decider,
        ConsentRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (decider is null || !decider.IsAuthenticated)
        {
            return SodDecision.Deny(SodViolationKind.SelfApproval, "Separation of duties violated: authenticated decider context is required.");
        }

        if (decider.GroupResolutionStatus != GroupResolutionStatus.Complete ||
            GroupResolutionClaims.HasAnyDegradedClaim(decider.Claims) ||
            IsDegradedGroupOrRoleResolution(decider.Principal))
        {
            return SodDecision.Deny(SodViolationKind.SelfApproval, "Separation of duties violated: group resolution is degraded; denied fail-closed.");
        }

        var members = new HashSet<Sid>(decider.GroupSids);
        if (request.RequestedGranteeType == GranteeType.Group && _groupStore is not null)
        {
            if (string.IsNullOrWhiteSpace(tenantId.Value))
            {
                return SodDecision.Deny(SodViolationKind.SelfApproval, "Separation of duties violated: tenant context is required for group membership verification; denied fail-closed.");
            }

            var realmId = ResolveRealmId(decider.Principal, decider.UserSid, decider, _realmRegistry);
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddLookupKey(keys, decider.UserSid.Value);
            if (decider.UserSid.Value.StartsWith($"{realmId}|", StringComparison.OrdinalIgnoreCase) && decider.UserSid.Value.Length > realmId.Length + 1)
            {
                keys.Add(decider.UserSid.Value[(realmId.Length + 1)..]);
            }

            foreach (var key in keys)
            {
                var effective = await _groupStore.GetEffectiveGroupSidsForUserAsync(tenantId, realmId, key, ct: ct).ConfigureAwait(false);
                if (effective is null || effective.MaxDepthExceeded || effective.HasNonCanonicalSid)
                {
                    return SodDecision.Deny(SodViolationKind.SelfApproval, "Effective group membership depth exceeded or non-canonical SID encountered; denied (fail closed).");
                }

                foreach (var sid in effective.GroupObjectSids)
                {
                    if (!string.IsNullOrWhiteSpace(sid))
                    {
                        members.Add(new Sid(sid));
                    }
                }

                var directGroups = await _groupStore.GetGroupMembershipsForUserAsync(tenantId, realmId, key, ct).ConfigureAwait(false);
                if (directGroups is not null)
                {
                    foreach (var g in directGroups)
                    {
                        if (string.IsNullOrWhiteSpace(g))
                        {
                            continue;
                        }

                        members.Add(new Sid(g));
                        if (!SidFormat.IsGroupSid(g) && SidFormat.TryFormatRealmGroupKey(realmId, g, out var qualified))
                        {
                            members.Add(new Sid(qualified));
                        }
                    }
                }
            }
        }

        return CheckPermissionDecision(decider, request, members);
    }

    public async Task<SodDecision> CheckConsentApprovalAsync(
        TenantId tenantId, ConsentRequest request, ClaimsPrincipal? principal, Sid approverSid,
        IReadOnlyCollection<string>? additionalActorIdentifiers = null, bool checkPriorApprovers = true,
        IReadOnlyCollection<string>? groupLookupKeys = null, CancellationToken ct = default)
    {
        var actorIds = CollectSubjectIdentifiers(principal, approverSid.Value, additionalActorIdentifiers);
        if (actorIds.Overlaps(CollectRequesterIdentifiers(request.RequesterSid, request.RequesterIdentifiers)))
            return SodDecision.Deny(SodViolationKind.SelfApproval, "Separation of duties violated: The requester cannot approve their own consent request.");

        if (request.RequestedGranteeType == GranteeType.User && AnyOverlap(actorIds, request.RequestedGranteeRef))
            return SodDecision.Deny(SodViolationKind.SelfApproval, "Separation of duties violated: The requested grantee cannot approve their own consent request.");

        if (request.RequestedGranteeType == GranteeType.ServicePrincipal)
        {
            var spIds = CollectServicePrincipalIdentifiers(principal, approverSid.Value, additionalActorIdentifiers);
            if (AnyOverlap(spIds, request.RequestedGranteeRef))
                return SodDecision.Deny(SodViolationKind.SelfApproval, "Separation of duties violated: The requested grantee cannot approve their own consent request.");
        }

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

        if (IsDegradedGroupOrRoleResolution(principal) ||
            IsDegradedGroupOrRoleResolution(callerContext?.Principal) ||
            callerContext?.GroupResolutionStatus == GroupResolutionStatus.Degraded ||
            GroupResolutionClaims.HasAnyDegradedClaim(callerContext?.Claims))
            return SodDecision.Deny(SodViolationKind.SelfApproval, "Separation of duties violated: group or role resolution is degraded; denied fail-closed.");

        var directSids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (principal is not null)
            foreach (var g in principal.GetGroupSids()) AddIdentifierForms(directSids, g.Value);
        if (callerContext is not null)
            foreach (var g in callerContext.GroupSids) AddIdentifierForms(directSids, g.Value);
        if (directSids.Overlaps(targetForms))
            return SodDecision.Deny(SodViolationKind.SelfApproval, "Separation of duties violated: a member of the requested target group cannot approve the request.");

        if (_groupStoreAccess is null) return SodDecision.Allow();
        if (string.IsNullOrWhiteSpace(tenantId.Value))
            return SodDecision.Deny(SodViolationKind.SelfApproval, "Separation of duties violated: tenant context is required for group membership verification; denied fail-closed.");

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (lookupKeys is { Count: > 0 })
        {
            foreach (var k in lookupKeys) AddLookupKey(keys, k);
        }
        else
        {
            AddLookupKey(keys, actorSid.Value);
            if (callerContext is not null) AddLookupKey(keys, callerContext.UserSid.Value);
        }

        var realmId = ResolveRealmId(principal, actorSid, callerContext, _realmRegistry);
        foreach (var existingKey in keys.ToList())
        {
            if (existingKey.StartsWith($"{realmId}|", StringComparison.OrdinalIgnoreCase) && existingKey.Length > realmId.Length + 1)
                keys.Add(existingKey[(realmId.Length + 1)..]);
        }

        foreach (var key in keys)
        {
            var effective = await _groupStoreAccess.GetEffectiveGroupSidsForUserAsync(tenantId, realmId, key, ct: ct).ConfigureAwait(false);
            if (effective is null || effective.MaxDepthExceeded || effective.HasNonCanonicalSid)
                return SodDecision.Deny(SodViolationKind.SelfApproval, "Effective group membership depth exceeded or non-canonical SID encountered; denied (fail closed).");
            if (effective.GroupObjectSids.Any(g => AnyOverlap(targetForms, g)))
                return SodDecision.Deny(SodViolationKind.SelfApproval, "Separation of duties violated: a member of the requested target group cannot approve the request.");

            var directGroups = await _groupStoreAccess.GetGroupMembershipsForUserAsync(tenantId, realmId, key, ct).ConfigureAwait(false);
            if (directGroups is not null && directGroups.Any(g =>
                    AnyOverlap(targetForms, g)
                    || (!SidFormat.IsGroupSid(g) && SidFormat.TryFormatRealmGroupKey(realmId, g, out var qualified) && AnyOverlap(targetForms, qualified))))
                return SodDecision.Deny(SodViolationKind.SelfApproval, "Separation of duties violated: a member of the requested target group cannot approve the request.");
        }

        return SodDecision.Allow();
    }

    private readonly IScimGroupStore? _groupStoreAccess = groupStore;

    public static bool IsDegradedGroupOrRoleResolution(ClaimsPrincipal? principal) =>
        principal is not null &&
        (GroupResolutionClaims.IsMarkedDegraded(principal) ||
         GroupResolutionClaims.HasAnyDegradedClaim(principal.Claims) ||
         (GroupResolutionClaims.HasPipelineMarker(principal) &&
          GroupResolutionClaims.Read(principal) != GroupResolutionStatus.Complete) ||
         (!GroupResolutionClaims.HasPipelineMarker(principal) &&
          GroupSidResolver.HasGroupOverage(principal)));

    public static HashSet<string> CollectSubjectIdentifiers(
        ClaimsPrincipal? principal, string? fallbackSid = null, IEnumerable<string>? additional = null)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddIdentifierForms(ids, fallbackSid);
        if (principal is not null)
        {
            var realmId = principal.FindFirst("autheris:realm")?.Value;
            var isPipelineBound = HasRealmBoundPipelinePrimarySid(principal);
            foreach (var id in principal.GetUserIdentifiers()) AddIdentifierForms(ids, id);
            foreach (var claim in principal.Claims)
                if (ExtraSubjectClaimTypes.Contains(claim.Type, StringComparer.OrdinalIgnoreCase))
                {
                    if (!(isPipelineBound && IsRawCrossRealmSubjectClaim(claim.Type)))
                        AddIdentifierForms(ids, claim.Value);
                    else if (!string.IsNullOrWhiteSpace(realmId) && !string.IsNullOrWhiteSpace(claim.Value))
                        AddIdentifierForms(ids, $"{realmId}|{claim.Value}");
                }
        }

        if (additional is not null)
            foreach (var item in additional) AddIdentifierForms(ids, item);
        return ids;
    }

    public static HashSet<string> CollectServicePrincipalIdentifiers(
        ClaimsPrincipal? principal, string? fallbackSid = null, IEnumerable<string>? additional = null)
    {
        var ids = CollectSubjectIdentifiers(principal, fallbackSid, additional);
        if (principal is not null)
        {
            var realmId = principal.FindFirst("autheris:realm")?.Value;
            var isPipelineBound = HasRealmBoundPipelinePrimarySid(principal);
            foreach (var claim in principal.Claims)
                if (AppIdClaimTypes.Contains(claim.Type, StringComparer.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(claim.Value))
                    AddIdentifierForms(ids, QualifyAppClaim(claim.Value, realmId, isPipelineBound));
        }

        return ids;
    }

    public static HashSet<string> CollectActorIdentifiers(ClaimsPrincipal? principal, Sid actorSid, SecurityPrincipalContext? callerContext)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddIdentifierForms(ids, actorSid.Value, includeLocalAccountForms: true);
        if (principal is not null)
        {
            var isPipelineBound = HasRealmBoundPipelinePrimarySid(principal);
            var realmId = principal.FindFirst("autheris:realm")?.Value;
            foreach (var id in principal.GetUserIdentifiers()) AddIdentifierForms(ids, id, includeLocalAccountForms: true);
            foreach (var claim in principal.Claims)
            {
                if (ExtraSubjectClaimTypes.Contains(claim.Type, StringComparer.OrdinalIgnoreCase))
                {
                    if (!(isPipelineBound && IsRawCrossRealmSubjectClaim(claim.Type)))
                        AddIdentifierForms(ids, claim.Value, includeLocalAccountForms: true);
                    else if (!string.IsNullOrWhiteSpace(realmId) && !string.IsNullOrWhiteSpace(claim.Value))
                        AddIdentifierForms(ids, $"{realmId}|{claim.Value}", includeLocalAccountForms: false);
                }
                else if (AppIdClaimTypes.Contains(claim.Type, StringComparer.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(claim.Value))
                    AddIdentifierForms(ids, QualifyAppClaim(claim.Value, realmId, isPipelineBound), includeLocalAccountForms: true);
            }
            foreach (var g in principal.GetGroupSids()) AddIdentifierForms(ids, g.Value, includeLocalAccountForms: true);
            foreach (var r in principal.GetUserRoles()) AddIdentifierForms(ids, r, includeLocalAccountForms: true);
        }

        if (callerContext is not null)
        {
            AddIdentifierForms(ids, callerContext.UserSid.Value, includeLocalAccountForms: true);
            if (!string.IsNullOrWhiteSpace(callerContext.Caller?.ClientId))
            {
                var realmId = callerContext.Principal?.FindFirst("autheris:realm")?.Value;
                var isPipelineBound = HasRealmBoundPipelinePrimarySid(callerContext.Principal);
                AddIdentifierForms(ids, QualifyAppClaim(callerContext.Caller!.ClientId!, realmId, isPipelineBound), includeLocalAccountForms: true);
            }

            if (callerContext.Claims is not null)
            {
                var isPipelineBound = HasRealmBoundPipelinePrimarySid(callerContext.Principal);
                var realmId = callerContext.Principal?.FindFirst("autheris:realm")?.Value
                    ?? callerContext.Claims.FirstOrDefault(c => string.Equals(c.Type, "autheris:realm", StringComparison.OrdinalIgnoreCase))?.Value;
                foreach (var claim in callerContext.Claims)
                {
                    if (ExtraSubjectClaimTypes.Contains(claim.Type, StringComparer.OrdinalIgnoreCase))
                    {
                        if (!(isPipelineBound && IsRawCrossRealmSubjectClaim(claim.Type)))
                            AddIdentifierForms(ids, claim.Value, includeLocalAccountForms: true);
                        else if (!string.IsNullOrWhiteSpace(realmId) && !string.IsNullOrWhiteSpace(claim.Value))
                            AddIdentifierForms(ids, $"{realmId}|{claim.Value}", includeLocalAccountForms: false);
                    }
                    else if (AppIdClaimTypes.Contains(claim.Type, StringComparer.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(claim.Value))
                        AddIdentifierForms(ids, QualifyAppClaim(claim.Value, realmId, isPipelineBound), includeLocalAccountForms: true);
                }
            }

            foreach (var g in callerContext.GroupSids) AddIdentifierForms(ids, g.Value, includeLocalAccountForms: true);
            foreach (var r in callerContext.TenantRoles.Concat(callerContext.ClusterRoles)) AddIdentifierForms(ids, r, includeLocalAccountForms: true);
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

    public static bool IdentifiersOverlap(string? a, string? b)
    {
        var left = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddIdentifierForms(left, a);
        return left.Count > 0 && AnyOverlap(left, b);
    }

    public static bool AnyOverlap(IReadOnlySet<string> left, string? right, bool includeLocalAccountForms = false)
    {
        if (left.Count == 0 || string.IsNullOrWhiteSpace(right)) return false;
        var forms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddIdentifierForms(forms, right, includeLocalAccountForms);
        return forms.Overlaps(left);
    }

    public static void AddIdentifierForms(ISet<string> target, string? value, bool includeLocalAccountForms = false)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var v = NormalizeIdentifier(value);
        if (v.Length == 0) return;
        var realmQualified = IsRealmQualifiedIdentifier(v);
        AddSingleForm(target, v, includeLocalAccountForms && !realmQualified);
        if (!realmQualified && TryStripEntityTypePrefix(v, out var stripped))
            AddSingleForm(target, stripped, includeLocalAccountForms && !IsRealmQualifiedIdentifier(stripped));
    }

    private static string NormalizeIdentifier(string value) =>
        value.Trim().Normalize(NormalizationForm.FormKC);

    private static void AddLookupKey(ISet<string> keys, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;
        var v = NormalizeIdentifier(raw);
        if (v.Length == 0) return;
        keys.Add(v);
        if (!IsRealmQualifiedIdentifier(v) && TryStripEntityTypePrefix(v, out var stripped)) keys.Add(stripped);
    }

    private static bool IsRealmQualifiedIdentifier(string value) =>
        SidFormat.IsRealmGroupKey(value) || value.StartsWith(SidFormat.RealmGroupKeyPrefix, StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("scim:", StringComparison.OrdinalIgnoreCase) || value.StartsWith("autheris:", StringComparison.OrdinalIgnoreCase)
        || value.Contains('|', StringComparison.Ordinal);

    private static bool TryStripEntityTypePrefix(string value, out string stripped)
    {
        var colon = value.IndexOf(':', StringComparison.Ordinal);
        if (colon > 0 && colon + 1 < value.Length && value[..colon] is var p &&
            (p.Equals("user", StringComparison.OrdinalIgnoreCase) || p.Equals("sp", StringComparison.OrdinalIgnoreCase) || p.Equals("group", StringComparison.OrdinalIgnoreCase)))
        {
            stripped = value[(colon + 1)..].Trim();
            return stripped.Length > 0;
        }
        stripped = string.Empty;
        return false;
    }

    private static string QualifyAppClaim(string raw, string? realmId, bool isPipelineBound) =>
        isPipelineBound && !string.IsNullOrWhiteSpace(realmId) ? $"{realmId}|{raw.Trim()}" : raw;

    private static bool HasRealmBoundPipelinePrimarySid(ClaimsPrincipal? principal) =>
        principal?.HasClaim(c => string.Equals(c.Type, ClaimTypes.PrimarySid, StringComparison.Ordinal)
            && string.Equals(c.Issuer, IdentityClaimIssuers.Pipeline, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(c.Value)) == true;

    private static bool IsRawCrossRealmSubjectClaim(string claimType) =>
        claimType is "oid" or "http://schemas.microsoft.com/identity/claims/objectidentifier" or "sub" or ClaimTypes.NameIdentifier;

    private static string ResolveRealmId(
        ClaimsPrincipal? principal, Sid actorSid, SecurityPrincipalContext? callerContext, IIdentityRealmRegistry? realmRegistry)
    {
        var claimRealm = principal?.FindFirst("autheris:realm")?.Value
            ?? callerContext?.Principal?.FindFirst("autheris:realm")?.Value;
        if (!string.IsNullOrWhiteSpace(claimRealm)) return claimRealm.Trim();
        var sidSource = callerContext?.UserSid.Value ?? actorSid.Value;
        var sep = sidSource.IndexOf('|', StringComparison.Ordinal);
        return sep > 0 ? sidSource[..sep].Trim() : realmRegistry?.GetDefaultRealm()?.Id ?? "default";
    }

    private static void AddSingleForm(ISet<string> target, string v, bool includeLocalAccountForms)
    {
        v = NormalizeIdentifier(v);
        if (v.Length == 0) return;
        target.Add(v);
        target.Add(v.ToUpperInvariant());
        if (!includeLocalAccountForms) return;
        var slash = v.LastIndexOf('\\', StringComparison.Ordinal);
        if (slash >= 0 && slash < v.Length - 1) target.Add(v[(slash + 1)..]);
        var at = v.IndexOf('@', StringComparison.Ordinal);
        if (at > 0) target.Add(v[..at]);
    }
}
