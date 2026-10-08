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
        => FormatTableAlias(rawTableName: null, normalizedTableName, dialect);

    /// <summary>
    /// SQL-8: the alias that replaces a table reference keeps the spelling of the written name. A quoted name
    /// ("Orders") stays quoted; otherwise PostgreSQL/SQLite would fold the alias (Orders -> orders) and every
    /// "Orders".column reference of the statement would no longer resolve.
    /// </summary>
    public static string FormatTableAlias(string? rawTableName, string normalizedTableName, TargetSqlDialect dialect)
    {
        var lastRaw = rawTableName == null ? null : LastIdentifierPart(rawTableName);
        if (lastRaw is { Length: > 2 } && ((lastRaw[0] == '"' && lastRaw[^1] == '"') || (lastRaw[0] == '[' && lastRaw[^1] == ']') || (lastRaw[0] == '`' && lastRaw[^1] == '`')))
        {
            var inner = lastRaw[1..^1];
            return dialect == TargetSqlDialect.SqlServer
                ? $"[{inner.Replace("]", "]]", StringComparison.Ordinal)}]"
                : $"\"{inner.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
        }

        string simpleName = SqlIdentifierHelper.NormalizeIdentifier(SqlIdentifierHelper.GetSimpleName(normalizedTableName));
        if (dialect == TargetSqlDialect.SqlServer)
        {
            return $"[{simpleName}]";
        }
        return simpleName;
    }

    /// <summary>Last dot-separated part of a written table name; dots inside quotes do not split.</summary>
    private static string LastIdentifierPart(string rawTableName)
    {
        int start = 0;
        char? quote = null;
        for (int i = 0; i < rawTableName.Length; i++)
        {
            char c = rawTableName[i];
            if (quote != null)
            {
                if (c == quote) quote = null;
            }
            else if (c is '"' or '`')
            {
                quote = c;
            }
            else if (c == '[')
            {
                quote = ']';
            }
            else if (c == '.')
            {
                start = i + 1;
            }
        }

        return rawTableName[start..].Trim();
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
