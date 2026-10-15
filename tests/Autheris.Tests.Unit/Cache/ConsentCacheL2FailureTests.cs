using System.Security.Cryptography;
using System.Text;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Cache;
using Autheris.Infrastructure.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using StackExchange.Redis;
using Xunit;

namespace Autheris.Tests.Unit.Cache;

/// <summary>R-3: only genuine L2 faults degrade to a cache miss (with a stable log event id); everything else propagates (fail-closed).</summary>
public sealed class ConsentCacheL2FailureTests
{
    private static readonly TableIdentifier Table = new("corp", "hr", "employees");
    private static readonly Sid User = new("S-1-5-21-R3");

    private sealed class EventLogger : ILogger<ConsentCacheService>
    {
        public List<(LogLevel Level, EventId Id, Exception? Exception)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, eventId, exception));
    }

    private static (ConsentCacheService Cache, IDatabase Db, EventLogger Log, IEpochValidationService Epochs) Create(bool epochValid = true)
    {
        var epochs = Substitute.For<IEpochValidationService>();
        epochs.IsEpochValidAsync(Arg.Any<TableIdentifier>(), Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(epochValid);
        epochs.GetCurrentEpochAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>()).Returns(1L);
        epochs.AreEpochsValidAsync(Arg.Any<IReadOnlyList<EpochCheck>>(), Arg.Any<CancellationToken>())
            .Returns(ci => (IReadOnlyDictionary<TableIdentifier, bool>)ci.Arg<IReadOnlyList<EpochCheck>>().ToDictionary(c => c.Table, _ => epochValid));
        var db = Substitute.For<IDatabase>();
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);
        var secrets = Substitute.For<IKeyVaultSecretProvider>();
        secrets.GetSecretBytes(Arg.Any<string>()).Returns(Encoding.UTF8.GetBytes("cluster-master-key"));
        var log = new EventLogger();
        var cache = new ConsentCacheService(
            new MemoryCache(new MemoryCacheOptions()), epochs, Substitute.For<IEventBus>(), Options.Create(new GatewayOptions()),
            new MemoryPackCacheSerializer(), mux, secrets, log);
        return (cache, db, log, epochs);
    }

    /// <summary>Makes the <c>StringSetAsync</c> overload that the cache binds to (TimeSpan converts to Expiration) fail with <paramref name="fault"/>.</summary>
    private static void FailWrites(IDatabase db, Exception fault) =>
        db.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<Expiration>(), Arg.Any<ValueCondition>(), Arg.Any<CommandFlags>())
            .Returns<Task<bool>>(_ => throw fault);

    private static TableAccessDecision Decision() =>
        TableAccessDecision.Allowed(Table, new Dictionary<string, ColumnAccessLevel>(), null, hasUnconstrainedColumnAllow: true);

    private static RedisException RedisFault() => new("redis down");

    [Fact]
    public async Task ConsentCache_L2Reads_ArePipelined_AllIssuedBeforeAnyCompletes()
    {
        var (cache, db, _, _) = Create();
        var tables = Enumerable.Range(1, 6).Select(i => new TableIdentifier("corp", "hr", "t" + i)).ToList();
        var pending = new List<TaskCompletionSource<RedisValue>>();
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(_ =>
        {
            var tcs = new TaskCompletionSource<RedisValue>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (pending) { pending.Add(tcs); }
            return tcs.Task;
        });

        var read = cache.GetCachedDecisionsAsync(new TenantId("t1"), User, tables);

        // No reply has been delivered yet: a sequential loop would have issued exactly one command by now.
        lock (pending) { pending.Count.ShouldBe(6); }
        foreach (var tcs in pending) { tcs.SetResult(RedisValue.Null); }
        var result = await read;

        result.Count.ShouldBe(6);
        result.Values.ShouldAllBe(v => v == null);
    }

    [Fact]
    public async Task ConsentCache_L2RedisException_FallsBackToMiss_AndLogsStableEventId()
    {
        var (cache, db, log, _) = Create();
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns<Task<RedisValue>>(_ => throw RedisFault());

        (await cache.GetCachedDecisionAsync(new TenantId("t1"), User, Table)).ShouldBeNull();
        (await cache.GetCachedDecisionsAsync(new TenantId("t1"), User, [Table])).ShouldContainKeyAndValue(Table, null);

        log.Entries.Count(e => e.Id.Id == ConsentCacheLogEvents.L2ReadFailed.Id && e.Level == LogLevel.Warning && e.Exception is RedisException).ShouldBe(2);
    }

    [Theory]
    [InlineData(typeof(TimeoutException))]
    [InlineData(typeof(CryptographicException))]
    public async Task ConsentCache_KnownL2Faults_FallBackToMiss(Type exceptionType)
    {
        var (cache, db, _, _) = Create();
        var ex = (Exception)Activator.CreateInstance(exceptionType)!;
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns<Task<RedisValue>>(_ => throw ex);

        (await cache.GetCachedDecisionAsync(new TenantId("t1"), User, Table)).ShouldBeNull();
    }

    [Fact]
    public async Task ConsentCache_UnexpectedException_Propagates()
    {
        var (cache, db, _, _) = Create();
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns<Task<RedisValue>>(_ => throw new InvalidOperationException("bug"));

        await Should.ThrowAsync<InvalidOperationException>(() => cache.GetCachedDecisionAsync(new TenantId("t1"), User, Table));
        await Should.ThrowAsync<InvalidOperationException>(() => cache.GetCachedDecisionsAsync(new TenantId("t1"), User, [Table]));
    }

    [Fact]
    public async Task ConsentCache_Cancellation_Propagates()
    {
        var (cache, db, _, _) = Create();
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns<Task<RedisValue>>(_ => throw new OperationCanceledException());

        await Should.ThrowAsync<OperationCanceledException>(() => cache.GetCachedDecisionAsync(new TenantId("t1"), User, Table));
    }

    [Fact]
    public async Task ConsentCache_L2WriteFailure_KeepsL1Entry()
    {
        var (cache, db, log, _) = Create();
        FailWrites(db, RedisFault());

        await cache.SetCachedDecisionAsync(new TenantId("t1"), User, Table, Decision(), TimeSpan.FromMinutes(1));

        (await cache.GetCachedDecisionAsync(new TenantId("t1"), User, Table)).ShouldNotBeNull();
        log.Entries.ShouldContain(e => e.Id.Id == ConsentCacheLogEvents.L2WriteFailed.Id && e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task ConsentCache_L2WriteUnexpectedException_Propagates()
    {
        var (cache, db, _, _) = Create();
        FailWrites(db, new InvalidOperationException("bug"));

        await Should.ThrowAsync<InvalidOperationException>(() =>
            cache.SetCachedDecisionAsync(new TenantId("t1"), User, Table, Decision(), TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task ConsentCache_L2DeleteFailureOfAStaleEntry_StillReturnsMiss_AndLogsDeleteEventId()
    {
        var (cache, db, log, _) = Create(epochValid: false);
        var serializer = new MemoryPackCacheSerializer();
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(ci =>
        {
            var key = ci.Arg<RedisKey>().ToString();
            var payload = serializer.Serialize(CachedConsentEnvelope.FromDecision(Decision(), epoch: 1));
            return Task.FromResult((RedisValue)cache.ProtectL2Payload(key, payload, DateTimeOffset.UtcNow.AddMinutes(5)));
        });
        db.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns<Task<bool>>(_ => throw RedisFault());

        var result = await cache.GetCachedDecisionsAsync(new TenantId("t1"), User, [Table]);

        result[Table].ShouldBeNull();
        log.Entries.ShouldContain(e => e.Id.Id == ConsentCacheLogEvents.L2DeleteFailed.Id && e.Level == LogLevel.Warning);
    }

    [Fact]
    public void ConsentCacheLogEvents_UseTheirOwnStableIds()
    {
        new[] { ConsentCacheLogEvents.L2ReadFailed.Id, ConsentCacheLogEvents.L2DeleteFailed.Id, ConsentCacheLogEvents.L2WriteFailed.Id }
            .Distinct().Count().ShouldBe(3);
        ConsentCacheLogEvents.L2ReadFailed.Name.ShouldBe("ConsentCacheL2ReadFailed");
    }
}
