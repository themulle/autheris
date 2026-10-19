namespace Autheris.Domain.Model;

using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>WP-SC-03: who a profile is assigned to. Standing access is given to groups and roles; individual users are time-bound.</summary>
public enum AccessProfileSubjectType
{
    User = 0,
    Group = 1,
    Role = 2
}

/// <summary>WP-SC-03 / SEC-SC-04: a regular assignment, or a break-glass exception (at most 72 hours, approved after the fact within 24 hours).</summary>
public enum AccessProfileAssignmentKind
{
    Standard = 0,
    BreakGlass = 1
}

/// <summary>
/// One row of <c>ACCESS_PROFILE_ASSIGNMENTS</c>. <see cref="AssignedAt"/> is the start of the current grant period (set by the repository, a
/// renewal restarts it). <see cref="CreatedBy"/> is the requester, <see cref="ApprovedBy"/> the second person (SEC-SC-04: four-eyes).
/// </summary>
public sealed record AccessProfileAssignment
{
    public required string Subject { get; init; }
    public AccessProfileSubjectType SubjectType { get; init; } = AccessProfileSubjectType.User;
    public DateTimeOffset AssignedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ExpiresAt { get; init; }
    public string? CreatedBy { get; init; }
    public string? ApprovedBy { get; init; }
    public DateTimeOffset? ApprovedAt { get; init; }
    public string? Justification { get; init; }
    public AccessProfileAssignmentKind Kind { get; init; } = AccessProfileAssignmentKind.Standard;

    /// <summary>WP-SC-16: number of approved renewals of this assignment.</summary>
    public int RenewalCount { get; init; }
}

/// <summary>A write was refused because it violates the individual-grant or <c>Unmasked</c> rules. <see cref="Code"/> is a stable machine-readable reason.</summary>
public sealed class AccessProfileAssignmentRejectedException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// WP-SC-03 / SEC-SC-04 / SEC-SC-20: write-time validation, read-time effectiveness (fail closed) and deterministic precedence of profile assignments.
/// <c>Unmasked</c> takes effect only through (a) a group/role mapping approved by a second administrator, (b) a time-bound individual grant
/// (at most 30 days, requester, approver and grantee all different), or (c) a break-glass exception (at most 72 hours, approved after the fact within 24 hours).
/// </summary>
public static class AccessProfileAssignmentRules
{
    public static readonly TimeSpan MaxUnmaskedIndividual = TimeSpan.FromDays(30);
    public static readonly TimeSpan MaxRegularIndividual = TimeSpan.FromDays(180);
    public static readonly TimeSpan MaxBreakGlass = TimeSpan.FromHours(72);
    public static readonly TimeSpan BreakGlassApprovalWindow = TimeSpan.FromHours(24);

    /// <summary>Recertification cadence of a standing <c>Unmasked</c> group/role mapping (C1 route (a), owner Q5): the approval lapses after this period.</summary>
    public static readonly TimeSpan UnmaskedMappingRecertification = TimeSpan.FromDays(90);

    public const string RequiresExpiry = "INDIVIDUAL_GRANT_REQUIRES_EXPIRY";
    public const string ExpiryInThePast = "GRANT_EXPIRY_IN_THE_PAST";
    public const string TooLong = "GRANT_EXCEEDS_MAXIMUM_DURATION";
    public const string FourEyesRequired = "UNMASKED_REQUIRES_FOUR_EYES";
    public const string BreakGlassRequiresJustification = "BREAK_GLASS_REQUIRES_JUSTIFICATION";
    public const string SameApprover = "APPROVER_MUST_DIFFER";
    public const string SelfRenewal = "RENEWAL_BY_GRANTEE_FORBIDDEN";
    public const string BreakGlassNotRenewable = "BREAK_GLASS_NOT_RENEWABLE";
    public const string KindConflict = "ASSIGNMENT_KIND_CONFLICT";

