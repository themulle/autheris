namespace Autheris.Domain.Interfaces;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

public interface IAccessProfileRepository
{
    Task<IReadOnlyList<AccessProfile>> GetProfilesForSubjectAsync(TenantId tenantId, string subject, CancellationToken ct = default);
    Task<IReadOnlyList<AccessProfile>> GetAllProfilesAsync(TenantId tenantId, CancellationToken ct = default);
    Task<AccessProfile?> GetProfileAsync(TenantId tenantId, string profileId, CancellationToken ct = default);
    Task UpsertProfileAsync(AccessProfile profile, CancellationToken ct = default);
    Task<bool> DeleteProfileAsync(TenantId tenantId, string profileId, CancellationToken ct = default);

    /// <summary>
    /// WP-SC-03: the profiles that are EFFECTIVE for a caller, found through its user ids (SIDs, aliases), its groups and its roles. Expired,
    /// unapproved <c>Unmasked</c> and over-limit assignments are not returned. The result is ordered most restrictive first (Strict, Default,
    /// Unmasked; ties by profile id), so taking the first matching profile is deterministic. A store failure throws; it never returns an empty list.
    /// </summary>
    Task<IReadOnlyList<AccessProfile>> GetProfilesForSubjectsAsync(
        TenantId tenantId,
        IReadOnlyCollection<string> users,
        IReadOnlyCollection<string> groups,
        IReadOnlyCollection<string> roles,
        CancellationToken ct = default);

    /// <summary>
    /// WP-SC-03: adds or replaces ONE assignment (the grant period starts now). Individual users need an explicit expiry, an individual <c>Unmasked</c>
    /// grant needs four-eyes and at most 30 days, a break-glass exception at most 72 hours; otherwise
    /// <see cref="AccessProfileAssignmentRejectedException"/> is thrown and nothing is stored.
    /// </summary>
    Task<AccessProfileAssignment> AssignProfileAsync(TenantId tenantId, string profileId, AccessProfileAssignment assignment, CancellationToken ct = default);

    /// <summary>WP-SC-03: records the approval of an assignment (the second person; never the grantee, and for <c>Unmasked</c> and break-glass never the requester).</summary>
    Task<AccessProfileAssignment> ApproveAssignmentAsync(TenantId tenantId, string profileId, AccessProfileSubjectType subjectType, string subject, string approvedBy, CancellationToken ct = default);

    /// <summary>WP-SC-16: renews an assignment with a new approval (never by the grantee), restarts its grant period and increments its renewal count. Break-glass is not renewable.</summary>
    Task<AccessProfileAssignment> RenewAssignmentAsync(TenantId tenantId, string profileId, AccessProfileSubjectType subjectType, string subject, DateTimeOffset newExpiresAt, string approvedBy, CancellationToken ct = default);

    /// <summary>WP-SC-03: removes one assignment; false when it did not exist.</summary>
    Task<bool> RemoveAssignmentAsync(TenantId tenantId, string profileId, AccessProfileSubjectType subjectType, string subject, CancellationToken ct = default);
}
