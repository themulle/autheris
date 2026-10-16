namespace Autheris.Infrastructure.Persistence;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;

/// <summary>WP-SC-03: persisted access profiles on SqlServer; the SQL and the rules live in <see cref="AccessProfileStore"/>.</summary>
public partial class SqlServerGovernanceRepository : IAccessProfileRepository
{
    private AccessProfileStore? _accessProfileStore;

    private AccessProfileStore AccessProfiles => _accessProfileStore ??= new AccessProfileStore(AccessProfileDialect.SqlServer, async (_, ct) =>
    {
        var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return new AccessProfileStoreScope(connection, connection.DisposeAsync);
    });

    public Task<IReadOnlyList<AccessProfile>> GetProfilesForSubjectAsync(TenantId tenantId, string subject, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        return AccessProfiles.GetProfilesForSubjectsAsync(tenantId, [subject], [], [], ct);
    }

    public Task<IReadOnlyList<AccessProfile>> GetProfilesForSubjectsAsync(
        TenantId tenantId, IReadOnlyCollection<string> users, IReadOnlyCollection<string> groups, IReadOnlyCollection<string> roles, CancellationToken ct = default) =>
        AccessProfiles.GetProfilesForSubjectsAsync(tenantId, users, groups, roles, ct);

    public Task<IReadOnlyList<AccessProfile>> GetAllProfilesAsync(TenantId tenantId, CancellationToken ct = default) =>
        AccessProfiles.GetAllProfilesAsync(tenantId, ct);

    public Task<AccessProfile?> GetProfileAsync(TenantId tenantId, string profileId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        return AccessProfiles.GetProfileAsync(tenantId, profileId, ct);
    }

    public Task UpsertProfileAsync(AccessProfile profile, CancellationToken ct = default) => AccessProfiles.UpsertProfileAsync(profile, ct);

    public Task<bool> DeleteProfileAsync(TenantId tenantId, string profileId, CancellationToken ct = default) =>
        AccessProfiles.DeleteProfileAsync(tenantId, profileId, ct);

    public Task<AccessProfileAssignment> AssignProfileAsync(TenantId tenantId, string profileId, AccessProfileAssignment assignment, CancellationToken ct = default) =>
        AccessProfiles.AssignProfileAsync(tenantId, profileId, assignment, ct);

    public Task<AccessProfileAssignment> ApproveAssignmentAsync(
        TenantId tenantId, string profileId, AccessProfileSubjectType subjectType, string subject, string approvedBy, CancellationToken ct = default) =>
        AccessProfiles.ApproveAssignmentAsync(tenantId, profileId, subjectType, subject, approvedBy, ct);

    public Task<AccessProfileAssignment> RenewAssignmentAsync(
        TenantId tenantId, string profileId, AccessProfileSubjectType subjectType, string subject, DateTimeOffset newExpiresAt, string approvedBy, CancellationToken ct = default) =>
        AccessProfiles.RenewAssignmentAsync(tenantId, profileId, subjectType, subject, newExpiresAt, approvedBy, ct);

    public Task<bool> RemoveAssignmentAsync(TenantId tenantId, string profileId, AccessProfileSubjectType subjectType, string subject, CancellationToken ct = default) =>
        AccessProfiles.RemoveAssignmentAsync(tenantId, profileId, subjectType, subject, ct);
}
