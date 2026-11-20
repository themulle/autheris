using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Domain.Common;

namespace Autheris.Application.Catalog.OpenMetadata;

public sealed record OpenMetadataSyncResult(
    int SyncedTables,
    int SyncedConsents,
    int SyncedMaskingRules,
    IReadOnlyList<TableIdentifier> InvalidationTables,
    IReadOnlyList<string> Warnings,
    bool Success)
{
    /// <summary>
    /// SR-L-23: Warnings reduced to stable codes with counts (<c>OM_TABLE_MAP_FAILED: 3</c>), ordered by code. Contains no
    /// exception text and no table names; safe to return to GraphQL callers. <see cref="Warnings"/> keeps the full text for
    /// administrative REST/log paths.
    /// </summary>
    public IReadOnlyList<string> SanitizedWarnings =>
        Warnings
            .GroupBy(ClassifyWarning, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Key}: {g.Count().ToString(System.Globalization.CultureInfo.InvariantCulture)}")
            .ToList();

    internal static string ClassifyWarning(string warning)
    {
        if (warning.StartsWith("Skipped OpenMetadata table", StringComparison.Ordinal)) return "OM_TABLE_SKIPPED";
        if (warning.StartsWith("Rejected OpenMetadata tables", StringComparison.Ordinal)) return "OM_TABLE_COLLISION";
        if (warning.StartsWith("Failed to map or upsert table", StringComparison.Ordinal)) return "OM_TABLE_MAP_FAILED";
        if (warning.StartsWith("Failed to fetch tables", StringComparison.Ordinal)) return "OM_FETCH_FAILED";
        if (warning.StartsWith("Failed during policy/role/team", StringComparison.Ordinal)) return "OM_CONSENT_SYNC_FAILED";
        if (warning.StartsWith("Invalid OpenMetadata-derived consent", StringComparison.Ordinal)) return "OM_CONSENT_INVALID";
        if (warning.StartsWith("Failed to refresh", StringComparison.Ordinal)) return "OM_CONSENT_REFRESH_FAILED";
        if (warning.StartsWith("Failed to revoke", StringComparison.Ordinal)) return "OM_CONSENT_REVOKE_FAILED";
        if (warning.StartsWith("Failed to create consent", StringComparison.Ordinal)) return "OM_CONSENT_CREATE_FAILED";
        if (warning.StartsWith("Sync already in progress", StringComparison.Ordinal)) return "OM_SYNC_IN_PROGRESS";
        return "OM_WARNING";
    }
}
