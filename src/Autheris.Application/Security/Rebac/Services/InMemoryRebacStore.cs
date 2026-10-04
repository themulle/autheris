namespace Autheris.Application.Security.Rebac.Services;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;

/// <summary>
/// F-SEC-04: In-Memory Multi-Tenant Relationship Tuple Store.
/// Provides sub-millisecond tuple querying and strict tenant boundary isolation.
/// </summary>
public sealed class InMemoryRebacStore : IRebacStore
{
    private readonly ILogger<InMemoryRebacStore> _logger;
    // Partitioned by tenantId -> Set of RebacTuples
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, RebacTuple>> _tenantStores = new(StringComparer.OrdinalIgnoreCase);

    public InMemoryRebacStore(ILogger<InMemoryRebacStore> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public ValueTask AddTupleAsync(RebacTuple tuple, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tuple);
        var tenant = NormalizeTenant(tuple.TenantId);
        var store = _tenantStores.GetOrAdd(tenant, _ => new ConcurrentDictionary<string, RebacTuple>(StringComparer.OrdinalIgnoreCase));

        var key = BuildKey(tuple.User, tuple.Relation, tuple.Object);
        store[key] = tuple;

        _logger.LogDebug("F-SEC-04 ReBAC added tuple {Tuple}", tuple);
        return ValueTask.CompletedTask;
    }

    public async ValueTask AddTuplesAsync(IEnumerable<RebacTuple> tuples, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tuples);
        foreach (var t in tuples)
        {
            if (ct.IsCancellationRequested) break;
            await AddTupleAsync(t, ct).ConfigureAwait(false);
        }
    }

    public ValueTask<bool> DeleteTupleAsync(RebacTuple tuple, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tuple);
        var tenant = NormalizeTenant(tuple.TenantId);
        if (_tenantStores.TryGetValue(tenant, out var store))
        {
            var key = BuildKey(tuple.User, tuple.Relation, tuple.Object);
            var removed = store.TryRemove(key, out _);
            if (removed)
            {
                _logger.LogDebug("F-SEC-04 ReBAC removed tuple {Tuple}", tuple);
            }
            return ValueTask.FromResult(removed);
        }

        return ValueTask.FromResult(false);
    }

    public ValueTask<IReadOnlyList<RebacTuple>> GetTuplesAsync(
        string tenantId,
        string? user = null,
        string? relation = null,
        string? obj = null,
        CancellationToken ct = default)
    {
        var tenant = NormalizeTenant(tenantId);
        if (!_tenantStores.TryGetValue(tenant, out var store))
        {
            return ValueTask.FromResult<IReadOnlyList<RebacTuple>>([]);
        }

        IEnumerable<RebacTuple> query = store.Values;

        if (!string.IsNullOrWhiteSpace(user))
        {
            query = query.Where(t => string.Equals(t.User, user, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(relation))
        {
            query = query.Where(t => string.Equals(t.Relation, relation, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(obj))
        {
            query = query.Where(t => string.Equals(t.Object, obj, StringComparison.OrdinalIgnoreCase));
        }

        return ValueTask.FromResult<IReadOnlyList<RebacTuple>>(query.ToList());
    }

    public ValueTask ClearTenantTuplesAsync(string tenantId, CancellationToken ct = default)
    {
        var tenant = NormalizeTenant(tenantId);
        _tenantStores.TryRemove(tenant, out _);
        _logger.LogInformation("F-SEC-04 ReBAC cleared all tuples for tenant '{Tenant}'", tenant);
        return ValueTask.CompletedTask;
    }

    private static string NormalizeTenant(string? tenantId) =>
        string.IsNullOrWhiteSpace(tenantId) ? "default" : tenantId.Trim();

    private static string BuildKey(string user, string relation, string obj) =>
        $"{user}#{relation}@{obj}";
}
