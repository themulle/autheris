namespace Autheris.Tests.Unit.Cache;

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
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
            await cache.SetCachedDecisionAsync(user, Table, decision, TimeSpan.FromMinutes(10), ct: CancellationToken.None);
        }

        // The oldest key (user 0) should have been evicted when count exceeded 5000 (oldest 1000 keys evicted)
        var user0 = new Sid("S-1-5-21-0");
        var res0 = await cache.GetCachedDecisionAsync(user0, Table, ct: CancellationToken.None);
        res0.ShouldBeNull();

        // User 999 should also be evicted (since 1000 keys were evicted)
        var user999 = new Sid("S-1-5-21-999");
        var res999 = await cache.GetCachedDecisionAsync(user999, Table, ct: CancellationToken.None);
        res999.ShouldBeNull();

        // User 1000 should still be retained in L1
        var user1000 = new Sid("S-1-5-21-1000");
        var res1000 = await cache.GetCachedDecisionAsync(user1000, Table, ct: CancellationToken.None);
        res1000.ShouldNotBeNull();

        // The latest key (user 5000) should be present
        var user5000 = new Sid("S-1-5-21-5000");
        var res5000 = await cache.GetCachedDecisionAsync(user5000, Table, ct: CancellationToken.None);
        res5000.ShouldNotBeNull();
    }
}
