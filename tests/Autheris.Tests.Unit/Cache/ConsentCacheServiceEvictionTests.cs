namespace Autheris.Tests.Unit.Cache;

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Cache;
using Autheris.Infrastructure.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using StackExchange.Redis;
using Xunit;

public sealed class ConsentCacheServiceEvictionTests
{
    private static readonly TableIdentifier Table = new("corp", "hr", "employees");

    private static (ConsentCacheService Cache, IMemoryCache MemCache) CreateService()
    {
        var memCache = new MemoryCache(new MemoryCacheOptions());
        var epochs = Substitute.For<IEpochValidationService>();
        epochs.IsEpochValidAsync(Arg.Any<TableIdentifier>(), Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(true);
        epochs.GetCurrentEpochAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>()).Returns(1L);

        var secrets = Substitute.For<IKeyVaultSecretProvider>();
        secrets.GetSecretBytes(Arg.Any<string>()).Returns(Encoding.UTF8.GetBytes("test-key-32-bytes-long-padding!!"));

        var cache = new ConsentCacheService(
            memCache,
            epochs,
            Substitute.For<IEventBus>(),
            Options.Create(new GatewayOptions()),
            new MemoryPackCacheSerializer(),
            null, // No Redis mux to focus on L1
            secrets,
            NullLogger<ConsentCacheService>.Instance);

        return (cache, memCache);
    }

    [Fact]
    public async Task L1Cache_EvictsOldest20Percent_WhenTableKeyCountExceeds5000()
    {
        var (cache, _) = CreateService();

        // Add 5001 decisions for different users
        for (int i = 0; i <= 5000; i++)
        {
            var user = new Sid($"S-1-5-21-{i}");
            var decision = TableAccessDecision.Allowed(Table, new Dictionary<string, ColumnAccessLevel> { ["col1"] = ColumnAccessLevel.Clear });
            await cache.SetCachedDecisionAsync(Autheris.Tests.Common.TestTenants.Default, user, Table, decision, TimeSpan.FromMinutes(10), ct: CancellationToken.None);
        }

        // The oldest key (user 0) should have been evicted when count exceeded 5000 (oldest 1000 keys evicted)
        var user0 = new Sid("S-1-5-21-0");
        var res0 = await cache.GetCachedDecisionAsync(Autheris.Tests.Common.TestTenants.Default, user0, Table, ct: CancellationToken.None);
        res0.ShouldBeNull();

        // User 999 should also be evicted (since 1000 keys were evicted)
        var user999 = new Sid("S-1-5-21-999");
        var res999 = await cache.GetCachedDecisionAsync(Autheris.Tests.Common.TestTenants.Default, user999, Table, ct: CancellationToken.None);
        res999.ShouldBeNull();

        // User 1000 should still be retained in L1
        var user1000 = new Sid("S-1-5-21-1000");
        var res1000 = await cache.GetCachedDecisionAsync(Autheris.Tests.Common.TestTenants.Default, user1000, Table, ct: CancellationToken.None);
        res1000.ShouldNotBeNull();

        // The latest key (user 5000) should be present
        var user5000 = new Sid("S-1-5-21-5000");
        var res5000 = await cache.GetCachedDecisionAsync(Autheris.Tests.Common.TestTenants.Default, user5000, Table, ct: CancellationToken.None);
        res5000.ShouldNotBeNull();
    }

    [Fact]
    public async Task L1Cache_WhenReplacingExistingKey_PreservesTrackedKeyForTableEviction()
    {
        using var inner = new MemoryCache(new MemoryCacheOptions());
        var replacedCallbackDone = new TaskCompletionSource<EvictionReason>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var trackingCache = new CallbackTrackingMemoryCache(inner, replacedCallbackDone);

        var epochs = new FixedEpochService();
        var secrets = new FixedSecretProvider();

        using var cache = new ConsentCacheService(
            trackingCache,
            epochs,
            new NoopEventBus(),
            Options.Create(new GatewayOptions()),
            new MemoryPackCacheSerializer(),
            null,
            secrets,
            NullLogger<ConsentCacheService>.Instance);

        var user = new Sid("S-1-5-21-ROTATE");
        var d1 = TableAccessDecision.Allowed(Table, new Dictionary<string, ColumnAccessLevel> { ["col1"] = ColumnAccessLevel.Mask });
        var d2 = TableAccessDecision.Allowed(Table, new Dictionary<string, ColumnAccessLevel> { ["col1"] = ColumnAccessLevel.Clear });

        await cache.SetCachedDecisionAsync(Autheris.Tests.Common.TestTenants.Default, user, Table, d1, TimeSpan.FromMinutes(10));
        await cache.SetCachedDecisionAsync(Autheris.Tests.Common.TestTenants.Default, user, Table, d2, TimeSpan.FromMinutes(10));

        var reason = await replacedCallbackDone.Task.WaitAsync(TimeSpan.FromSeconds(5));
        reason.ShouldBe(EvictionReason.Replaced);

        cache.GetTrackedKeyCount(Table).ShouldBe(1);

        await cache.EvictTableDecisionsAsync(Table);
        var afterEvict = await cache.GetCachedDecisionAsync(Autheris.Tests.Common.TestTenants.Default, user, Table);
        afterEvict.ShouldBeNull();
    }

