namespace Autheris.Application.Policy;

using System;
using System.Collections.Generic;
using Autheris.Application.Sql.Masking;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using TrinoSqlEngine.Governance;

/// <summary>
/// Builds the typed column masks of an access decision (WP-X0l): the decision producer attaches them to
/// <see cref="TableAccessDecision.ColumnMasks"/>, from the same decision inputs (effective column levels, catalog masking rules) that
/// the legacy string consumers read, so the compiler path and the string path cannot disagree about which columns are masked.
/// </summary>
public sealed class ColumnMaskPolicyFactory
{
    private readonly SqlDataMaskingProvider _masks;

    public ColumnMaskPolicyFactory(SqlDataMaskingProvider masks)
    {
        _masks = masks ?? throw new ArgumentNullException(nameof(masks));
    }

    /// <summary>
    /// A typed mask for every column whose effective level is <see cref="ColumnAccessLevel.Mask"/>; null when no column is masked.
    /// A denied column carries no mask here (the assembly nullifies it, see <c>GovernancePolicyFactory</c>).
    /// </summary>
    /// <exception cref="System.Security.SecurityException">A mask cannot be expressed as a typed spec (the caller leaves the decision string-only).</exception>
    public IColumnMaskPolicy? ForDecision(TableAccessDecision decision, TableMetadata metadata, TenantId tenant)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(metadata);

        var specs = new Dictionary<string, MaskSpec>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in metadata.Columns)
        {
            if (decision.GetEffectiveColumnAccess(column.ColumnName, metadata) != ColumnAccessLevel.Mask)
            {
                continue;
            }

            var rule = metadata.ColumnMaskingRules.TryGetValue(column.ColumnName, out var found) ? found : null;
            specs[column.ColumnName] = _masks.GetMaskSpec(rule, column.ColumnName, column.DataType, metadata.Dialect, tenant);
        }

        // A masked level for a column that the catalog does not list still needs a typed mask, otherwise the decision would be
        // denied on the compiler path for a column that is never projected.
        foreach (var (name, level) in decision.ColumnAccess)
        {
            if (level == ColumnAccessLevel.Mask && !specs.ContainsKey(name))
            {
                specs[name] = _masks.GetMaskSpec(null, name, null, metadata.Dialect, tenant);
            }
        }

        return specs.Count == 0 ? null : new ColumnMaskPolicy(specs);
    }
}
