namespace Autheris.Application.Sql.Services;

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Governance;

public sealed partial class GovernedSqlRewriter
{
    /// <summary>
    /// The declared primary key of the one table a paged statement reads, or null when keyset continuation can never apply:
    /// not exactly one table, a metric view, no declared key, a key wider than the limit, a dialect without a proven seek
    /// ordering, or (Oracle) a text key whose comparison and ordering may use different collations.
    /// </summary>
    internal static KeysetCandidate? KeysetCandidateFor(
        IReadOnlyDictionary<TableIdentity, TableMetadata> decidedTableMetadata,
        TargetSqlDialect dialect)
    {
        if (decidedTableMetadata.Count != 1
            || dialect is not (TargetSqlDialect.PostgreSql or TargetSqlDialect.SqlServer or TargetSqlDialect.DuckDb or TargetSqlDialect.Databricks or TargetSqlDialect.Oracle))
        {
            return null;
        }

        var table = decidedTableMetadata.Values.First();
        if (table.Table.ObjectKind == DiscoveryObjectKind.MetricView
            || table.PrimaryKeyColumns.Count is < 1 or > KeysetLimits.MaxKeyColumns)
        {
            return null;
        }

        if (dialect == TargetSqlDialect.Oracle)
        {
            foreach (var key in table.PrimaryKeyColumns)
            {
                var column = table.Columns.FirstOrDefault(c => string.Equals(c.ColumnName, key, System.StringComparison.OrdinalIgnoreCase));
                if (column is null || ColumnBindAdapter.Classify(column.DataType) is ColumnTypeClass.AnsiText or ColumnTypeClass.UnicodeText)
                {
                    return null;
                }
            }
        }

        return new KeysetCandidate(table.PrimaryKeyColumns.ToImmutableArray());
    }
}
