namespace Autheris.Application.Governance.Grants;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Domain.Model;

/// <summary>
/// WP-SC-16: derives the <see cref="GrantSensitivity"/> of a grant from catalog metadata. Any gap in the metadata
/// (no table found, null column list) classifies as <see cref="GrantSensitivity.Sensitive"/> so that the shorter maximum applies (fail closed).
/// </summary>
public static class GrantClassifier
{
    public static GrantSensitivity Classify(IEnumerable<TableMetadata?>? tables)
    {
        if (tables is null)
        {
            return GrantSensitivity.Sensitive;
        }

        var any = false;
        foreach (var t in tables)
        {
            any = true;
            if (t is null || IsSensitive(t))
            {
                return GrantSensitivity.Sensitive;
            }
        }

        return any ? GrantSensitivity.Regular : GrantSensitivity.Sensitive;
    }

    public static bool IsSensitive(TableMetadata table)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (table.Table.IsHighlySensitive)
        {
            return true;
        }

        if (table.Table.Tags.Any(IsPiiTag))
        {
            return true;
        }

        return table.Columns.Any(c => c.IsSensitive || (c.Meta.TryGetValue("pii", out var pii) && IsTruthy(pii)));
    }

    private static bool IsPiiTag(string tag) =>
        tag.Contains("pii", StringComparison.OrdinalIgnoreCase) || tag.Contains("gdpr", StringComparison.OrdinalIgnoreCase);

    private static bool IsTruthy(string v) =>
        string.Equals(v, "true", StringComparison.OrdinalIgnoreCase) || v == "1";
}
