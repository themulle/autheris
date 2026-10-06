namespace TrinoSqlEngine;

using System;
using System.Globalization;

public sealed partial class FastSqlEngine
{
    /// <summary>
    /// SQ-05: Builds a dialect-compliant table alias expression for subqueries.
    /// For SQL Server, simple unqualified names are quoted in brackets [tableName].
    /// For other dialects, simple unqualified names are emitted.
    /// </summary>
    [Obsolete("Use AST dialect generator (ISqlDialectGenerator) instead.")]
    public static string FormatTableAlias(string normalizedTableName, TargetSqlDialect dialect)
    {
        string simpleName = SqlIdentifierHelper.NormalizeIdentifier(SqlIdentifierHelper.GetSimpleName(normalizedTableName));
        if (dialect == TargetSqlDialect.SqlServer)
        {
            return $"[{simpleName}]";
        }
        return simpleName;
    }

    /// <summary>
    /// SQ-05: Builds a T-SQL compliant pagination clause (OFFSET ... ROWS FETCH NEXT ... ROWS ONLY).
    /// If an OFFSET clause is already present, only the FETCH NEXT clause is returned.
    /// If no ORDER BY exists, an ORDER BY (SELECT NULL) is prepended because T-SQL requires ORDER BY for OFFSET/FETCH.
    /// </summary>
    [Obsolete("Use AST dialect generator (ISqlDialectGenerator) instead.")]
    public static string BuildTsqlLimitClause(long rowCount, bool hasOffset, bool hasOrderBy)
    {
        string countStr = rowCount.ToString(CultureInfo.InvariantCulture);
        if (hasOffset)
        {
            return $"FETCH NEXT {countStr} ROWS ONLY";
        }

        if (hasOrderBy)
        {
            return $"OFFSET 0 ROWS FETCH NEXT {countStr} ROWS ONLY";
        }

        return $"ORDER BY (SELECT NULL) OFFSET 0 ROWS FETCH NEXT {countStr} ROWS ONLY";
    }
}
