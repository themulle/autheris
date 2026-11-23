namespace Autheris.Application.Policy.Classification;

using System;
using Autheris.Application.Sql.Masking;
using Autheris.Application.Sql.Policy;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using TrinoSqlEngine;
using TrinoSqlEngine.Governance;

/// <summary>Category-based masking (W5-01): the typed mask of a column whose effective mask category comes from its protection.</summary>
public sealed partial class ClassificationAccessResolver
{
    /// <summary>
    /// Typed mask for a column whose effective mask category is <paramref name="categoryCode"/>, taken from the category's mask rule
    /// (hierarchy inheritance applies). Returns <c>null</c> when the category has no usable rule; the caller then applies the
    /// type default mask, never clear.
    /// </summary>
    public MaskSpec? ResolveCategoryMaskSpec(
        ClassificationPolicyCatalog catalog,
        string categoryCode,
        string columnName,
        string? dataType,
        DatabaseDialect databaseDialect,
        TenantId tenant)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var dialect = PolicyParameterNamer.ToTargetDialect(databaseDialect);
        if (string.IsNullOrWhiteSpace(categoryCode) || string.IsNullOrWhiteSpace(columnName))
        {
            return null;
        }

        var normalized = categoryCode.Trim().ToUpperInvariant();
        if (!TryResolveRuleFromHierarchy(normalized, catalog, out var rule))
        {
            return null;
        }

        var resolution = _maskProvider.ResolveClassificationMask(
            rule.MaskFunction, rule.ParamsJson, columnName, dataType, dialect, tenant, rule.KeyRef);
        return resolution.IsDenied ? null : resolution.Spec;
    }
}
