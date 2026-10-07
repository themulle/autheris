namespace Autheris.Application.Sql;

using System;
using System.Collections.Generic;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>
/// High-performance, multi-tenant-isolated plan cache for compiled single-query AST statements.
/// Ensures strict tenant and RLS context isolation to prevent cross-tenant data leakage (SEC-CACHE-01 / F-PERF).
/// </summary>
public interface ICompiledSqlQueryPlanCache
{
    bool TryGetCompiledSql(
        ulong queryHash,
        DatabaseDialect dialect,
        TenantId tenantId,
        ulong policyHash,
        out string? sql);

    bool TryGetCompiledSql(
        string rawSql,
        ulong queryHash,
        DatabaseDialect dialect,
        TenantId tenantId,
        ulong policyHash,
        out string? sql);

    void SetCompiledSql(
        ulong queryHash,
        DatabaseDialect dialect,
        TenantId tenantId,
        ulong policyHash,
        string sql,
        TimeSpan? ttl = null);

    void SetCompiledSql(
        string rawSql,
        ulong queryHash,
        DatabaseDialect dialect,
        TenantId tenantId,
        ulong policyHash,
        string sql,
        TimeSpan? ttl = null);

    ulong ComputeHash(ReadOnlySpan<char> queryText, string? operationName = null);

    ulong ComputeRlsHash(IReadOnlyDictionary<TableIdentifier, string?>? rlsPredicates);

    ulong ComputeRlsFilterHash(IReadOnlyDictionary<string, string>? rlsPredicates);

    ulong ComputePolicyHash(
        IReadOnlyDictionary<string, string>? rlsPredicates,
        IReadOnlyDictionary<string, Dictionary<string, string>>? columnMasks = null,
        IReadOnlySet<string>? tablesWithoutRls = null,
        long maxRows = 0,
        bool isDml = false,
        string? rewriterEngine = null,
        IReadOnlySet<string>? tablesWithConsentRowFilter = null,
        IReadOnlySet<string>? tablesWithMaskedColumns = null);

    void Clear();
}
