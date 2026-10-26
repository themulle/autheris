namespace Autheris.Application.Olap;

using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// The code-held policy for client SQL on the OLAP endpoint. Every request must be exactly one <c>SELECT</c>. A cross-source request
/// is additionally limited to an explicit function allowlist, no table functions, no constructs that select or compare columns
/// without naming them, and base tables that are staged tables of the request. New functions are added by a code change, never by
/// configuration.
/// </summary>
public static class OlapSqlPolicy
{
    /// <summary>Functions a cross-source OLAP statement may call (lower case). Operators and casts are not function calls.</summary>
    public static readonly IReadOnlySet<string> AllowedFunctions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // aggregates
        "count", "count_star", "sum", "avg", "min", "max", "stddev", "stddev_samp", "stddev_pop", "variance", "var_samp", "var_pop",
        // window ranking
        "row_number", "rank", "dense_rank",
        // null handling and conditionals
        "coalesce", "ifnull", "nullif", "if", "greatest", "least",
        // numeric
        "abs", "round", "ceil", "ceiling", "floor", "sign", "sqrt", "power", "mod",
        // string
        "lower", "upper", "length", "trim", "ltrim", "rtrim", "substr", "substring", "concat", "replace", "starts_with", "ends_with",
        "contains", "left", "right", "strpos", "position",
        // date and time
        "date_trunc", "date_part", "extract", "year", "month", "day", "hour", "minute", "second", "strftime",
        // casts as emitted by some grammars
        "cast", "try_cast"
    };

    /// <summary>Function names that are never allowed on the OLAP endpoint, for any request (the engine blocklist and the dynamic-SQL functions).</summary>
    public static readonly IReadOnlySet<string> BlockedFunctions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "range", "generate_series", "repeat", "glob", "current_setting", "getenv", "getvariable",
        "columns", "query", "query_table", "json_execute_serialized_sql", "iceberg_scan", "delta_scan",
        "sqlite_scan", "sqlite_attach", "postgres_scan", "mysql_scan"
    };

    /// <summary>Every request: exactly one statement (DuckDB can only serialize a SELECT, so the text is a single query).</summary>
    public static void EnforceAll(OlapSqlInspection inspection)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        if (inspection.StatementCount != 1)
        {
            throw new OlapSqlRejectedException(OlapSqlRejectionReason.SingleSelectOnly);
        }

        foreach (var function in inspection.Functions.Concat(inspection.TableFunctions))
        {
            if (BlockedFunctions.Contains(function))
            {
                throw new OlapSqlRejectedException(OlapSqlRejectionReason.ProhibitedFunction);
            }
        }
    }

    /// <summary>
    /// Cross-source requests. <paramref name="stagedNames"/> are the names under which the request's tables are visible in DuckDB (table
    /// and domain view names, any case).
    /// </summary>
    public static void EnforceCrossSource(OlapSqlInspection inspection, IReadOnlyCollection<string> stagedNames, bool allowNonEquiJoins)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        ArgumentNullException.ThrowIfNull(stagedNames);
        EnforceAll(inspection);

        if (inspection.TableFunctions.Count > 0)
        {
            throw Construct();
        }

        foreach (var construct in inspection.Constructs)
        {
            bool joinShape = construct is OlapSqlInspector.ConstructCrossJoin or OlapSqlInspector.ConstructNonEquiJoin;
            if (joinShape && allowNonEquiJoins)
            {
                continue;
            }

            throw Construct();
        }

        foreach (var function in inspection.Functions)
        {
            if (!AllowedFunctions.Contains(function))
            {
                throw Construct();
            }
        }

        var visible = new HashSet<string>(stagedNames, StringComparer.OrdinalIgnoreCase);

        // A CTE named like a staged table shadows it for part of the statement; reject instead of reasoning about scopes.
        if (inspection.CteNames.Any(visible.Contains))
        {
            throw Construct();
        }

        foreach (var table in inspection.BaseTables)
        {
            var name = table.StartsWith("main.", StringComparison.OrdinalIgnoreCase) ? table["main.".Length..] : table;
            if (!visible.Contains(name))
            {
                throw Construct();
            }
        }
    }

    private static OlapSqlRejectedException Construct() =>
        new(OlapSqlRejectionReason.ConstructNotAllowed);
}
