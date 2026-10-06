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
    ulong RlsHash
);

/// <summary>
/// Lock-free, bounded query plan cache utilizing <see cref="XxHash3"/> 64-bit hashing for ultra-low latency plan lookups
/// with zero-trust multi-tenant isolation.
/// </summary>
public sealed class CompiledSqlQueryPlanCache : ICompiledSqlQueryPlanCache
{
    private readonly ConcurrentDictionary<CompiledSqlPlanKey, string> _cache = new();
    private const int MaxCachedPlans = 10_000;

    public bool TryGetCompiledSql(
        ulong queryHash,
        DatabaseDialect dialect,
        TenantId tenantId,
        ulong rlsHash,
        out string? sql)
    {
        var key = new CompiledSqlPlanKey(queryHash, dialect, tenantId, rlsHash);
        return _cache.TryGetValue(key, out sql);
    }

    public void SetCompiledSql(
        ulong queryHash,
        DatabaseDialect dialect,
        TenantId tenantId,
        ulong rlsHash,
        string sql)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        if (_cache.Count >= MaxCachedPlans)
        {
            _cache.Clear();
        }

        var key = new CompiledSqlPlanKey(queryHash, dialect, tenantId, rlsHash);
        _cache[key] = sql;
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
}
