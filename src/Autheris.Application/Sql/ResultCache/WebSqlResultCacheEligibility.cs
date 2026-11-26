namespace Autheris.Application.Sql.ResultCache;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using TrinoSqlEngine;

/// <summary>
/// The facts the eligibility decision needs, taken from the rewritten statement, the connection options and the caller.
/// <paramref name="FunctionCalls"/> null means the function list is unknown, which counts as volatile.
/// <paramref name="AllowedTables"/> empty or null means all tables of the source.
/// </summary>
public readonly record struct WebSqlResultCacheEligibilityInput(
    int GlobalMaxMegabytes,
    int SourceTtlSeconds,
    EnforcementMode EnforcementMode,
    bool UsesPerUserCredentials,
    bool HasConnection,
    bool IsDml,
    bool IsCrossSource,
    bool IsSelectStatement,
    bool HasRecursiveCte,
    bool HasPageOffsetParameter,
    string? CompiledSql,
    TargetSqlDialect Dialect,
    IReadOnlyList<string>? FunctionCalls,
    IReadOnlyList<TableIdentifier>? AccessedTables,
    IReadOnlyList<string>? AllowedTables,
    string? UserSid,
    string? PrincipalKey);

/// <summary>Pure decision whether a rewritten statement may use the WebSQL result cache. Every uncertainty makes the statement ineligible.</summary>
public static class WebSqlResultCacheEligibility
{
    private static readonly FrozenSet<string> UnsupportedClauseTokens = new[]
    {
        "into", "tablesample", "update", "share", "lock", "locked", "updlock", "holdlock", "xlock", "rowlock", "tablock",
        "readpast", "nowait",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> UnsupportedClauseLookup =
        UnsupportedClauseTokens.GetAlternateLookup<ReadOnlySpan<char>>();

    /// <summary>Returns <c>null</c> when the statement is eligible, otherwise a fixed code of <see cref="WebSqlResultCacheReasons"/>.</summary>
    public static string? Evaluate(in WebSqlResultCacheEligibilityInput input)
    {
        try
        {
            return EvaluateCore(in input);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return WebSqlResultCacheReasons.CallerNotBindable;
        }
    }

    private static string? EvaluateCore(in WebSqlResultCacheEligibilityInput input)
    {
        if (input.GlobalMaxMegabytes <= 0 || input.SourceTtlSeconds <= 0)
        {
            return WebSqlResultCacheReasons.SourceDisabled;
        }

        if (input.UsesPerUserCredentials)
        {
            return WebSqlResultCacheReasons.PerUserCredentials;
        }

        if (input.EnforcementMode != EnforcementMode.AutherisEnforced)
        {
            return WebSqlResultCacheReasons.DelegatedSource;
        }

        if (!input.HasConnection)
        {
            return WebSqlResultCacheReasons.NoConnection;
        }

        if (input.IsCrossSource)
        {
            return WebSqlResultCacheReasons.CrossSource;
        }

        if (input.IsDml || !input.IsSelectStatement)
        {
            return WebSqlResultCacheReasons.NotReadOnly;
        }

        if (input.HasRecursiveCte)
        {
            return WebSqlResultCacheReasons.RecursiveCte;
        }

        if (input.HasPageOffsetParameter)
        {
            return WebSqlResultCacheReasons.PagingUnavailable;
        }

        if (string.IsNullOrWhiteSpace(input.CompiledSql) || ContainsUnsupportedClause(input.CompiledSql))
        {
            return WebSqlResultCacheReasons.UnsupportedClause;
        }

        if (string.IsNullOrWhiteSpace(input.UserSid) || string.IsNullOrEmpty(input.PrincipalKey))
        {
            return WebSqlResultCacheReasons.CallerNotBindable;
        }

        if (input.FunctionCalls is null || SqlFunctionVolatility.ContainsVolatileToken(input.CompiledSql))
        {
            return WebSqlResultCacheReasons.VolatileFunction;
        }

        foreach (var function in input.FunctionCalls)
        {
            if (!SqlFunctionVolatility.IsCacheSafe(function, input.Dialect))
            {
                return WebSqlResultCacheReasons.VolatileFunction;
            }
        }

        if (input.AllowedTables is { Count: > 0 } allowed)
        {
            if (input.AccessedTables is null)
            {
                return WebSqlResultCacheReasons.TableNotListed;
            }

            var set = new HashSet<string>(allowed.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var entry in allowed)
            {
                if (!string.IsNullOrWhiteSpace(entry))
                {
                    set.Add(entry.Trim());
                }
            }

            foreach (var table in input.AccessedTables)
            {
                if (!set.Contains(table.ToQualifiedName()))
                {
                    return WebSqlResultCacheReasons.TableNotListed;
                }
            }
        }

        return null;
    }

    private static bool ContainsUnsupportedClause(string compiledSql)
    {
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

            if (UnsupportedClauseLookup.Contains(sql[start..i]))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '$' or '#';
}
