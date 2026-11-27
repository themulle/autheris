namespace Autheris.Application.Governance.Provisioning;

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

/// <summary>
/// Strict naming rules for the Unity Catalog securables the provisioning path touches. A name segment is lower case ASCII
/// letters, digits and underscores (1 to 255 characters); anything else is never provisioned, so no name needs quoting.
/// </summary>
public static partial class UcSecurableNames
{
    [GeneratedRegex(@"^[a-z0-9_]{1,255}\z", RegexOptions.CultureInvariant)]
    private static partial Regex SegmentRegex();

    public static bool IsValidSegment(string? segment) => segment is not null && SegmentRegex().IsMatch(segment);

    /// <summary>Lower-cases and validates one segment; false for a segment outside the naming rule.</summary>
    public static bool TryNormalizeSegment(string? raw, out string segment)
    {
        segment = raw is null ? string.Empty : raw.Trim().ToLowerInvariant();
        return IsValidSegment(segment);
    }

    /// <summary>Parses a <c>catalog.schema</c> entry of the allowed schemas into its normalized form.</summary>
    public static bool TryParseSchema(string? raw, out string catalog, out string schema)
    {
        catalog = string.Empty;
        schema = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var parts = raw.Split('.');
        return parts.Length == 2 && TryNormalizeSegment(parts[0], out catalog) && TryNormalizeSegment(parts[1], out schema);
    }

    /// <summary>The normalized allowed schemas (<c>catalog.schema</c>); invalid entries are dropped (startup validation rejects them).</summary>
    public static HashSet<string> AllowedSchemaSet(IEnumerable<string>? allowedSchemas)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (allowedSchemas is null)
        {
            return set;
        }

        foreach (var entry in allowedSchemas)
        {
            if (TryParseSchema(entry, out var catalog, out var schema))
            {
                set.Add(catalog + "." + schema);
            }
        }

        return set;
    }

    /// <summary>
    /// Whether <paramref name="fullName"/> is a well-formed name of <paramref name="type"/> inside the allowed schemas: a table or
    /// schema below an allowed schema, or the catalog of an allowed schema.
    /// </summary>
    public static bool IsInsideAllowedSchemas(UcSecurableType type, string? fullName, IReadOnlySet<string> allowedSchemas)
    {
        ArgumentNullException.ThrowIfNull(allowedSchemas);
        if (string.IsNullOrEmpty(fullName))
        {
            return false;
        }

        var parts = fullName.Split('.');
        foreach (var part in parts)
        {
            if (!IsValidSegment(part))
            {
                return false;
            }
        }

        switch (type)
        {
            case UcSecurableType.Table when parts.Length == 3:
                return allowedSchemas.Contains(parts[0] + "." + parts[1]);
            case UcSecurableType.Schema when parts.Length == 2:
                return allowedSchemas.Contains(fullName);
            case UcSecurableType.Catalog when parts.Length == 1:
                foreach (var allowed in allowedSchemas)
                {
                    if (allowed.StartsWith(fullName + ".", StringComparison.Ordinal))
                    {
                        return true;
                    }
                }

                return false;
            default:
                return false;
        }
    }
}
