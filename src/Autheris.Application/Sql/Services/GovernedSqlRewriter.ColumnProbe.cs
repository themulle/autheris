namespace Autheris.Application.Sql.Services;

using System;
using System.Collections.Generic;
using Autheris.Application.Sql.Internal;
using Autheris.Application.Sql.Policy;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using TrinoSqlEngine.Governance;

public sealed partial class GovernedSqlRewriter
{
    /// <summary>
    /// The probe that lets the executor name an unknown column: only for a plain SELECT on a single source whose tables are ALL fully visible
    /// to the caller (a decided table with a hidden column, a metric view or any missing decision gives none, so the denial stays unnamed).
    /// </summary>
    private static UnknownColumnProbe? BuildColumnProbe(
        string clientSql,
        bool isPlainSelect,
        int distinctCatalogCount,
        IReadOnlyDictionary<TableIdentity, TableAccessDecision> decisions,
        IReadOnlyDictionary<TableIdentity, TableMetadata> decidedTableMetadata)
    {
        if (!isPlainSelect || distinctCatalogCount > 1 || decidedTableMetadata.Count == 0 || decisions.Count != decidedTableMetadata.Count)
        {
            return null;
        }

        var catalogColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (identity, meta) in decidedTableMetadata)
        {
            if (meta.Table.ObjectKind == DiscoveryObjectKind.MetricView ||
                !decisions.TryGetValue(identity, out var decision) ||
                !ColumnVisibility.IsFullyVisible(decision, meta))
            {
                return null;
            }

            foreach (var column in meta.Columns)
            {
                catalogColumns.Add(column.ColumnName);
            }
        }

        return new UnknownColumnProbe(clientSql, catalogColumns);
    }
}