    private static bool Same(string? a, string? b) => a != null && b != null && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>The maximum duration of an assignment of this kind to this subject type; null for a standing group/role mapping.</summary>
    public static TimeSpan? MaxDuration(AccessProfile profile, AccessProfileAssignment a)
    {
        if (a.Kind == AccessProfileAssignmentKind.BreakGlass)
        {
            return MaxBreakGlass;
        }

        if (a.SubjectType != AccessProfileSubjectType.User)
        {
            return null;
        }

        return profile.MaskingMode == MaskingPolicyMode.Unmasked ? MaxUnmaskedIndividual : MaxRegularIndividual;
    }

    /// <summary>
    /// The expiry that applies when the assignment is read: the earlier of the stored expiry and <c>AssignedAt + maximum</c> (a legacy assignment without
    /// expiry is clamped, never permanent). Null only for a standing group/role mapping without expiry.
    /// </summary>
    public static DateTimeOffset? EffectiveExpiry(AccessProfile profile, AccessProfileAssignment a)
    {
        var limit = MaxDuration(profile, a) is { } max ? a.AssignedAt + max : (DateTimeOffset?)null;
        if (a.ExpiresAt is not { } explicitExpiry)
        {
            return limit;
        }

        return limit is { } l && l < explicitExpiry ? l : explicitExpiry;
    }

    /// <summary>
    /// The instant from which <paramref name="a"/> no longer grants <paramref name="profile"/>, null when it never lapses by time (a standing group/role mapping of a
    /// non-<c>Unmasked</c> profile without expiry). It is the earlier of the clamped expiry, the end of the 24 h approval window of an unapproved break-glass
    /// exception and, for a standing <c>Unmasked</c> mapping, <c>ApprovedAt + 90 days</c> (recertification; no approval time means already lapsed).
    /// </summary>
    public static DateTimeOffset? EffectiveUntil(AccessProfile profile, AccessProfileAssignment a)
    {
        var until = EffectiveExpiry(profile, a);

        DateTimeOffset? Earlier(DateTimeOffset? current, DateTimeOffset bound) => current is { } c && c < bound ? c : bound;

        if (a.Kind == AccessProfileAssignmentKind.BreakGlass)
        {
            var approved = a.ApprovedBy != null && !Same(a.ApprovedBy, a.CreatedBy) && !Same(a.ApprovedBy, a.Subject);
            return approved ? until : Earlier(until, a.AssignedAt + BreakGlassApprovalWindow);
        }

        if (profile.MaskingMode == MaskingPolicyMode.Unmasked && a.SubjectType != AccessProfileSubjectType.User)
        {
            return Earlier(until, a.ApprovedAt is { } at ? at + UnmaskedMappingRecertification : DateTimeOffset.MinValue);
        }

        return until;
    }

    /// <summary>Throws <see cref="AccessProfileAssignmentRejectedException"/> when <paramref name="a"/> may not be stored for <paramref name="profile"/>.</summary>
    public static void ValidateForAssignment(AccessProfile profile, AccessProfileAssignment a, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(a);
        if (string.IsNullOrWhiteSpace(a.Subject))
        {
            throw new ArgumentException("The assignment subject must not be empty.", nameof(a));
        }

        var individual = a.SubjectType == AccessProfileSubjectType.User;
        if (a.ExpiresAt is { } expires && expires <= now)
        {
            throw new AccessProfileAssignmentRejectedException(ExpiryInThePast, "The assignment expiry is not in the future.");
        }

        if ((individual || a.Kind == AccessProfileAssignmentKind.BreakGlass) && a.ExpiresAt is null)
        {
            throw new AccessProfileAssignmentRejectedException(RequiresExpiry, "An individual grant or break-glass exception requires an expiry.");
        }

        if (MaxDuration(profile, a) is { } max && a.ExpiresAt is { } e && e > now + max)
        {
            throw new AccessProfileAssignmentRejectedException(TooLong, $"The assignment exceeds the maximum duration of {max.TotalHours:0} hours.");
        }

        var unmasked = profile.MaskingMode == MaskingPolicyMode.Unmasked;
        if (a.Kind == AccessProfileAssignmentKind.BreakGlass)
        {
            if (string.IsNullOrWhiteSpace(a.Justification) || string.IsNullOrWhiteSpace(a.CreatedBy))
            {
                throw new AccessProfileAssignmentRejectedException(BreakGlassRequiresJustification, "A break-glass exception needs a justification and a requester.");
            }

            if (a.ApprovedBy != null && (Same(a.ApprovedBy, a.CreatedBy) || Same(a.ApprovedBy, a.Subject)))
            {
                throw new AccessProfileAssignmentRejectedException(SameApprover, "The approver must differ from the requester and the grantee.");
            }

            return;
        }

        if (unmasked && individual && !HasFourEyes(a))
        {
            throw new AccessProfileAssignmentRejectedException(FourEyesRequired, "An individual Unmasked grant needs a requester, a justification and a different approver.");
        }
    }

