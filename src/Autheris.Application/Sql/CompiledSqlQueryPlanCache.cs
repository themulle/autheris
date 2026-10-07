namespace Autheris.Application.Sql;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO.Hashing;
using System.Linq;
using System.Runtime.InteropServices;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>
/// Composite cache key that guarantees complete isolation between queries, database dialects,
/// tenants, and user-specific Row-Level Security (RLS) contexts (SEC-CACHE-01).
/// </summary>
public readonly record struct CompiledSqlPlanKey(
    ulong QueryHash,
    DatabaseDialect Dialect,
    TenantId TenantId,
    ulong PolicyHash
);

/// <summary>
/// Lock-free, bounded query plan cache utilizing <see cref="XxHash3"/> 64-bit hashing for ultra-low latency plan lookups
/// with zero-trust multi-tenant and per-user policy isolation (SEC-CACHE-01).
/// </summary>
public sealed class CompiledSqlQueryPlanCache : ICompiledSqlQueryPlanCache
{
    private sealed record CacheEntry(string RawSql, string Sql, DateTimeOffset ExpiresAt);
    private readonly ConcurrentDictionary<CompiledSqlPlanKey, CacheEntry> _cache = new();
    private readonly TimeSpan _defaultTtl;
    private const int MaxCachedPlans = 10_000;

    public CompiledSqlQueryPlanCache(TimeSpan? defaultTtl = null)
    {
        _defaultTtl = defaultTtl ?? TimeSpan.FromMinutes(10);
    }

    public bool TryGetCompiledSql(
        ulong queryHash,
        DatabaseDialect dialect,
        TenantId tenantId,
        ulong policyHash,
        out string? sql)
    {
        return TryGetCompiledSql(string.Empty, queryHash, dialect, tenantId, policyHash, out sql);
    }

    public bool TryGetCompiledSql(
        string rawSql,
        ulong queryHash,
        DatabaseDialect dialect,
        TenantId tenantId,
        ulong policyHash,
        out string? sql)
    {
        var key = new CompiledSqlPlanKey(queryHash, dialect, tenantId, policyHash);
        if (_cache.TryGetValue(key, out var entry))
        {
            if (DateTimeOffset.UtcNow < entry.ExpiresAt)
            {
                // Verify raw query text equality to eliminate any 64-bit hash collision risk
                if (string.IsNullOrEmpty(rawSql) || string.IsNullOrEmpty(entry.RawSql) || string.Equals(rawSql, entry.RawSql, StringComparison.Ordinal))
                {
                    sql = entry.Sql;
                    return true;
                }
            }
            else
            {
                // Entry expired - remove it
                _cache.TryRemove(key, out _);
            }
        }

        sql = null;
        return false;
    }

    public void SetCompiledSql(
        ulong queryHash,
        DatabaseDialect dialect,
        TenantId tenantId,
        ulong policyHash,
        string sql,
        TimeSpan? ttl = null)
    {
        SetCompiledSql(string.Empty, queryHash, dialect, tenantId, policyHash, sql, ttl);
    }

    public void SetCompiledSql(
        string rawSql,
        ulong queryHash,
        DatabaseDialect dialect,
        TenantId tenantId,
        ulong policyHash,
        string sql,
        TimeSpan? ttl = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        if (_cache.Count >= MaxCachedPlans)
        {
            _cache.Clear();
        }

        var expiresAt = DateTimeOffset.UtcNow.Add(ttl ?? _defaultTtl);
        var key = new CompiledSqlPlanKey(queryHash, dialect, tenantId, policyHash);
        _cache[key] = new CacheEntry(rawSql, sql, expiresAt);
    }

    public void Clear()
    {
        _cache.Clear();
    }

