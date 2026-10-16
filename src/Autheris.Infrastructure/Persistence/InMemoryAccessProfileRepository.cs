namespace Autheris.Infrastructure.Persistence;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;

public sealed class InMemoryAccessProfileRepository : IAccessProfileRepository
{
    private readonly ConcurrentDictionary<(TenantId Tenant, string ProfileId), AccessProfile> _profiles = new();

    public Task<IReadOnlyList<AccessProfile>> GetProfilesForSubjectAsync(TenantId tenantId, string subject, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);

        var now = DateTimeOffset.UtcNow;
        var list = _profiles.Values
            .Where(p => p.TenantId == tenantId &&
                        p.IsActive(now) &&
                        p.AssignedSubjects.Any(s => string.Equals(s, subject, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        return Task.FromResult<IReadOnlyList<AccessProfile>>(list);
    }

    public Task<IReadOnlyList<AccessProfile>> GetAllProfilesAsync(TenantId tenantId, CancellationToken ct = default)
    {
        var list = _profiles.Values
            .Where(p => p.TenantId == tenantId)
            .OrderBy(p => p.Name)
            .ToList();

        return Task.FromResult<IReadOnlyList<AccessProfile>>(list);
    }

    public Task<AccessProfile?> GetProfileAsync(TenantId tenantId, string profileId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);

        _profiles.TryGetValue((tenantId, profileId), out var profile);
        return Task.FromResult(profile);
    }

    public Task UpsertProfileAsync(AccessProfile profile, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(profile.ProfileId);

        _profiles[(profile.TenantId, profile.ProfileId)] = profile;
        return Task.CompletedTask;
    }

    public Task<bool> DeleteProfileAsync(TenantId tenantId, string profileId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);

        var removed = _profiles.TryRemove((tenantId, profileId), out _);
        return Task.FromResult(removed);
    }

    // WP-SC-03: the typed assignment API of the in-memory test double. It applies the same rules as the persisted stores for the explicit
    // API; the legacy list-based reads above stay permissive. Production registers the persisted repositories.

    public Task<IReadOnlyList<AccessProfile>> GetProfilesForSubjectsAsync(
        TenantId tenantId, IReadOnlyCollection<string> users, IReadOnlyCollection<string> groups, IReadOnlyCollection<string> roles, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var wanted = new HashSet<(AccessProfileSubjectType, string)>();
        foreach (var u in users ?? []) wanted.Add((AccessProfileSubjectType.User, u.Trim().ToUpperInvariant()));
        foreach (var g in groups ?? []) wanted.Add((AccessProfileSubjectType.Group, g.Trim().ToUpperInvariant()));
        foreach (var r in roles ?? []) wanted.Add((AccessProfileSubjectType.Role, r.Trim().ToUpperInvariant()));

        var matches = _profiles.Values
            .Where(p => p.TenantId == tenantId &&
                        (p.Assignments.Any(a => wanted.Contains((a.SubjectType, a.Subject.Trim().ToUpperInvariant())) && AccessProfileAssignmentRules.IsEffective(p, a, now)) ||
                         // test-double only: a legacy profile without typed assignments keeps the permissive plain-subject match
                         (p.Assignments.Count == 0 && p.IsActive(now) && p.AssignedSubjects.Any(x => wanted.Contains((AccessProfileSubjectType.User, x.Trim().ToUpperInvariant()))))))
            .ToList();
        return Task.FromResult(AccessProfileAssignmentRules.Order(matches));
    }

    public Task<AccessProfileAssignment> AssignProfileAsync(TenantId tenantId, string profileId, AccessProfileAssignment assignment, CancellationToken ct = default)
    {
        var profile = RequireProfile(tenantId, profileId);
        var now = DateTimeOffset.UtcNow;
        var stored = assignment with { AssignedAt = now, Subject = assignment.Subject.Trim(), ApprovedAt = assignment.ApprovedBy is null ? null : assignment.ApprovedAt ?? now };
        AccessProfileAssignmentRules.ValidateForAssignment(profile, stored, now);
        Replace(profile, stored);
        return Task.FromResult(stored);
    }

    public Task<AccessProfileAssignment> ApproveAssignmentAsync(TenantId tenantId, string profileId, AccessProfileSubjectType subjectType, string subject, string approvedBy, CancellationToken ct = default)
    {
        var profile = RequireProfile(tenantId, profileId);
        var existing = RequireAssignment(profile, subjectType, subject);
        AccessProfileAssignmentRules.ValidateApprover(profile, existing, approvedBy);
        var approved = existing with { ApprovedBy = approvedBy.Trim(), ApprovedAt = DateTimeOffset.UtcNow };
        Replace(profile, approved);
        return Task.FromResult(approved);
    }

    public Task<AccessProfileAssignment> RenewAssignmentAsync(TenantId tenantId, string profileId, AccessProfileSubjectType subjectType, string subject, DateTimeOffset newExpiresAt, string approvedBy, CancellationToken ct = default)
    {
        var profile = RequireProfile(tenantId, profileId);
        var existing = RequireAssignment(profile, subjectType, subject);
        if (existing.Kind == AccessProfileAssignmentKind.BreakGlass)
        {
            throw new AccessProfileAssignmentRejectedException(AccessProfileAssignmentRules.BreakGlassNotRenewable, "A break-glass exception cannot be renewed.");
        }

        AccessProfileAssignmentRules.ValidateApprover(profile, existing, approvedBy);
        var now = DateTimeOffset.UtcNow;
        var renewed = existing with { AssignedAt = now, ExpiresAt = newExpiresAt, ApprovedBy = approvedBy.Trim(), ApprovedAt = now, RenewalCount = existing.RenewalCount + 1 };
        AccessProfileAssignmentRules.ValidateForAssignment(profile, renewed, now);
        Replace(profile, renewed);
        return Task.FromResult(renewed);
    }

    public Task<bool> RemoveAssignmentAsync(TenantId tenantId, string profileId, AccessProfileSubjectType subjectType, string subject, CancellationToken ct = default)
    {
        var profile = RequireProfile(tenantId, profileId);
        var removed = profile.Assignments.RemoveAll(a => a.SubjectType == subjectType && string.Equals(a.Subject, subject.Trim(), StringComparison.OrdinalIgnoreCase)) > 0;
        profile.AssignedSubjects.RemoveAll(s => string.Equals(s, subject.Trim(), StringComparison.OrdinalIgnoreCase) && !profile.Assignments.Any(a => string.Equals(a.Subject, s, StringComparison.OrdinalIgnoreCase)));
        return Task.FromResult(removed);
    }

    private AccessProfile RequireProfile(TenantId tenantId, string profileId) =>
        _profiles.TryGetValue((tenantId, profileId), out var p) ? p : throw new KeyNotFoundException($"Access profile '{profileId}' does not exist.");

    private static AccessProfileAssignment RequireAssignment(AccessProfile profile, AccessProfileSubjectType type, string subject) =>
        profile.Assignments.FirstOrDefault(a => a.SubjectType == type && string.Equals(a.Subject, subject.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? throw new KeyNotFoundException($"Assignment of '{subject}' does not exist.");

    private static void Replace(AccessProfile profile, AccessProfileAssignment assignment)
    {
        profile.Assignments.RemoveAll(a => a.SubjectType == assignment.SubjectType && string.Equals(a.Subject, assignment.Subject, StringComparison.OrdinalIgnoreCase));
        profile.Assignments.Add(assignment);
        if (!profile.AssignedSubjects.Contains(assignment.Subject, StringComparer.OrdinalIgnoreCase))
        {
            profile.AssignedSubjects.Add(assignment.Subject);
        }
    }
}
