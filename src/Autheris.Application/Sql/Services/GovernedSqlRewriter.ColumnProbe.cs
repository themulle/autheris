namespace Autheris.Application.Sql.Services;

using System;
using System.Collections.Generic;
using Autheris.Application.Sql.Errors;
using Autheris.Application.Sql.Internal;
using Autheris.Application.Sql.Policy;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
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

    /// <summary>
    /// A row-policy guard refusal that is caused by a column the statement references but no table, alias or CTE defines answers like every
    /// other unknown column: <c>UNKNOWN_COLUMN</c> when the caller sees every table, else the denial of a hidden column. Without this, the
    /// guard's own code would tell an unknown column from a hidden one on a table with a row policy. It reads only the client text and the
    /// catalog (hidden columns included) and returns null when the refusal has another cause, so the guard's decision and code stay.
    /// </summary>
    private static Exception? UnresolvedColumnAnswer(
        string clientSql,
        bool textIsClientText,
        bool isPlainSelect,
        int distinctCatalogCount,
        TargetSqlDialect dialect,
        IReadOnlyDictionary<TableIdentity, TableAccessDecision> decisions,
        IReadOnlyDictionary<TableIdentity, TableMetadata> decidedTableMetadata)
    {
        if (decidedTableMetadata.Count == 0)
        {
            return null;
        }

        var catalogColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var meta in decidedTableMetadata.Values)
        {
            foreach (var column in meta.Columns)
            {
                catalogColumns.Add(column.ColumnName);
            }
        }

        var finding = UnknownColumnResolver.Classify(new UnknownColumnProbe(clientSql, catalogColumns), out var name, out var line, out var columnPos);
        if (finding == UnknownColumnResolver.Finding.None)
        {
            return null;
        }

        if (finding == UnknownColumnResolver.Finding.Named &&
            BuildColumnProbe(clientSql, isPlainSelect, distinctCatalogCount, decisions, decidedTableMetadata) is not null)
        {
            // a position of the gateway's own text would point into text the client did not write
            return new SqlUnknownColumnException(name, textIsClientText ? line : null, textIsClientText ? columnPos : null, dialect);
        }

        // the fixed text is the audit reason: no name, no position
        return new WebSqlTableDeniedException("Access denied: the statement references a column the caller cannot use.");
    }
}
