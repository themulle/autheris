namespace Autheris.Application.Sql;

using System;
using System.Collections.Generic;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

public sealed class NullCompiledSqlQueryPlanCache : ICompiledSqlQueryPlanCache
{
    public static readonly NullCompiledSqlQueryPlanCache Instance = new();

    public bool TryGetCompiledSql(ulong queryHash, DatabaseDialect dialect, TenantId tenantId, ulong policyHash, out string? sql)
    {
        sql = null;
        return false;
    }

    public bool TryGetCompiledSql(string rawSql, ulong queryHash, DatabaseDialect dialect, TenantId tenantId, ulong policyHash, out string? sql)
    {
        sql = null;
        return false;
    }

    public bool TryGetCompiledSql(string rawSql, ulong queryHash, DatabaseDialect dialect, TenantId tenantId, ulong policyHash, string? policyFingerprint, string? dataSource, out string? sql)
    {
        sql = null;
        return false;
    }

    public void SetCompiledSql(ulong queryHash, DatabaseDialect dialect, TenantId tenantId, ulong policyHash, string sql, TimeSpan? ttl = null)
    {
    }

    public void SetCompiledSql(string rawSql, ulong queryHash, DatabaseDialect dialect, TenantId tenantId, ulong policyHash, string sql, TimeSpan? ttl = null)
    {
    }

    public void SetCompiledSql(string rawSql, ulong queryHash, DatabaseDialect dialect, TenantId tenantId, ulong policyHash, string? policyFingerprint, string? dataSource, string sql, TimeSpan? ttl = null)
    {
    }

    public ulong ComputeHash(ReadOnlySpan<char> queryText, string? operationName = null) => 0UL;

    public ulong ComputeRlsHash(IReadOnlyDictionary<TableIdentifier, string?>? rlsPredicates) => 0UL;

    public ulong ComputeRlsFilterHash(IReadOnlyDictionary<string, string>? rlsPredicates) => 0UL;

    public ulong ComputePolicyHash(
        IReadOnlyDictionary<string, string>? rlsPredicates,
        IReadOnlyDictionary<string, Dictionary<string, string>>? columnMasks = null,
        IReadOnlySet<string>? tablesWithoutRls = null,
        long maxRows = 0,
        bool isDml = false,
        string? rewriterEngine = null,
        IReadOnlySet<string>? tablesWithConsentRowFilter = null,
        IReadOnlySet<string>? tablesWithMaskedColumns = null,
        RowFilterSubqueryStrategy subqueryStrategy = RowFilterSubqueryStrategy.Exists,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? catalogColumnsMap = null,
        IReadOnlySet<string>? allowedFunctions = null) => 0UL;

    public void Clear()
    {
    }
}
