namespace Autheris.Application.Sql.Federation.Pushdown;

using System;
using System.Linq;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using TrinoSqlEngine.Ast.Nodes;

/// <summary>
/// WP-122-2: Dialect capability matrix enforcing superset semantics, collation safety, and ANSI Three-Valued Logic (3VL).
/// </summary>
public static class PushabilityMatrix
{
    public static bool IsStringType(string? dataType)
    {
        if (string.IsNullOrWhiteSpace(dataType)) return false;
        var dt = dataType.Trim().ToLowerInvariant();
        return dt.StartsWith("varchar", StringComparison.Ordinal) ||
               dt.StartsWith("nvarchar", StringComparison.Ordinal) ||
               dt.StartsWith("char", StringComparison.Ordinal) ||
               dt.StartsWith("nchar", StringComparison.Ordinal) ||
               dt == "text" || dt == "ntext" || dt == "string" || dt == "citext";
    }

    public static bool HasExactCompareHint(TableColumn? column)
    {
        if (column == null) return false;
        return column.Meta.TryGetValue("exact_compare", out var val) &&
               string.Equals(val, "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// SEC-DATA122-04: Dialect collation safety check for negative filters on SQL Server.
    /// SQL Server string inequality drops rows that DuckDB would retain due to default case-insensitivity.
    /// </summary>
    public static bool IsNegativeFilterSafeForDialect(DatabaseDialect dialect, TableColumn? column)
    {
        if (dialect != DatabaseDialect.SqlServer)
        {
            return true;
        }

        if (column == null)
        {
            return false;
        }

        if (!IsStringType(column.DataType))
        {
            return true;
        }

        return HasExactCompareHint(column);
    }

    /// <summary>
    /// SQLA-10: a string range comparison (<c>&lt;</c>, <c>&lt;=</c>, <c>&gt;</c>, <c>&gt;=</c>, BETWEEN) on SQL Server uses the column collation, which
    /// can differ from DuckDB; it needs the <c>exact_compare</c> hint like a negative filter. Same rule, own name for the call sites.
    /// </summary>
    public static bool IsRangeFilterSafeForDialect(DatabaseDialect dialect, TableColumn? column) =>
        IsNegativeFilterSafeForDialect(dialect, column);

    /// <summary>
    /// SEC-DATA122-04: ANSI Three-Valued Logic safety check.
    /// NOT IN (...) containing any NULL literal or parameter evaluates to UNKNOWN (falsy) in SQL.
    /// </summary>
    public static bool HasNullInNotInList(InListExpression inList)
    {
        if (!inList.IsNotIn)
        {
            return false;
        }

        foreach (var item in inList.Items)
        {
            if (item is LiteralExpression lit && (lit.Type == LiteralType.Null || lit.Value == null))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Checks if a LIKE pattern contains dialect-specific bracket character classes ([A-Z]) on SQL Server.
    /// </summary>
    public static bool HasDialectSpecificBrackets(LikeExpression like, DatabaseDialect dialect)
    {
        if (dialect != DatabaseDialect.SqlServer)
        {
            return false;
        }

        if (like.Pattern is LiteralExpression lit && lit.Value is string patternText)
        {
            return patternText.Contains('[') || patternText.Contains(']');
        }

        return false;
    }
}
