namespace TrinoSqlEngine;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;

/// <summary>
/// SQ-06 / SEC P-02: Curated function allowlists per target dialect (scalar, string, date, math, aggregate, window and
/// conditional functions without side effects, SQL text execution, file/network access or server/session disclosure).
/// Use with <see cref="CompileRequest.AllowedFunctions"/>. The default denylist (<see cref="SqlFunctionPolicy"/>) remains
/// the second line of defense: <see cref="Build"/> never includes a denylisted name, and
/// <see cref="SqlFunctionPolicy.IsFunctionAllowed"/> rejects denylisted names even if they are allowlisted.
/// Names are unqualified and matched case-insensitively; qualified calls (e.g. <c>pg_catalog.lower</c>) are not allowlisted.
/// Syntax constructs such as CAST, EXTRACT, CASE, TRIM(... FROM ...), SUBSTRING(... FROM ...), POSITION and
/// CURRENT_DATE/CURRENT_TIMESTAMP are grammar elements and need no allowlist entry.
/// </summary>
public static class SqlFunctionAllowlists
{
    // Common (ANSI/Trino) functions that SQLite cannot execute and that no generator rule maps: advertising them made the provider
    // answer 502 SQL_PROVIDER_ERROR. Without an entry the compiler rejects the call (fail closed, 4xx) before the database sees it.
    // Declared before the allowlist properties: static initializers run in textual order.
    private static readonly string[] SqliteUnsupportedCommonFunctions =
    [
        "stddev", "stddev_pop", "stddev_samp", "variance", "var_pop", "var_samp", "greatest", "least",
        "lpad", "rpad", "reverse", "char_length", "character_length",
    ];

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
        "extract", "cast", "current_date", "current_timestamp", "convert_timezone",
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