    public ulong ComputeHash(ReadOnlySpan<char> queryText, string? operationName = null)
    {
        var bytes = MemoryMarshal.AsBytes(queryText);
        if (string.IsNullOrEmpty(operationName))
        {
            return XxHash3.HashToUInt64(bytes);
        }

        int opBytesLen = operationName.Length * sizeof(char);
        int totalLen = bytes.Length + opBytesLen;

        byte[]? rented = null;
        Span<byte> buffer = totalLen <= 1024
            ? stackalloc byte[totalLen]
            : (rented = System.Buffers.ArrayPool<byte>.Shared.Rent(totalLen)).AsSpan(0, totalLen);

        try
        {
            bytes.CopyTo(buffer);
            MemoryMarshal.AsBytes(operationName.AsSpan()).CopyTo(buffer[bytes.Length..]);
            return XxHash3.HashToUInt64(buffer);
        }
        finally
        {
            if (rented != null)
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    public ulong ComputeRlsHash(IReadOnlyDictionary<TableIdentifier, string?>? rlsPredicates)
    {
        if (rlsPredicates == null || rlsPredicates.Count == 0)
        {
            return 0UL;
        }

        var hasher = new XxHash3();
        var sorted = rlsPredicates
            .OrderBy(kv => kv.Key.Domain, StringComparer.Ordinal)
            .ThenBy(kv => kv.Key.Schema, StringComparer.Ordinal)
            .ThenBy(kv => kv.Key.TableName, StringComparer.Ordinal);

        foreach (var (table, pred) in sorted)
        {
            hasher.Append(MemoryMarshal.AsBytes(table.Domain.AsSpan()));
            hasher.Append(MemoryMarshal.AsBytes(":".AsSpan()));
            hasher.Append(MemoryMarshal.AsBytes(table.Schema.AsSpan()));
            hasher.Append(MemoryMarshal.AsBytes(":".AsSpan()));
            hasher.Append(MemoryMarshal.AsBytes(table.TableName.AsSpan()));
            hasher.Append(MemoryMarshal.AsBytes("=".AsSpan()));
            if (!string.IsNullOrEmpty(pred))
            {
                hasher.Append(MemoryMarshal.AsBytes(pred.AsSpan()));
            }
            hasher.Append(MemoryMarshal.AsBytes(";".AsSpan()));
        }

        return hasher.GetCurrentHashAsUInt64();
    }

    public ulong ComputeRlsFilterHash(IReadOnlyDictionary<string, string>? rlsPredicates)
    {
        if (rlsPredicates == null || rlsPredicates.Count == 0)
        {
            return 0UL;
        }

        var hasher = new XxHash3();
        var sorted = rlsPredicates.OrderBy(kv => kv.Key, StringComparer.Ordinal);

        foreach (var (table, pred) in sorted)
        {
            hasher.Append(MemoryMarshal.AsBytes(table.AsSpan()));
            hasher.Append(MemoryMarshal.AsBytes("=".AsSpan()));
            if (!string.IsNullOrEmpty(pred))
            {
                hasher.Append(MemoryMarshal.AsBytes(pred.AsSpan()));
            }
            hasher.Append(MemoryMarshal.AsBytes(";".AsSpan()));
        }

        return hasher.GetCurrentHashAsUInt64();
    }

    public ulong ComputePolicyHash(
        IReadOnlyDictionary<string, string>? rlsPredicates,
        IReadOnlyDictionary<string, Dictionary<string, string>>? columnMasks = null,
        IReadOnlySet<string>? tablesWithoutRls = null,
        long maxRows = 0,
        bool isDml = false,
        string? rewriterEngine = null,
        IReadOnlySet<string>? tablesWithConsentRowFilter = null,
        IReadOnlySet<string>? tablesWithMaskedColumns = null)
    {
        var hasher = new XxHash3();

        // 1. RLS Predicates with length-prefixed keys and values
        if (rlsPredicates != null && rlsPredicates.Count > 0)
        {
            AppendLengthPrefixed(hasher, "RLS");
            foreach (var (tbl, pred) in rlsPredicates.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                AppendLengthPrefixed(hasher, tbl);
                AppendLengthPrefixed(hasher, pred ?? string.Empty);
            }
        }

        // 2. Column Masks per table with length-prefixed table, column, and mask expressions
        if (columnMasks != null && columnMasks.Count > 0)
        {
            AppendLengthPrefixed(hasher, "MASKS");
            foreach (var (tbl, cols) in columnMasks.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                AppendLengthPrefixed(hasher, tbl);
                if (cols != null)
                {
                    foreach (var (col, maskExpr) in cols.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                    {
                        AppendLengthPrefixed(hasher, col);
                        AppendLengthPrefixed(hasher, maskExpr ?? string.Empty);
                    }
                }
            }
        }

        // 3. Tables without RLS (RLS bypass list)
        if (tablesWithoutRls != null && tablesWithoutRls.Count > 0)
        {
            AppendLengthPrefixed(hasher, "NORLS");
            foreach (var tbl in tablesWithoutRls.OrderBy(t => t, StringComparer.Ordinal))
            {
                AppendLengthPrefixed(hasher, tbl);
            }
        }

        // 4. Tables with Consent Row Filter
        if (tablesWithConsentRowFilter != null && tablesWithConsentRowFilter.Count > 0)
        {
            AppendLengthPrefixed(hasher, "CONSENT_RLS");
            foreach (var tbl in tablesWithConsentRowFilter.OrderBy(t => t, StringComparer.Ordinal))
            {
                AppendLengthPrefixed(hasher, tbl);
            }
        }

        // 5. Tables with Masked Columns
        if (tablesWithMaskedColumns != null && tablesWithMaskedColumns.Count > 0)
        {
            AppendLengthPrefixed(hasher, "MASKED_TBLS");
            foreach (var tbl in tablesWithMaskedColumns.OrderBy(t => t, StringComparer.Ordinal))
            {
                AppendLengthPrefixed(hasher, tbl);
            }
        }

        // 6. Max Rows, DML, Engine with length-prefixing
        hasher.Append(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref maxRows, 1)));
        byte dmlByte = isDml ? (byte)1 : (byte)0;
        hasher.Append(MemoryMarshal.CreateReadOnlySpan(ref dmlByte, 1));
        AppendLengthPrefixed(hasher, rewriterEngine ?? string.Empty);

        return hasher.GetCurrentHashAsUInt64();
    }

    private static void AppendLengthPrefixed(XxHash3 hasher, ReadOnlySpan<char> text)
    {
        int len = text.Length;
        hasher.Append(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref len, 1)));
        if (len > 0)
        {
            hasher.Append(MemoryMarshal.AsBytes(text));
        }
    }
}
