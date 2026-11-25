namespace Autheris.Application.Governance.Ownership;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.External;
using Autheris.Application.Governance.ConsentRequests;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Security;

/// <summary>
/// The single seam through which an externally sourced deputy relation (issue #1126) makes a caller an eligible decider. A caller (A)
/// acts for a principal (B) only while a relation "A deputizes for B" is active, and only where B is a container owner-group member; B's
/// Data Governance capacity and B's own deputy relations are never consulted (no chains). Unknown, degraded or failing state answers "no".
/// </summary>
public interface IDeputyAuthority
{
    /// <summary>The relations under which the caller is an active deputy in <paramref name="tenant"/> at this instant; empty on any doubt.</summary>
    ValueTask<IReadOnlyList<DeputyRelation>> GetActiveRelationsAsync(SecurityPrincipalContext? caller, TenantId tenant, CancellationToken ct = default);

    /// <summary>
    /// True when the principal (B) of <paramref name="relation"/> is an effective member of <paramref name="ownerGroupSid"/> in
    /// <paramref name="tenant"/> (resolved through the group store, degraded resolution never confirms) and is not locked.
    /// </summary>
    ValueTask<bool> IsPrincipalOwnerMemberAsync(TenantId tenant, DeputyRelation relation, Sid ownerGroupSid, CancellationToken ct = default);

    /// <summary>
    /// True when the principal (B) of <paramref name="relation"/> is <paramref name="subjectRef"/> or (for a group subject) an effective
    /// member of it. A deputy never decides for B on B's own request or a grant that benefits B. Unknown state answers "yes" (excluded).
    /// </summary>
    ValueTask<bool> IsPrincipalSubjectAsync(TenantId tenant, DeputyRelation relation, string? subjectRef, CancellationToken ct = default);
}

