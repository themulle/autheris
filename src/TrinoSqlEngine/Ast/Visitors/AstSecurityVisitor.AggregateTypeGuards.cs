namespace TrinoSqlEngine.Ast.Visitors;

using System;
using System.Collections.Generic;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;

/// <summary>The value class of a catalog data type as far as aggregate functions are concerned (mirrors the catalog value kinds).</summary>
public enum AggregateTypeClass
{
    /// <summary>The type is not recognised (or absent): fail closed, every aggregate over it is rejected.</summary>
    Unknown,
    Numeric,
    Text,
    Temporal,
    Boolean
}

/// <summary>
/// Classification of a provider-native data type name into an <see cref="AggregateTypeClass"/>. The classes follow the catalog value
/// kinds of the GraphQL aggregation matrix (<c>AggregateResultMapper.KindFor</c>): SUM and AVG accept numeric types, MIN and MAX accept
/// numeric, text and temporal types, a boolean accepts neither. A consistency test pins both sides.
/// </summary>
public static class AggregateTypeClasses
{
    public static AggregateTypeClass Classify(string? dataType)
    {
        if (string.IsNullOrWhiteSpace(dataType))
        {
            return AggregateTypeClass.Unknown;
        }

        var dt = dataType.Trim().ToLowerInvariant();

        // Spatial and complex types are text-like (min/max over them is the database's business, sum/avg are rejected).
        if (dt.Contains("interval", StringComparison.Ordinal) || dt.Contains("point", StringComparison.Ordinal) ||
            dt.Contains("polygon", StringComparison.Ordinal) || dt.Contains("geometry", StringComparison.Ordinal) ||
            dt.Contains("geography", StringComparison.Ordinal) || dt.Contains("line", StringComparison.Ordinal) ||
            dt.Contains("json", StringComparison.Ordinal) || dt.Contains("xml", StringComparison.Ordinal) ||
            dt.Contains("uuid", StringComparison.Ordinal) || dt.Contains("guid", StringComparison.Ordinal))
        {
            return AggregateTypeClass.Text;
        }

        var baseType = dt.Split('(', '[', ' ')[0].Trim();

        if (baseType.StartsWith("bit", StringComparison.Ordinal) || baseType.StartsWith("bool", StringComparison.Ordinal))
        {
            return AggregateTypeClass.Boolean;
        }

        if (baseType is "bigint" or "int8" or "long" or "int" or "integer" or "int4" or "smallint" or "int2" or "tinyint" or
            "decimal" or "numeric" or "money" or "smallmoney" or "float" or "double" or "real" or "float4" or "float8" or
            // Spellings the catalog value kinds do not know (they fall back to string there) but that are numeric on their dialect:
            // Oracle NUMBER and BINARY_*, serial types, MySQL-style and DuckDB integer names.
            "number" or "dec" or "binary_double" or "binary_float" or "serial" or "bigserial" or "smallserial" or "mediumint" or
            "int1" or "int16" or "int32" or "int64" or "hugeint" or "ubigint" or "uinteger" or "usmallint" or "utinyint")
        {
            return AggregateTypeClass.Numeric;
        }

        if (dt.Contains("date", StringComparison.Ordinal) || dt.Contains("time", StringComparison.Ordinal))
        {
            return AggregateTypeClass.Temporal;
        }

        if (baseType is "char" or "nchar" or "varchar" or "nvarchar" or "character" or "text" or "ntext" or "string" or "clob" or
            "nclob" or "citext" or "varchar2" or "nvarchar2" or "bpchar" or "name" or "tinytext" or "mediumtext" or "longtext")
        {
            return AggregateTypeClass.Text;
        }

        return AggregateTypeClass.Unknown;
    }

    /// <summary>True when <paramref name="function"/> (lower-case sum, avg, min, max) may run over a column of <paramref name="type"/>.</summary>
    public static bool IsValid(string function, AggregateTypeClass type) => function switch
    {
        "sum" or "avg" => type == AggregateTypeClass.Numeric,
        "min" or "max" => type is AggregateTypeClass.Numeric or AggregateTypeClass.Text or AggregateTypeClass.Temporal,
        _ => true
    };
}

/// <summary>
/// #310 (SEC-PG-09): an aggregate function was applied to a column of a type it does not accept. The message is the fixed text of the
/// code: it names neither the function arguments nor the column type.
/// </summary>
public sealed class AggregateTypeInvalidException()
    : Exception(GovernedSqlErrorCodes.FixedMessage(GovernedSqlErrorCodes.AggregationInvalidFunctionForType))
{
    public string Code => GovernedSqlErrorCodes.AggregationInvalidFunctionForType;
}

public sealed partial class AstSecurityVisitor
{
    /// <summary>
    /// SUM/AVG over a non-numeric and MIN/MAX over a boolean (or unrecognised) column are rejected. Runs after the mask guards, and only
    /// for a direct column argument that the catalog lists for a FROM table: an unknown or denied column is not in the catalog, so its
    /// type is never consulted and the executor answers it with the denied code. A computed argument passes (its columns are checked
    /// by the other guards, and a type error in an expression is the database's).
    /// </summary>
    private void EnsureAggregateTypesValid(QuerySpecification node)
    {
        var lookup = _options.ColumnDataType;
        if (lookup is null || node.From is null || _specTables.Count == 0)
        {
            return;
        }

        var stack = _projectionGuardStack ??= new Stack<SqlNode>(64);
        stack.Clear();
        PushAll(node.Projections, stack);
        if (node.Having != null) stack.Push(node.Having);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current is SelectStatement) continue;
            if (current is FunctionCallExpression fn && fn.Arguments.Count == 1 && fn.Arguments[0] is ColumnReference column)
            {
                string name = fn.Name.SimpleName;
                if (name.Equals("sum", StringComparison.OrdinalIgnoreCase) || name.Equals("avg", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("min", StringComparison.OrdinalIgnoreCase) || name.Equals("max", StringComparison.OrdinalIgnoreCase))
                {
                    CheckAggregateArgument(name.ToLowerInvariant(), column, lookup);
                }

                continue;
            }

            PushChildren(current, stack);
        }
    }

    private void CheckAggregateArgument(string function, ColumnReference column, Func<string, string, string?> lookup)
    {
        string columnName = column.Name.SimpleName;
        bool qualified = column.Name.Parts.Count > 1;
        string? qualifier = qualified ? column.Name.Parts[^2].Value : null;
        foreach (var (table, alias) in _specTables)
        {
            if (qualified && !IsWholeRowName(qualifier!, table, alias)) continue;
            IReadOnlyList<string>? columns;
            try
            {
                columns = CachedColumns(table);
            }
            catch (Exception)
            {
                // A failing lookup is uncertainty: fail toward the guard. A null list is a source the catalog does not list
                // (a CTE name, an unknown table): there is no type to check, the executor answers an unknown table.
                throw new AggregateTypeInvalidException();
            }

            if (columns is null || !ContainsIgnoreCase(columns, columnName)) continue;

            // The column is listed: its type is the catalog's. A missing type is unknown, which fails closed.
            if (!AggregateTypeClasses.IsValid(function, AggregateTypeClasses.Classify(lookup(table, columnName))))
            {
                throw new AggregateTypeInvalidException();
            }
        }
    }

    private static bool ContainsIgnoreCase(IReadOnlyList<string> columns, string name)
    {
        for (int i = 0; i < columns.Count; i++)
        {
            if (columns[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }
}
