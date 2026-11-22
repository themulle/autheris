using System.Collections.Concurrent;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;

namespace Autheris.Tests.Common.Fakes;

/// <summary>
/// In-memory <see cref="IConsentCacheService"/> for tests. Starts empty (cache miss) and stores cached decisions
/// until evicted or cleared.
/// </summary>
public sealed class InMemoryConsentCacheService : IConsentCacheService
{
    private readonly ConcurrentDictionary<(string Tenant, string User, TableIdentifier Table, string ContextHash), TableAccessDecision> _entries = new();

    public int Count => _entries.Count;

    public Task<TableAccessDecision?> GetCachedDecisionAsync(
        TenantId tenant,
        Sid userSid,
        TableIdentifier table,
        string? contextHash = null,
        CancellationToken ct = default)
        => Task.FromResult(_entries.TryGetValue(Key(tenant, userSid, table, contextHash), out var decision) ? decision : null);

    public Task<IReadOnlyDictionary<TableIdentifier, TableAccessDecision?>> GetCachedDecisionsAsync(
        TenantId tenant,
        Sid userSid,
        IReadOnlyList<TableIdentifier> tables,
        string? contextHash = null,
        CancellationToken ct = default)
    {
        var result = new Dictionary<TableIdentifier, TableAccessDecision?>(tables.Count);
        foreach (var table in tables)
        {
            result[table] = _entries.TryGetValue(Key(tenant, userSid, table, contextHash), out var decision) ? decision : null;
        }

        return Task.FromResult<IReadOnlyDictionary<TableIdentifier, TableAccessDecision?>>(result);
    }

    public Task SetCachedDecisionAsync(
        TenantId tenant,
        Sid userSid,
        TableIdentifier table,
        TableAccessDecision decision,
        TimeSpan ttl,
        string? contextHash = null,
        CancellationToken ct = default)
    {
        _entries[Key(tenant, userSid, table, contextHash)] = decision;
        return Task.CompletedTask;
    }

    public Task ClearL1CacheAsync(CancellationToken ct = default)
    {
        _entries.Clear();
        return Task.CompletedTask;
    }

    private static (string Tenant, string User, TableIdentifier Table, string ContextHash) Key(
        TenantId tenant,
        Sid userSid,
        TableIdentifier table,
        string? contextHash)
        => (tenant.Value, userSid.Value, table, contextHash ?? string.Empty);
}