    // SEC-X1-10: closed list. printf/format (unbounded padding width) are excluded, like every name of SqlFunctionPolicy.DefaultDeniedFunctions.
    private static readonly string[] SqliteFunctions =
    [
        "date", "time", "datetime", "julianday", "strftime", "unixepoch", "ifnull", "iif", "instr",
        "group_concat", "total", "unicode", "char", "trunc", "pi",
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

    // SQL special forms and ANSI niladic functions that the builder checks under their function name; every dialect has a rule.
    private static readonly string[] TrinoSpecialForms =
    [
        "substring", "trim", "position", "extract", "cast", "current_date", "current_timestamp", "current_time", "localtime",
        "localtimestamp",
    ];

    // Trino functions that only exist with TranslateTrinoDateFunctions (the builder turns them into date nodes); without the
    // option they are only listed where the dialect has the native function of the same name (its default allowlist).
    private static readonly string[] TrinoDateFunctions = ["date_add", "date_trunc", "now"];

    /// <summary>
    /// Trino function names that a dialect generator rewrites to a native form (CR-ADG-02: a name is listed only where the dialect
    /// has a rule, otherwise the call would reach the database unchanged and fail at execution).
    /// </summary>
    private static IEnumerable<string> RewrittenFunctions(TargetSqlDialect dialect)
    {
        if (dialect is TargetSqlDialect.PostgreSql or TargetSqlDialect.DuckDb or TargetSqlDialect.SqlServer or TargetSqlDialect.Oracle
            or TargetSqlDialect.Databricks)
        {
            yield return "strpos";
        }

        if (dialect is TargetSqlDialect.Databricks)
        {
            yield return "approx_distinct";
        }

        if (dialect is TargetSqlDialect.Databricks or TargetSqlDialect.DuckDb)
        {
            yield return "arbitrary";
        }
    }

    /// <summary>
    /// Names that have a rule in the per-dialect function map of the governed compiler (CR-ADG-02): the legacy allowlist of the
    /// dialect plus the Trino functions that the compiler rewrites for that dialect. Anything else is rejected (INV-1, plan 3.6).
    /// The date functions are included for <c>TranslateTrinoDateFunctions</c>; <see cref="WithoutDateTranslation"/> removes them again.
    /// </summary>
    internal static IReadOnlySet<string> CompilerNames(TargetSqlDialect dialect)
    {
        var names = new HashSet<string>(GetDefault(dialect), StringComparer.OrdinalIgnoreCase);
        foreach (var name in TrinoSpecialForms.Concat(TrinoDateFunctions).Concat(RewrittenFunctions(dialect)))
        {
            if (!SqlFunctionPolicy.IsDeniedByDefault(name)) names.Add(name);
        }

        return names.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The function map of a request without <c>TranslateTrinoDateFunctions</c>: <c>date_add</c>, <c>date_trunc</c> and <c>now</c>
    /// stay only where the dialect has the native function of that name, otherwise the call would reach the database unchanged.
    /// </summary>
    internal static IReadOnlySet<string> WithoutDateTranslation(TargetSqlDialect dialect, IReadOnlySet<string> names)
    {
        // G8: the result is a pure function of the (immutable) name set and the dialect, so it is computed once per name set
        // instead of building a new frozen set on every compile. It contains no request value (SEC-ADG-01).
        var entry = WithoutDateCache.GetValue(names, static _ => new WithoutDateEntry());
        var cached = entry.Value;
        if (cached is not null && cached.Dialect == dialect)
        {
            return cached.Names;
        }

        var native = GetDefault(dialect);
        var result = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        foreach (var name in TrinoDateFunctions)
        {
            if (!native.Contains(name)) result.Remove(name);
        }

        var frozen = result.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        entry.Value = new WithoutDateResult(dialect, frozen);
        return frozen;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IReadOnlySet<string>, WithoutDateEntry> WithoutDateCache = new();

    private sealed record WithoutDateResult(TargetSqlDialect Dialect, IReadOnlySet<string> Names);

    private sealed class WithoutDateEntry
    {
        // A reference write is atomic; a lost race only recomputes the same value.
        public volatile WithoutDateResult? Value;
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
    public static IReadOnlySet<string> Sqlite { get; } = Create(CommonFunctions, SqliteFunctions, except: SqliteUnsupportedCommonFunctions);

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
                if (!SqlFunctionPolicy.IsDeniedByDefault(name) && !IsTimeZoneFunctionName(name))
                {
                    result.Add(name);
                }
            }
        }

        return result.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }

    private static readonly FrozenSet<string> TimeZoneFunctionNames = new[]
    {
        "timezone", "at_timezone", "with_timezone", "switchoffset", "todatetimeoffset", "timezone_hour", "timezone_minute", "convert_timezone"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// REQ-SQL-130 (BC-130-9): true for the names of the time zone functions of the dialects and for <c>convert_timezone</c> itself, whatever the case,
    /// quoting or qualification of the entry. A function of that family that is not <c>convert_timezone</c> has no closed zone set behind it, so neither
    /// <see cref="Build"/> nor the options validation lets configuration admit one.
    /// </summary>
    public static bool IsTimeZoneFunctionName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        string last = name.Trim();
        int dot = last.LastIndexOf('.');
        if (dot >= 0) last = last[(dot + 1)..];
        last = last.Trim().Trim('"', '[', ']', '`').Trim();
        return TimeZoneFunctionNames.Contains(last);
    }

    /// <summary>
    /// The allowlist of admin-authored policy predicates (row policies, policy lint): <see cref="Build"/> without the functions that
    /// need the governed time zone validator (BC-130-2).
    /// </summary>
    public static IReadOnlySet<string> BuildForPolicy(TargetSqlDialect dialect, IEnumerable<string>? additionalAllowedFunctions)
    {
        var result = new HashSet<string>(Build(dialect, additionalAllowedFunctions), StringComparer.OrdinalIgnoreCase);
        foreach (var name in GovernedOnlyFunctions)
        {
            result.Remove(name);
        }

        return result.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Functions whose arguments are checked by a dedicated governed validator that policy predicates do not run (REQ-SQL-130).</summary>
    private static readonly string[] GovernedOnlyFunctions = ["convert_timezone"];

    private static FrozenSet<string> Create(params string[][] groups) => Create(groups, except: []);

    private static FrozenSet<string> Create(string[] first, string[] second, string[] except) => Create([first, second], except);

    private static FrozenSet<string> Create(string[][] groups, string[] except)
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

        foreach (var name in except)
        {
            result.Remove(name);
        }

        return result.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }
}
