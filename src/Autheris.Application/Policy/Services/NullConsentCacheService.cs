namespace Autheris.Application.Policy.Services;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;

public sealed class NullConsentCacheService : IConsentCacheService
{
    public static readonly NullConsentCacheService Instance = new();

    public Task<TableAccessDecision?> GetCachedDecisionAsync(TenantId tenant, Sid userSid, TableIdentifier table, string? contextHash = null, CancellationToken ct = default)
        => Task.FromResult<TableAccessDecision?>(null);

    public Task<IReadOnlyDictionary<TableIdentifier, TableAccessDecision?>> GetCachedDecisionsAsync(
        TenantId tenant,
        Sid userSid,
        IReadOnlyList<TableIdentifier> tables,
        string? contextHash = null,
        CancellationToken ct = default)
    {
        var result = new Dictionary<TableIdentifier, TableAccessDecision?>();
        foreach (var t in tables)
        {
            result[t] = null;
        }
        return Task.FromResult<IReadOnlyDictionary<TableIdentifier, TableAccessDecision?>>(result);
    }

    public Task SetCachedDecisionAsync(TenantId tenant, Sid userSid, TableIdentifier table, TableAccessDecision decision, TimeSpan ttl, string? contextHash = null, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task SetCachedDecisionAsync(TenantId tenant, Sid userSid, TableIdentifier table, TableAccessDecision decision, TimeSpan ttl, string? contextHash, long? epochAtLoad, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<long?> GetEpochSnapshotAsync(TableIdentifier table, CancellationToken ct = default)
        => Task.FromResult<long?>(null);

    public Task EvictTableDecisionsAsync(TableIdentifier table, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task ClearL1CacheAsync(CancellationToken ct = default)
        => Task.CompletedTask;
}