    private static bool HasFourEyes(AccessProfileAssignment a) =>
        !string.IsNullOrWhiteSpace(a.CreatedBy) && !string.IsNullOrWhiteSpace(a.ApprovedBy) && !string.IsNullOrWhiteSpace(a.Justification) &&
        !Same(a.ApprovedBy, a.CreatedBy) && !Same(a.ApprovedBy, a.Subject) && !Same(a.CreatedBy, a.Subject);

    /// <summary>Validates an approval or renewal by <paramref name="approver"/> of an existing assignment (the grantee never approves; for four-eyes cases the requester does not either).</summary>
    public static void ValidateApprover(AccessProfile profile, AccessProfileAssignment existing, string approver)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(approver);
        if (Same(approver, existing.Subject))
        {
            throw new AccessProfileAssignmentRejectedException(SelfRenewal, "The grantee cannot approve or renew its own assignment.");
        }

        var fourEyes = existing.Kind == AccessProfileAssignmentKind.BreakGlass || profile.MaskingMode == MaskingPolicyMode.Unmasked;
        if (fourEyes && Same(approver, existing.CreatedBy))
        {
            throw new AccessProfileAssignmentRejectedException(SameApprover, "The approver must differ from the requester.");
        }
    }

    /// <summary>Whether <paramref name="a"/> grants <paramref name="profile"/> at <paramref name="now"/>. Anything doubtful is not effective (fail closed).</summary>
    public static bool IsEffective(AccessProfile profile, AccessProfileAssignment a, DateTimeOffset now)
    {
        // The profile's own validity is the stored one; the clamp of Unmasked access happens per assignment below (EffectiveExpiry).
        if (profile.ValidTo is { } profileEnd && profileEnd <= now)
        {
            return false;
        }

        if (EffectiveUntil(profile, a) is { } expiry && expiry <= now)
        {
            return false;
        }

        if (a.Kind == AccessProfileAssignmentKind.BreakGlass)
        {
            if (a.ExpiresAt is null || string.IsNullOrWhiteSpace(a.CreatedBy))
            {
                return false;
            }

            var approved = a.ApprovedBy != null && !Same(a.ApprovedBy, a.CreatedBy) && !Same(a.ApprovedBy, a.Subject);
            return approved || (a.ApprovedBy == null && now < a.AssignedAt + BreakGlassApprovalWindow);
        }

        if (profile.MaskingMode != MaskingPolicyMode.Unmasked)
        {
            return true; // Strict and Default can only tighten.
        }

        return a.SubjectType == AccessProfileSubjectType.User
            ? a.ExpiresAt is not null && HasFourEyes(a)
            : !string.IsNullOrWhiteSpace(a.CreatedBy) && !string.IsNullOrWhiteSpace(a.ApprovedBy) && !Same(a.ApprovedBy, a.CreatedBy);
    }

    /// <summary>Most restrictive first: Strict, Default, Unmasked; ties by profile id (ordinal) for a deterministic result.</summary>
    public static IReadOnlyList<AccessProfile> Order(IEnumerable<AccessProfile> profiles) =>
        profiles
            .OrderBy(p => Rank(p.MaskingMode))
            .ThenBy(p => p.ProfileId, StringComparer.Ordinal)
            .ToList();

    private static int Rank(MaskingPolicyMode mode) => mode switch
    {
        MaskingPolicyMode.Strict => 0,
        MaskingPolicyMode.Default => 1,
        MaskingPolicyMode.Unmasked => 2,
        _ => 0 // an unknown mode is treated as the most restrictive
    };
}