    private sealed class CallbackTrackingMemoryCache(
        MemoryCache inner,
        TaskCompletionSource<EvictionReason> onReplaced) : IMemoryCache
    {
        private bool _attachedFirst;

        public ICacheEntry CreateEntry(object key)
        {
            var entry = inner.CreateEntry(key);
            if (!_attachedFirst)
            {
                _attachedFirst = true;
                return new HookedEntry(entry, onReplaced);
            }

            return entry;
        }

        public void Remove(object key) => inner.Remove(key);

        public bool TryGetValue(object key, out object? value) => inner.TryGetValue(key, out value);

        public void Dispose() => inner.Dispose();

        private sealed class HookedEntry(ICacheEntry inner, TaskCompletionSource<EvictionReason> onReplaced) : ICacheEntry
        {
            public object Key => inner.Key;
            public object? Value { get => inner.Value; set => inner.Value = value; }
            public DateTimeOffset? AbsoluteExpiration { get => inner.AbsoluteExpiration; set => inner.AbsoluteExpiration = value; }
            public TimeSpan? AbsoluteExpirationRelativeToNow { get => inner.AbsoluteExpirationRelativeToNow; set => inner.AbsoluteExpirationRelativeToNow = value; }
            public TimeSpan? SlidingExpiration { get => inner.SlidingExpiration; set => inner.SlidingExpiration = value; }
            public IList<Microsoft.Extensions.Primitives.IChangeToken> ExpirationTokens => inner.ExpirationTokens;
            public IList<PostEvictionCallbackRegistration> PostEvictionCallbacks => inner.PostEvictionCallbacks;
            public CacheItemPriority Priority { get => inner.Priority; set => inner.Priority = value; }
            public long? Size { get => inner.Size; set => inner.Size = value; }

            public void Dispose()
            {
                inner.PostEvictionCallbacks.Add(new PostEvictionCallbackRegistration
                {
                    EvictionCallback = (_, _, reason, _) => onReplaced.TrySetResult(reason)
                });
                inner.Dispose();
            }
        }
    }

    private sealed class FixedEpochService : IEpochValidationService
    {
        public Task<long> GetCurrentEpochAsync(TableIdentifier table, CancellationToken ct = default) => Task.FromResult(1L);
        public Task<bool> IsEpochValidAsync(TableIdentifier table, long cachedEpoch, CancellationToken ct = default) => Task.FromResult(true);
        public Task InvalidateEpochAsync(TableIdentifier table, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyDictionary<TableIdentifier, long>> GetCurrentEpochsAsync(IEnumerable<TableIdentifier> tables, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<TableIdentifier, long>>(new Dictionary<TableIdentifier, long>());
    }

    private sealed class FixedSecretProvider : IKeyVaultSecretProvider
    {
        private static readonly byte[] Key = Encoding.UTF8.GetBytes("test-key-32-bytes-long-padding!!");
        public byte[] GetSecretBytes(string secretRef) => Key;
    }

    private sealed class NoopEventBus : IEventBus
    {
        public event Action? ConnectionRestored { add { } remove { } }
        public Task PublishAsync<T>(string channel, T message, CancellationToken ct = default) => Task.CompletedTask;
        public IDisposable Subscribe<T>(string channel, Func<T, Task> handler) => new NoopDisposable();
        public Task<long> IncrementCounterAsync(string key, CancellationToken ct = default) => Task.FromResult(0L);
        public Task<long> GetCounterAsync(string key, CancellationToken ct = default) => Task.FromResult(0L);
        private sealed class NoopDisposable : IDisposable { public void Dispose() { } }
    }
}