/// <summary>
/// <see cref="IDeputyAuthority"/> over <see cref="IDeputyRelationRepository"/> and the SCIM group store. Relations are read live on every
/// call (no cache), so the end of a window, a removal or a reconciliation takes effect immediately.
/// </summary>
public sealed class DeputyAuthority(
    IDeputyRelationRepository repository,
    IScimGroupStore? groupStore = null,
    TimeProvider? timeProvider = null) : IDeputyAuthority
{
    /// <summary>Upper bound of relations read for one caller.</summary>
    public const int MaxRelationsPerCaller = 200;

    private readonly IDeputyRelationRepository _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async ValueTask<IReadOnlyList<DeputyRelation>> GetActiveRelationsAsync(
        SecurityPrincipalContext? caller, TenantId tenant, CancellationToken ct = default)
    {
        if (!IsEligibleCaller(caller, tenant))
        {
            return [];
        }

        var keys = DeputyKey.ForCaller(caller!);
        if (keys.Count == 0)
        {
            return [];
        }

        try
        {
            var now = _time.GetUtcNow();
            var rows = await _repository.ListAsync(tenant, new DeputyRelationFilter(DeputyKeys: keys, ActiveAt: now), MaxRelationsPerCaller, ct).ConfigureAwait(false);
            var active = new List<DeputyRelation>();
            foreach (var row in rows ?? [])
            {
                // Re-check what the store filtered: tenant, window and the identity match itself. A relation of a person with itself is inert.
                if (row.TenantId == tenant && row.IsActiveAt(now) &&
                    (keys.Contains(row.DeputyKey) || (row.DeputyAltKey is { } alt && keys.Contains(alt))) &&
                    !keys.Contains(row.PrincipalKey) && !(row.PrincipalAltKey is { } palt && keys.Contains(palt)) &&
                    !await IsLockedAsync(tenant, row.DeputyRef, ct).ConfigureAwait(false))
                {
                    active.Add(row);
                }
            }

            return active;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A deputyship that cannot be established never grants.
            return [];
        }
    }

    public async ValueTask<bool> IsPrincipalOwnerMemberAsync(TenantId tenant, DeputyRelation relation, Sid ownerGroupSid, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(relation);
        if (string.IsNullOrWhiteSpace(ownerGroupSid.Value) || relation.TenantId != tenant)
        {
            return false;
        }

        try
        {
            var groups = await PrincipalGroupsAsync(tenant, relation, ct).ConfigureAwait(false);
            return groups is not null && groups.Any(g => SeparationOfDutiesService.IdentifiersOverlap(g, ownerGroupSid.Value));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    public async ValueTask<bool> IsPrincipalSubjectAsync(TenantId tenant, DeputyRelation relation, string? subjectRef, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(relation);
        if (string.IsNullOrWhiteSpace(subjectRef))
        {
            return false;
        }

        if (PrincipalForms(relation).Any(f => SeparationOfDutiesService.IdentifiersOverlap(f, subjectRef)))
        {
            return true;
        }

        try
        {
            var groups = await PrincipalGroupsAsync(tenant, relation, ct).ConfigureAwait(false);
            // Unknown group membership of B never clears B as a beneficiary.
            return groups is null || groups.Any(g => SeparationOfDutiesService.IdentifiersOverlap(g, subjectRef));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return true;
        }
    }

    /// <summary>Every identifier form of B the relation knows (issue #1126; four-eyes compares them with the recorded approvers).</summary>
    public static IReadOnlyList<string> PrincipalIdentifierForms(DeputyRelation relation)
    {
        ArgumentNullException.ThrowIfNull(relation);
        return PrincipalForms(relation).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>Every identifier form of B the relation knows: the realm-qualified reference, the bare user key and the directory id.</summary>
    private static IEnumerable<string> PrincipalForms(DeputyRelation relation)
    {
        yield return relation.PrincipalRef;
        if (DeputyKey.TrySplit(relation.PrincipalRef, out var realm, out var key))
        {
            yield return key;
            if (relation.PrincipalAltKey is { } alt && DeputyKey.TrySplit(alt, out _, out var directoryId))
            {
                yield return directoryId;
                yield return realm + "|" + directoryId;
            }
        }
    }

    /// <summary>B's effective groups (SIDs and realm group keys); null when unknown, degraded or B is locked.</summary>
    private async Task<IReadOnlyCollection<string>?> PrincipalGroupsAsync(TenantId tenant, DeputyRelation relation, CancellationToken ct)
    {
        if (groupStore is null || !DeputyKey.TrySplit(relation.PrincipalRef, out var realmId, out var userKey) ||
            await IsLockedAsync(tenant, relation.PrincipalRef, ct).ConfigureAwait(false))
        {
            return null;
        }

        var effective = await groupStore.GetEffectiveGroupSidsForUserAsync(tenant, realmId, userKey, ct: ct).ConfigureAwait(false);
        if (effective is null || effective.MaxDepthExceeded || effective.HasNonCanonicalSid)
        {
            return null;
        }

        var groups = new List<string>(effective.GroupObjectSids);
        if (effective.GroupIds is not null)
        {
            foreach (var id in effective.GroupIds)
            {
                if (SidFormat.TryFormatRealmGroupKey(realmId, id, out var qualified))
                {
                    groups.Add(qualified);
                }
            }
        }

        return groups;
    }

    private async Task<bool> IsLockedAsync(TenantId tenant, string reference, CancellationToken ct) =>
        groupStore is IScimProvisioningStore locks &&
        DeputyKey.TrySplit(reference, out var realmId, out var userKey) &&
        await locks.IsSubjectLockedAsync(tenant, realmId, userKey, ct).ConfigureAwait(false);

    private static bool IsEligibleCaller(SecurityPrincipalContext? caller, TenantId tenant)
    {
        if (caller is null || !caller.IsAuthenticated || caller.TenantClaimInvalid || caller.Principal is null ||
            string.IsNullOrWhiteSpace(caller.UserSid.Value) ||
            string.IsNullOrWhiteSpace(tenant.Value) || TenantId.IsReserved(tenant) ||
            AppOnlyPrincipal.Is(caller.Principal))
        {
            return false;
        }

        if (caller.GroupResolutionStatus != GroupResolutionStatus.Complete ||
            GroupResolutionClaims.HasAnyDegradedClaim(caller.Claims) ||
            SeparationOfDutiesService.IsDegradedGroupOrRoleResolution(caller.Principal))
        {
            return false;
        }

        return caller.ResolvedTenant is { } resolved &&
               string.Equals(resolved.Value, tenant.Value, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Per-call memo over an <see cref="IDeputyAuthority"/> for one caller and one operation (one inbox page): the caller's relations are read
/// once and each principal's owner-group membership once per group. Never shared across callers or requests.
/// </summary>
public sealed class MemoizedDeputyAuthority(IDeputyAuthority inner) : IDeputyAuthority
{
    private readonly Dictionary<string, IReadOnlyList<DeputyRelation>> _relations = new(StringComparer.Ordinal);
    private readonly Dictionary<(Guid, string), bool> _membership = [];
    private readonly Dictionary<(Guid, string), bool> _subjects = [];

    public async ValueTask<IReadOnlyList<DeputyRelation>> GetActiveRelationsAsync(SecurityPrincipalContext? caller, TenantId tenant, CancellationToken ct = default)
    {
        var key = tenant.Value ?? string.Empty;
        if (!_relations.TryGetValue(key, out var relations))
        {
            relations = await inner.GetActiveRelationsAsync(caller, tenant, ct).ConfigureAwait(false);
            _relations[key] = relations;
        }

        return relations;
    }

    public async ValueTask<bool> IsPrincipalOwnerMemberAsync(TenantId tenant, DeputyRelation relation, Sid ownerGroupSid, CancellationToken ct = default)
    {
        var key = (relation.Id, ownerGroupSid.Value ?? string.Empty);
        if (!_membership.TryGetValue(key, out var member))
        {
            member = await inner.IsPrincipalOwnerMemberAsync(tenant, relation, ownerGroupSid, ct).ConfigureAwait(false);
            _membership[key] = member;
        }

        return member;
    }

    public async ValueTask<bool> IsPrincipalSubjectAsync(TenantId tenant, DeputyRelation relation, string? subjectRef, CancellationToken ct = default)
    {
        var key = (relation.Id, subjectRef ?? string.Empty);
        if (!_subjects.TryGetValue(key, out var subject))
        {
            subject = await inner.IsPrincipalSubjectAsync(tenant, relation, subjectRef, ct).ConfigureAwait(false);
            _subjects[key] = subject;
        }

        return subject;
    }
}
