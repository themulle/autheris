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
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ValidTo { get; init; }
    public string? Justification { get; init; }
    public string? CreatedBy { get; init; }

    public bool IsActive(DateTimeOffset at) =>
        ValidTo == null || ValidTo.Value > at;

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
