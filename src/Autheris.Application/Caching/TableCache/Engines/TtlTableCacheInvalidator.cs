namespace Autheris.Application.Caching.TableCache.Engines;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Caching.TableCache;
using Microsoft.Extensions.Logging;

/// <summary>
/// Sweeps catalog for expired TTL entries and triggers eviction.
/// </summary>
public sealed class TtlTableCacheInvalidator
{
    private readonly ITableCacheManager _manager;
    private readonly ILogger<TtlTableCacheInvalidator>? _logger;

    public TtlTableCacheInvalidator(
        ITableCacheManager manager,
        ILogger<TtlTableCacheInvalidator>? logger = null)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _logger = logger;
    }

    public async Task SweepExpiredAsync(CancellationToken ct = default)
    {
        try
        {
            await _manager.EvictExpiredAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "TTL cache sweep failed.");
        }
    }
}
