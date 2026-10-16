namespace Autheris.Application.Olap;

using System;
using System.Collections.Concurrent;
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
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _tenants = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _perTenant;
    private readonly TimeSpan _globalWait;

    public OlapRequestLimiter(IOptions<GatewayOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var olap = options.Value.DuckDbOlap ?? new DuckDbOlapOptions();
        int global = Math.Clamp(olap.MaxConcurrentQueries > 0 ? olap.MaxConcurrentQueries : 4, 1, 64);
        _perTenant = Math.Clamp(olap.MaxConcurrentQueriesPerTenant > 0 ? olap.MaxConcurrentQueriesPerTenant : 2, 1, global);
        _globalWait = TimeSpan.FromSeconds(Math.Min(10, olap.QueryTimeoutSeconds > 0 ? olap.QueryTimeoutSeconds : 10));
        _global = new SemaphoreSlim(global, global);
    }

    public async Task<IDisposable?> TryAcquireAsync(string tenantId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        var tenant = _tenants.GetOrAdd(tenantId, _ => new SemaphoreSlim(_perTenant, _perTenant));

        if (!await tenant.WaitAsync(TimeSpan.Zero, ct).ConfigureAwait(false))
        {
            return null;
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
                tenant.Release();
            }
        }

        return globalTaken ? new Lease(_global, tenant) : null;
    }

    public void Dispose()
    {
        _global.Dispose();
        foreach (var tenant in _tenants.Values)
        {
            tenant.Dispose();
        }
    }

    private sealed class Lease(SemaphoreSlim global, SemaphoreSlim tenant) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                tenant.Release();
                global.Release();
            }
        }
    }
}
