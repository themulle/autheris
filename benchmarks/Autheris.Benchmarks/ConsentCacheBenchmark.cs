using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Infrastructure.Cache;
using Autheris.Infrastructure.Messaging;
using Microsoft.Extensions.Caching.Memory;

namespace Autheris.Benchmarks;

public class FastEpochValidationService : IEpochValidationService
{
    public Task<bool> IsEpochValidAsync(TableIdentifier table, long cachedEpoch, CancellationToken ct = default) => Task.FromResult(true);
    public Task InvalidateEpochAsync(TableIdentifier table, CancellationToken ct = default) => Task.CompletedTask;
    public Task<long> GetCurrentEpochAsync(TableIdentifier table, CancellationToken ct = default) => Task.FromResult(1L);
    public Task<IReadOnlyDictionary<TableIdentifier, long>> GetCurrentEpochsAsync(IEnumerable<TableIdentifier> tables, CancellationToken ct = default)
    {
        var dict = new Dictionary<TableIdentifier, long>();
        foreach (var t in tables) dict[t] = 1L;
        return Task.FromResult<IReadOnlyDictionary<TableIdentifier, long>>(dict);
    }
}

public class ConsentCacheBenchmark : IDisposable
{
    private readonly ConsentCacheService _cacheService;
    private readonly MemoryCache _memCache;
    private readonly InProcessChannelEventBus _eventBus;
    private readonly Sid _userSid = new("S-1-5-21-1001");
    private readonly TableIdentifier _table = new("finance", "dbo", "invoices");

    public ConsentCacheBenchmark()
    {
        _memCache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 100_000 });
        var epochService = new FastEpochValidationService();
        _eventBus = new InProcessChannelEventBus();
        _cacheService = new ConsentCacheService(_memCache, epochService, _eventBus);

        var colAccess = new Dictionary<string, ColumnAccessLevel>
        {
            ["id"] = ColumnAccessLevel.Clear,
            ["email"] = ColumnAccessLevel.Mask
        };
        var decision = TableAccessDecision.Allowed(_table, colAccess, "amount > 100");

        _cacheService.SetCachedDecisionAsync(_userSid, _table, decision, TimeSpan.FromMinutes(10)).GetAwaiter().GetResult();
    }

    public Task<TableAccessDecision?> RunCacheHitAsync()
    {
        return _cacheService.GetCachedDecisionAsync(_userSid, _table);
    }

    public void Dispose()
    {
        _cacheService.Dispose();
        _memCache.Dispose();
        GC.SuppressFinalize(this);
    }
}
