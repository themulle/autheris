namespace Autheris.Application.Governance.Grants;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Domain.Model;
using Autheris.Domain.Security;

/// <summary>
/// Derives the <see cref="GrantSensitivity"/> of a grant from effective protection or catalog metadata.
/// Any gap (missing protection, null table, empty set) classifies as <see cref="GrantSensitivity.Sensitive"/> so that the shorter maximum applies (fail closed).
/// </summary>
public static class GrantClassifier
{
    public static GrantSensitivity Classify(IEnumerable<EffectiveProtection?>? protections)
    {
        if (protections is null)
        {
            return GrantSensitivity.Sensitive;
        }

        var any = false;
        foreach (var protection in protections)
        {
            any = true;
            if (RequiresSensitiveHandling(protection))
            {
                return GrantSensitivity.Sensitive;
            }
        }

        return any ? GrantSensitivity.Regular : GrantSensitivity.Sensitive;
    }

    public static GrantSensitivity Classify(EffectiveProtection? protection) =>
        RequiresSensitiveHandling(protection) ? GrantSensitivity.Sensitive : GrantSensitivity.Regular;

    public static bool RequiresSensitiveHandling(EffectiveProtection? protection)
    {
        if (protection is null)
        {
            return true;
        }

        if (protection.TableClass.NormalizeOrConfidential() > ProtectionClass.Internal)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(protection.TableMaskCategory))
        {
            return true;
        }

        return protection.Columns.Values.Any(static c =>
            c.EffectiveClass.NormalizeOrConfidential() > ProtectionClass.Internal ||
            !string.IsNullOrWhiteSpace(c.EffectiveMaskCategory));
    }

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
            if (t is null || ContainsSensitiveData(t))
            {
                return GrantSensitivity.Sensitive;
            }
        }

        return any ? GrantSensitivity.Regular : GrantSensitivity.Sensitive;
    }

    public static bool ContainsSensitiveData(TableMetadata table)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (ClassificationMetadata.IsTableSensitive(table))
        {
            return true;
        }

        return table.Columns.Any(c => c.Meta.TryGetValue("pii", out var pii) && IsTruthy(pii));
    }

    private static bool IsTruthy(string v) =>
        string.Equals(v, "true", StringComparison.OrdinalIgnoreCase) || v == "1";
}
