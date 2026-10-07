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
        ulong rlsHash,
        out string? sql);

    void SetCompiledSql(
        ulong queryHash,
        DatabaseDialect dialect,
        TenantId tenantId,
        ulong rlsHash,
        string sql);

    ulong ComputeHash(ReadOnlySpan<char> queryText, string? operationName = null);

    ulong ComputeRlsHash(IReadOnlyDictionary<TableIdentifier, string?>? rlsPredicates);

    ulong ComputeRlsFilterHash(IReadOnlyDictionary<string, string>? rlsPredicates);
}
