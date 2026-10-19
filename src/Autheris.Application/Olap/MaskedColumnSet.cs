namespace Autheris.Application.Olap;

using System;
using System.Collections.Generic;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;

/// <summary>
/// The columns whose staged values are masked strings (typed <c>VARCHAR</c> in DuckDB). Shared by the SQL federation and the OLAP
/// staging so that both stage the same schema for the same decision.
/// </summary>
public static class MaskedColumnSet
{
    public static HashSet<string> For(TableMetadata metadata, TableAccessDecision decision)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(decision);

        var masked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var col in metadata.Columns)
        {
            var access = decision.GetColumnAccess(col.ColumnName);
            if (access == ColumnAccessLevel.Mask ||
                (metadata.ColumnMaskingRules != null && metadata.ColumnMaskingRules.ContainsKey(col.ColumnName) && access != ColumnAccessLevel.Clear) ||
                decision.GetEffectiveColumnAccess(col.ColumnName, metadata) == ColumnAccessLevel.Mask)
            {
                masked.Add(col.ColumnName);
            }
        }

        return masked;
    }
}
