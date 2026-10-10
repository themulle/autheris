namespace TrinoSqlEngine;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;

/// <summary>
/// SQ-06 / SEC P-02: Curated function allowlists per target dialect (scalar, string, date, math, aggregate, window and
/// conditional functions without side effects, SQL text execution, file/network access or server/session disclosure).
/// Use with <see cref="RlsOptions.AllowedFunctions"/>. The default denylist (<see cref="SqlFunctionPolicy"/>) remains
/// the second line of defense: <see cref="Build"/> never includes a denylisted name, and
/// <see cref="SqlFunctionPolicy.IsFunctionAllowed"/> rejects denylisted names even if they are allowlisted.
/// Names are unqualified and matched case-insensitively; qualified calls (e.g. <c>pg_catalog.lower</c>) are not allowlisted.
/// Syntax constructs such as CAST, EXTRACT, CASE, TRIM(... FROM ...), SUBSTRING(... FROM ...), POSITION and
/// CURRENT_DATE/CURRENT_TIMESTAMP are grammar elements and need no allowlist entry.
/// </summary>
public static class SqlFunctionAllowlists
{
    private static readonly string[] CommonFunctions =
    [
        // Aggregates
        "count", "sum", "avg", "min", "max", "stddev", "stddev_pop", "stddev_samp", "variance", "var_pop", "var_samp",
        // Window functions
        "row_number", "rank", "dense_rank", "percent_rank", "cume_dist", "ntile", "lag", "lead",
        "first_value", "last_value", "nth_value",
        // Conditional
        "coalesce", "nullif", "greatest", "least",
        // String
        "lower", "upper", "trim", "ltrim", "rtrim", "substring", "substr", "concat", "length", "char_length",
        "character_length", "octet_length", "replace", "position", "lpad", "rpad", "reverse", "left", "right",
        // Math
        "abs", "round", "floor", "ceil", "ceiling", "mod", "power", "sqrt", "exp", "ln", "log", "log10", "sign",
        // Date / time and conversion helpers
        "extract", "cast", "current_date", "current_timestamp",
    ];

    private static readonly string[] PostgreSqlFunctions =
    [
        "now", "date_trunc", "date_part", "date_bin", "age", "make_date", "make_time", "make_timestamp",
        "make_interval", "justify_days", "justify_hours", "justify_interval", "to_char", "to_date", "to_timestamp",
        "to_number", "localtimestamp", "string_agg", "array_agg", "bool_and", "bool_or", "every",
        "percentile_cont", "percentile_disc", "mode", "initcap", "split_part", "strpos", "btrim", "concat_ws",
        "translate", "starts_with", "regexp_replace", "bit_length", "trunc", "div", "cbrt", "pi", "degrees",
        "radians", "width_bucket",
    ];

    private static readonly string[] SqlServerFunctions =
    [
        "getdate", "getutcdate", "sysdatetime", "sysutcdatetime", "sysdatetimeoffset", "dateadd", "datediff",
        "datediff_big", "datepart", "datename", "datefromparts", "datetimefromparts", "eomonth", "year", "month",
        "day", "isnull", "iif", "choose", "len", "datalength", "charindex", "patindex", "stuff", "format",
        "string_agg", "concat_ws", "replicate", "str", "try_cast", "try_convert", "convert", "isnumeric",
        "square", "pi", "degrees", "radians", "count_big", "stdev", "stdevp", "var", "varp", "translate",
    ];

    private static readonly string[] SqliteFunctions =
    [
        "date", "time", "datetime", "julianday", "strftime", "unixepoch", "ifnull", "iif", "instr", "printf",
        "format", "group_concat", "total", "unicode", "char", "trunc", "pi",
    ];

    private static readonly string[] OracleFunctions =
    [
        "sysdate", "systimestamp", "nvl", "nvl2", "decode", "trunc", "to_char", "to_date", "to_timestamp",
        "to_number", "add_months", "months_between", "last_day", "next_day", "initcap", "instr",
        "listagg", "median", "translate", "dump", "vsize", "nanvl",
    ];

    private static readonly string[] DatabricksFunctions =
    [
        "now", "date_trunc", "date_add", "date_sub", "datediff", "dateadd", "date_format", "to_date", "to_timestamp", "trunc",
        "year", "month", "day", "dayofmonth", "dayofweek", "dayofyear", "hour", "minute", "second", "quarter", "weekofyear",
        "last_day", "add_months", "months_between", "make_date", "ifnull", "nvl", "nvl2", "instr", "locate", "concat_ws",
        "initcap", "translate", "regexp_replace", "startswith", "endswith", "contains", "approx_count_distinct", "any_value",
        "bool_and", "bool_or", "every", "string_agg", "percentile_cont", "percentile_disc", "median", "try_cast", "pi",
        "localtimestamp", "current_timestamp", "current_date",
    ];

