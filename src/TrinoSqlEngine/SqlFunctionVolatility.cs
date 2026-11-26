namespace TrinoSqlEngine;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;

/// <summary>
/// Classifies SQL function names as safe or unsafe for result caching. A function is cache safe only when it is deterministic within the
/// statement's inputs and known to the governed compiler of the dialect; every unknown name counts as volatile (fail closed).
/// </summary>
public static class SqlFunctionVolatility
{
    private static readonly FrozenSet<string> VolatileFunctions = new[]
    {
        "now", "current_timestamp", "current_date", "current_time", "localtime", "localtimestamp", "getdate", "getutcdate",
        "sysdatetime", "sysutcdatetime", "sysdatetimeoffset", "sysdate", "systimestamp", "unix_timestamp", "current_timezone",
        "rand", "random", "uuid", "newid", "gen_random_uuid", "current_user", "session_user", "user", "current_role",
        "current_catalog", "current_schema", "current_database", "current_metastore", "connection_id", "pg_backend_pid",
        "txid_current", "clock_timestamp", "statement_timestamp", "transaction_timestamp", "timeofday", "sessiontimezone",
        "dbtimezone", "current_setting", "uid", "sys_guid",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> VolatileLookup =
        VolatileFunctions.GetAlternateLookup<ReadOnlySpan<char>>();

    /// <summary>
    /// True when <paramref name="functionName"/> is neither volatile nor unknown to the governed compiler of <paramref name="dialect"/>.
    /// Names added through <c>AdditionalAllowedFunctions</c> are unknown here and therefore volatile.
    /// </summary>
    public static bool IsCacheSafe(string? functionName, TargetSqlDialect dialect)
    {
        if (string.IsNullOrWhiteSpace(functionName))
        {
            return false;
        }

        var name = functionName.Trim();
        if (VolatileFunctions.Contains(name))
        {
            return false;
        }

        return SqlFunctionAllowlists.CompilerNames(dialect).Contains(name);
    }

    /// <summary>
    /// Whole-word, case-insensitive scan of compiled SQL for a volatile function name. It catches functions that policy injection added
    /// to the statement. A match inside a quoted identifier is a false positive, which only costs a cache miss.
    /// </summary>
    public static bool ContainsVolatileToken(string? compiledSql)
    {
        if (string.IsNullOrEmpty(compiledSql))
        {
            return false;
        }

        ReadOnlySpan<char> sql = compiledSql;
        var i = 0;
        while (i < sql.Length)
        {
            if (!IsWordChar(sql[i]))
            {
                i++;
                continue;
            }

            var start = i;
            while (i < sql.Length && IsWordChar(sql[i]))
            {
                i++;
            }

            if (VolatileLookup.Contains(sql[start..i]))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '$' or '#';
}
