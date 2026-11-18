#pragma warning disable CA2012 // NSubstitute .Returns(...) configures a ValueTask member; the instance is not consumed here.
namespace Autheris.Tests.Unit.Cache;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Infrastructure.Cache;
using Autheris.Infrastructure.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using StackExchange.Redis;
using Xunit;

[Trait("Category", "Security")]
public sealed class ConsentCacheExpiryTests
{
    private static readonly Sid User = new("S-1-5-21-USER");
    private static readonly TableIdentifier Table = new("sales", "dbo", "orders");
    private static readonly TenantId Tenant = new("tenant-a");

    private static TableAccessDecision Decision() =>
        new(Table, true, new Dictionary<string, ColumnAccessLevel> { ["id"] = ColumnAccessLevel.Clear }, null, []);

    private static (ConsentCacheService Cache, IDatabase RedisDb, FakeTimeProvider Clock) CreateCache(
        FakeTimeProvider? existingClock = null,
        IDatabase? existingDb = null)
    {
        var clock = existingClock ?? new FakeTimeProvider(new DateTimeOffset(2026, 7, 1, 10, 0, 0, TimeSpan.Zero));
        var secrets = Substitute.For<IKeyVaultSecretProvider>();
        secrets.GetSecretBytes(Arg.Any<string>()).Returns(Encoding.UTF8.GetBytes("cluster-master-key"));
        var redis = Substitute.For<IConnectionMultiplexer>();
        var db = existingDb ?? Substitute.For<IDatabase>();
        redis.IsConnected.Returns(true);
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);
        var epoch = Substitute.For<IEpochValidationService>();
        epoch.GetCurrentEpochAsync(Table, Arg.Any<CancellationToken>()).Returns(1L);
        epoch.IsEpochValidAsync(Table, 1L, Arg.Any<CancellationToken>()).Returns(true);
        epoch.AreEpochsValidAsync(Arg.Any<IReadOnlyList<EpochCheck>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ValueTask.FromResult<IReadOnlyDictionary<TableIdentifier, bool>>(
                ci.Arg<IReadOnlyList<EpochCheck>>().ToDictionary(c => c.Table, _ => true)));

        var cache = new ConsentCacheService(
            new MemoryCache(new MemoryCacheOptions()),
            epoch,
            Substitute.For<IEventBus>(),
            Options.Create(new GatewayOptions()),
            new MemoryPackCacheSerializer(),
            redis,
            secrets,
            NullLogger<ConsentCacheService>.Instance,
            timeProvider: clock);

        return (cache, db, clock);
    }

    [Fact]
    public void Envelope_ToDecision_WithClock_RejectsDefaultOrExpiredValidUntil()
    {
        var now = new DateTimeOffset(2026, 7, 1, 10, 0, 0, TimeSpan.Zero);
        var fp = RestrictedScopePolicy.Default.Fingerprint;

        var legacyNoExpiry = CachedConsentEnvelope.FromDecision(Decision(), 1, fp);
        legacyNoExpiry.ToDecision(fp, now).ShouldBeNull();

        var bounded = CachedConsentEnvelope.FromDecision(Decision(), 1, fp, now.AddMinutes(5));
        bounded.ToDecision(fp, now).ShouldNotBeNull();
        bounded.ToDecision(fp, now.AddMinutes(5)).ShouldBeNull();
    }

    [Fact]
    public async Task L1CacheEntry_ExpiresExactlyAtValidUntil_OnSingleAndBatchReads()
    {
        var (cache, _, clock) = CreateCache();
        using var _ = cache;

        await cache.SetCachedDecisionAsync(Tenant, User, Table, Decision(), TimeSpan.FromMinutes(2));

        (await cache.GetCachedDecisionAsync(Tenant, User, Table)).ShouldNotBeNull();
        (await cache.GetCachedDecisionsAsync(Tenant, User, [Table]))[Table].ShouldNotBeNull();

        clock.Advance(TimeSpan.FromMinutes(2));

        (await cache.GetCachedDecisionAsync(Tenant, User, Table)).ShouldBeNull();
        (await cache.GetCachedDecisionsAsync(Tenant, User, [Table]))[Table].ShouldBeNull();
    }

    [Fact]
    public async Task L2Promotion_BoundsL1ByEnvelopeValidUntil_AndEvictsExpiredL2Entry()
    {
        var (cache, db, clock) = CreateCache();
        using var _ = cache;

        var serializer = new MemoryPackCacheSerializer();
        var grantExpiry = clock.GetUtcNow().AddSeconds(20);
        var l2FrameExpiry = clock.GetUtcNow().AddMinutes(10);
        var envelope = CachedConsentEnvelope.FromDecision(Decision(), 1, RestrictedScopePolicy.Default.Fingerprint, grantExpiry);
        var bytes = serializer.Serialize(envelope);

        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(ci => Task.FromResult((RedisValue)cache.ProtectL2Payload(ci.Arg<RedisKey>().ToString(), bytes, l2FrameExpiry)));
        db.StringGetAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>())
            .Returns(ci => Task.FromResult(ci.Arg<RedisKey[]>().Select(k => (RedisValue)cache.ProtectL2Payload(k.ToString(), bytes, l2FrameExpiry)).ToArray()));

        // First read promotes to L1 while grantExpiry is still 20s in the future
        (await cache.GetCachedDecisionAsync(Tenant, User, Table)).ShouldNotBeNull();

        // Within remaining grant window (10s), L1 serves the entry without re-reading L2
        clock.Advance(TimeSpan.FromSeconds(10));
        (await cache.GetCachedDecisionAsync(Tenant, User, Table)).ShouldNotBeNull();
        await db.Received(1).StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());

        // Advance past grantExpiry (still well before l2FrameExpiry): promoted L1 entry expires
        clock.Advance(TimeSpan.FromSeconds(11));
        (await cache.GetCachedDecisionAsync(Tenant, User, Table)).ShouldBeNull();

        // With a cold L1, reading the same stale L2 entry (l2FrameExpiry still valid) misses and evicts L2
        var (coldCache, _, _) = CreateCache(clock, db);
        using var __ = coldCache;
        (await coldCache.GetCachedDecisionAsync(Tenant, User, Table)).ShouldBeNull();
        await db.Received(1).KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
    }
}