    // Trino function names the AST builder or a dialect generator rewrites (the rewrite is the dialect's rule, see
    // SqlDialectGeneratorBase subclasses), plus the SQL special forms that the builder checks under their function name.
    private static readonly string[] TrinoMappedFunctions =
    [
        "strpos", "approx_distinct", "arbitrary", "date_add", "date_trunc", "now", "current_time", "localtime", "localtimestamp",
        "substring", "trim", "position", "extract", "cast", "current_date", "current_timestamp",
    ];

    /// <summary>
    /// Names that have a rule in the per-dialect function map of the governed compiler (CR-ADG-02): the legacy allowlist of the
    /// dialect plus the Trino functions that the compiler rewrites. Anything else is rejected (INV-1, plan 3.6).
    /// </summary>
    internal static IReadOnlySet<string> CompilerNames(TargetSqlDialect dialect)
    {
        var names = new HashSet<string>(GetDefault(dialect), StringComparer.OrdinalIgnoreCase);
        foreach (var name in TrinoMappedFunctions)
        {
            if (!SqlFunctionPolicy.IsDeniedByDefault(name)) names.Add(name);
        }

        return names.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Databricks allowlist (ANSI + the reviewed safe Spark SQL functions; no reflection, secrets or UDF calls).</summary>
    public static IReadOnlySet<string> Databricks { get; } = Create(CommonFunctions, DatabricksFunctions);

    /// <summary>Dialect-neutral (ANSI) allowlist.</summary>
    public static IReadOnlySet<string> Ansi { get; } = Create(CommonFunctions);

    /// <summary>PostgreSQL allowlist (ANSI + PostgreSQL date/string/aggregate functions).</summary>
    public static IReadOnlySet<string> PostgreSql { get; } = Create(CommonFunctions, PostgreSqlFunctions);

    /// <summary>SQL Server allowlist (ANSI + T-SQL date/string/conversion functions).</summary>
    public static IReadOnlySet<string> SqlServer { get; } = Create(CommonFunctions, SqlServerFunctions);

    /// <summary>SQLite allowlist (ANSI + SQLite date/string/aggregate functions).</summary>
    public static IReadOnlySet<string> Sqlite { get; } = Create(CommonFunctions, SqliteFunctions);

    /// <summary>Oracle allowlist (ANSI + Oracle date/string/null-handling functions).</summary>
    public static IReadOnlySet<string> Oracle { get; } = Create(CommonFunctions, OracleFunctions);

    /// <summary>DuckDB allowlist (ANSI + safe analytical/date/string functions).</summary>
    public static IReadOnlySet<string> DuckDb { get; } = Create(CommonFunctions, PostgreSqlFunctions);

    /// <summary>Returns the curated default allowlist for <paramref name="dialect"/>.</summary>
    public static IReadOnlySet<string> GetDefault(TargetSqlDialect dialect) => dialect switch
    {
        TargetSqlDialect.PostgreSql => PostgreSql,
        TargetSqlDialect.SqlServer => SqlServer,
        TargetSqlDialect.Sqlite => Sqlite,
        TargetSqlDialect.Oracle => Oracle,
        TargetSqlDialect.DuckDb => DuckDb,
        TargetSqlDialect.Databricks => Databricks,
        _ => Ansi
    };

    /// <summary>
    /// Builds the effective allowlist: the dialect default plus <paramref name="additionalAllowedFunctions"/>.
    /// Additional entries that are on the default denylist (<see cref="SqlFunctionPolicy.IsDeniedByDefault"/>) are
    /// never included.
    /// </summary>
    public static IReadOnlySet<string> Build(TargetSqlDialect dialect, IEnumerable<string>? additionalAllowedFunctions)
    {
        var result = new HashSet<string>(GetDefault(dialect), StringComparer.OrdinalIgnoreCase);
        if (additionalAllowedFunctions != null)
        {
            foreach (var entry in additionalAllowedFunctions)
            {
                if (string.IsNullOrWhiteSpace(entry))
                {
                    continue;
                }

                string name = entry.Trim().ToLowerInvariant();
                if (!SqlFunctionPolicy.IsDeniedByDefault(name))
                {
                    result.Add(name);
                }
            }
        }

        return result.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }

    private static FrozenSet<string> Create(params string[][] groups)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            foreach (var name in group)
            {
                // Defensive: a curated entry that is (or becomes) denylisted is never allowlisted.
                if (!SqlFunctionPolicy.IsDeniedByDefault(name))
                {
                    result.Add(name);
                }
            }
        }

        return result.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }
}
