namespace Autheris.Application.Olap;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;

/// <summary>
/// Admission control for OLAP requests, taken BEFORE the first source read: a global cap (<c>DuckDbOlap:MaxConcurrentQueries</c>) and a
/// per-tenant cap (<c>DuckDbOlap:MaxConcurrentQueriesPerTenant</c>), so one tenant cannot hold every slot and staged rows of
/// unbounded concurrent requests cannot pile up in gateway memory.
/// </summary>
public interface IOlapRequestLimiter
{
    /// <summary>Returns a lease that must be disposed, or <c>null</c> when the tenant or the gateway is at capacity (answer 429).</summary>
    Task<IDisposable?> TryAcquireAsync(string tenantId, CancellationToken ct);
}

/// <inheritdoc cref="IOlapRequestLimiter"/>
public sealed class OlapRequestLimiter : IOlapRequestLimiter, IDisposable
{
    private readonly SemaphoreSlim _global;
    private readonly object _gate = new();

    // Active leases per tenant; an entry is removed when its count returns to zero, so the map is bounded by the concurrent requests.
    private readonly Dictionary<string, int> _tenants = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _perTenant;
    private readonly TimeSpan _globalWait;

    public OlapRequestLimiter(IOptions<GatewayOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var olap = options.Value.DuckDbOlap ?? new DuckDbOlapOptions();
        int global = Math.Clamp(olap.MaxConcurrentQueries, 1, 64);
        _perTenant = Math.Clamp(olap.MaxConcurrentQueriesPerTenant, 1, global);
        _globalWait = TimeSpan.FromSeconds(Math.Min(10, Math.Max(1, olap.QueryTimeoutSeconds)));
        _global = new SemaphoreSlim(global, global);
    }

    /// <summary>Number of tenants that currently hold at least one lease (for tests).</summary>
    internal int TrackedTenants
    {
        get
        {
            lock (_gate)
            {
                return _tenants.Count;
            }
        }
    }

    public async Task<IDisposable?> TryAcquireAsync(string tenantId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        lock (_gate)
        {
            _tenants.TryGetValue(tenantId, out int active);
            if (active >= _perTenant)
            {
                return null;
            }

            _tenants[tenantId] = active + 1;
        }

        bool globalTaken = false;
        try
        {
            globalTaken = await _global.WaitAsync(_globalWait, ct).ConfigureAwait(false);
        }
        finally
        {
            if (!globalTaken)
            {
                ReleaseTenant(tenantId);
            }
        }

        return globalTaken ? new Lease(this, tenantId) : null;
    }

    private void ReleaseTenant(string tenantId)
    {
        lock (_gate)
        {
            if (_tenants.TryGetValue(tenantId, out int active))
            {
                if (active <= 1)
                {
                    _tenants.Remove(tenantId);
                }
                else
                {
                    _tenants[tenantId] = active - 1;
                }
            }
        }
    }

    public void Dispose() => _global.Dispose();

    private sealed class Lease(OlapRequestLimiter owner, string tenantId) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                owner.ReleaseTenant(tenantId);
                owner._global.Release();
            }
        }
    }
}
