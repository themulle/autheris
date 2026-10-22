namespace Autheris.Domain.Model;

using System;
using System.Collections.Generic;
using Autheris.Domain.Common;

public sealed class AccessProfile
{
    public required string ProfileId { get; init; }
    public required string Name { get; init; }
    public required TenantId TenantId { get; init; }
    public MaskingPolicyMode MaskingMode { get; init; } = MaskingPolicyMode.Default;
    public List<string> TargetTables { get; init; } = []; // z. B. ["*.*"] oder ["tem.gps_position", "md.*"]
    public string? RowFilterPredicate { get; set; }      // z. B. "status != 'DELIVERED'"
    public List<string> AssignedSubjects { get; init; } = []; // SIDs oder Benutzernamen (z. B. ["david"])
    /// <summary>WP-SC-03: the typed assignments (users, groups, roles) with their approvals and expiry. <see cref="AssignedSubjects"/> lists the same subjects as plain strings.</summary>
    public List<AccessProfileAssignment> Assignments { get; init; } = [];
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ValidTo { get; init; }
    public string? Justification { get; init; }
    public string? CreatedBy { get; init; }

    /// <summary>
    /// EP-01: True if any target table pattern contains a wildcard ('*' or '?').
    /// </summary>
    public bool HasWildcardPattern =>
        TargetTables != null && TargetTables.Exists(p => p.Contains('*') || p.Contains('?'));

    /// <summary>
    /// WP-SC-16: expiry after the read-time clamp. A profile with typed assignments (as loaded for a caller: its effective, matched ones) stays active while ANY
    /// assignment is effective, so it ends at the latest <see cref="AccessProfileAssignmentRules.EffectiveUntil"/> of its assignments (null when one never lapses),
    /// never later than <see cref="ValidTo"/>. The loaded profile is cached, so every assignment expiry (individual cap, 24 h break-glass approval window,
    /// 90-day recertification of an <c>Unmasked</c> mapping) is re-checked at use. A legacy profile without assignments is bounded by its creation date.
    /// </summary>
    public DateTimeOffset? EffectiveValidTo
    {
        get
        {
            if (Assignments.Count == 0)
            {
                return IndividualGrantLimits.EffectiveProfileValidTo(MaskingMode, CreatedAt, ValidTo);
            }

            DateTimeOffset? latest = DateTimeOffset.MinValue;
            foreach (var a in Assignments)
            {
                if (AccessProfileAssignmentRules.EffectiveUntil(this, a) is not { } until)
                {
                    latest = null;
                    break;
                }

                if (until > latest)
                {
                    latest = until;
                }
            }

            return latest is { } l && ValidTo is { } v && v < l ? v : latest ?? ValidTo;
        }
    }

    public bool IsActive(DateTimeOffset at)
    {
        var effective = EffectiveValidTo;
        return effective == null || effective.Value > at;
    }

    public bool MatchesTable(TableIdentifier table)
    {
        if (TargetTables == null || TargetTables.Count == 0) return false;
        foreach (var pattern in TargetTables)
        {
            if (MatchesPattern(pattern, table))
            {
                return true;
            }
        }
        return false;
    }

    public static bool MatchesPattern(string pattern, TableIdentifier table)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return false;
        pattern = pattern.Trim();
        if (pattern == "*" || pattern == "*.*" || pattern == "*.*.*") return true;

        var parts = pattern.Split('.');
        if (parts.Length == 1)
        {
            return string.Equals(parts[0], "*", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(parts[0], table.TableName, StringComparison.OrdinalIgnoreCase);
        }
        if (parts.Length == 2)
        {
            bool schemaMatch = string.Equals(parts[0], "*", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(parts[0], table.Schema, StringComparison.OrdinalIgnoreCase);
            bool tableMatch = string.Equals(parts[1], "*", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(parts[1], table.TableName, StringComparison.OrdinalIgnoreCase);
            return schemaMatch && tableMatch;
        }
        if (parts.Length == 3)
        {
            bool domainMatch = string.Equals(parts[0], "*", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(parts[0], table.Domain, StringComparison.OrdinalIgnoreCase);
            bool schemaMatch = string.Equals(parts[1], "*", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(parts[1], table.Schema, StringComparison.OrdinalIgnoreCase);
            bool tableMatch = string.Equals(parts[2], "*", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(parts[2], table.TableName, StringComparison.OrdinalIgnoreCase);
            return domainMatch && schemaMatch && tableMatch;
        }
        return false;
    }
}
