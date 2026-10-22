namespace Autheris.Tests.Unit.Cache;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Cache;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using StackExchange.Redis;
using Xunit;

/// <summary>
/// A consent revoke must not be lost when the Redis epoch INCR fails: the revoking node treats the table as stale until Redis
/// confirms the bump (retried on the next read / invalidation / reconnect), sensitive tables bypass the cache meanwhile, and
/// peers that received the invalidation never accept a Redis epoch that did not move past what they knew.
/// </summary>
[Trait("Category", "Security")]
public sealed class EpochRevokeRedisFailureTests
{
    private static readonly TableIdentifier A = new("d", "s", "a");
    private static readonly TableIdentifier Sensitive = new("d", "s", "salary");
    private static readonly TableIdentifier Other = new("d", "s", "other");

    private sealed class Harness
    {
        public required EpochValidationService Service { get; init; }
        public required IConnectionMultiplexer Multiplexer { get; init; }
        public required IDatabase Db { get; init; }
        public Func<string, Task>? InvalidationHandler { get; set; }
        public Microsoft.Extensions.Time.Testing.FakeTimeProvider? Time { get; init; }

        public Task DeliverInvalidationAsync(TableIdentifier table) => InvalidationHandler!(table.ToString().ToLowerInvariant());
    }

    private static Harness Create(Microsoft.Extensions.Time.Testing.FakeTimeProvider? time = null)
    {
        var db = Substitute.For<IDatabase>();
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(Task.FromResult((RedisValue)"5"));
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        multiplexer.IsConnected.Returns(true);
        multiplexer.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);

        var sensitivity = Substitute.For<ITableSensitivityLookup>();
        sensitivity.IsSensitiveAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>()).Returns(new ValueTask<bool>(false));
        sensitivity.IsSensitiveAsync(Sensitive, Arg.Any<CancellationToken>()).Returns(new ValueTask<bool>(true));

        var bus = Substitute.For<IEventBus>();
        Harness? harness = null;
        bus.Subscribe(Arg.Any<string>(), Arg.Any<Func<string, Task>>()).Returns(ci =>
        {
            harness!.InvalidationHandler = ci.ArgAt<Func<string, Task>>(1);
            return Substitute.For<IDisposable>();
        });

        harness = new Harness
        {
            Service = new EpochValidationService(Options.Create(new GatewayOptions()), bus, sensitivity, multiplexer, timeProvider: time),
            Multiplexer = multiplexer,
            Db = db,
            Time = time,
        };
        return harness;
    }

    private static void IncrementFails(IDatabase db) =>
        db.StringIncrementAsync(Arg.Any<RedisKey>(), Arg.Any<long>(), Arg.Any<CommandFlags>()).ThrowsAsync(new TimeoutException("redis timeout"));

    private static void IncrementReturns(IDatabase db, long value) =>
        db.StringIncrementAsync(Arg.Any<RedisKey>(), Arg.Any<long>(), Arg.Any<CommandFlags>()).Returns(Task.FromResult(value));

    [Fact]
    public async Task FailedIncrement_KeepsTheTableStaleOnThisNode_EvenThoughRedisStillReportsTheOldEpoch()
    {
        var h = Create();
        (await h.Service.GetCurrentEpochAsync(A)).ShouldBe(5);
        IncrementFails(h.Db);

        await h.Service.InvalidateEpochAsync(A);

        (await h.Service.IsEpochValidAsync(A, 5)).ShouldBeFalse();
        (await h.Service.AreEpochsValidAsync([new EpochCheck(A, 5, false)]))[A].ShouldBeFalse();
        (await h.Service.TryGetAuthoritativeEpochsAsync([A])).ShouldBeNull();
    }

    [Fact]
    public async Task PendingBump_IsRetriedOnTheNextRead_ThenRedisIsAuthoritativeAgain()
    {
        var h = Create();
        (await h.Service.GetCurrentEpochAsync(A)).ShouldBe(5);
        IncrementFails(h.Db);
        await h.Service.InvalidateEpochAsync(A);
        (await h.Service.IsEpochValidAsync(A, 5)).ShouldBeFalse();

        IncrementReturns(h.Db, 6);
        h.Db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(Task.FromResult((RedisValue)"6"));

        (await h.Service.IsEpochValidAsync(A, 6)).ShouldBeTrue();
        (await h.Service.IsEpochValidAsync(A, 5)).ShouldBeFalse();
        await h.Db.Received().StringIncrementAsync(Arg.Any<RedisKey>(), Arg.Any<long>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task RevokeWhileDisconnected_IsWrittenToRedisOnReconnect()
    {
        var h = Create();
        (await h.Service.GetCurrentEpochAsync(A)).ShouldBe(5);
        h.Multiplexer.IsConnected.Returns(false);

        await h.Service.InvalidateEpochAsync(A);
        await h.Db.DidNotReceive().StringIncrementAsync(Arg.Any<RedisKey>(), Arg.Any<long>(), Arg.Any<CommandFlags>());
        (await h.Service.IsEpochValidAsync(A, 5)).ShouldBeFalse();

        h.Multiplexer.IsConnected.Returns(true);
        IncrementReturns(h.Db, 6);
        await h.Service.OnConnectionRestoredAsync();

        await h.Db.Received(1).StringIncrementAsync(Arg.Any<RedisKey>(), Arg.Any<long>(), Arg.Any<CommandFlags>());
        h.Db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(Task.FromResult((RedisValue)"6"));
        (await h.Service.IsEpochValidAsync(A, 6)).ShouldBeTrue();
        (await h.Service.IsEpochValidAsync(A, 5)).ShouldBeFalse();
    }

    [Fact]
    public async Task WhileABumpIsPending_SensitiveTablesBypassTheCache_OtherTablesStayCached()
    {
        var h = Create();
        IncrementFails(h.Db);
        await h.Service.InvalidateEpochAsync(A);

        (await h.Service.IsEpochValidAsync(Sensitive, 5)).ShouldBeFalse();
        (await h.Service.AreEpochsValidAsync([new EpochCheck(Sensitive, 5, false)]))[Sensitive].ShouldBeFalse();
        (await h.Service.AreEpochsValidAsync([new EpochCheck(Other, 5, IsHighlySensitive: true)]))[Other].ShouldBeFalse();
        (await h.Service.IsEpochValidAsync(Other, 5)).ShouldBeTrue();
    }

    [Fact]
    public async Task Peer_ReceivingTheInvalidation_DoesNotAcceptTheUnmovedRedisEpoch()
    {
        var h = Create();
        (await h.Service.IsEpochValidAsync(A, 5)).ShouldBeTrue();
        h.Db.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(RedisResult.Create((RedisValue)6L)));

        // The revoking node's INCR was lost, but its invalidation event arrived: Redis still says 5.
        await h.DeliverInvalidationAsync(A);

        (await h.Service.IsEpochValidAsync(A, 5)).ShouldBeFalse();
        (await h.Service.AreEpochsValidAsync([new EpochCheck(A, 5, false)]))[A].ShouldBeFalse();
        await h.Db.Received().ScriptEvaluateAsync(
            Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Is<RedisValue[]>(v => v.Length == 1 && v[0] == "6"), Arg.Any<CommandFlags>());

        h.Db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(Task.FromResult((RedisValue)"6"));
        (await h.Service.IsEpochValidAsync(A, 6)).ShouldBeTrue();
    }

    [Fact]
    public async Task Peer_ReceivingTheInvalidation_AcceptsARedisEpochThatAlreadyAdvanced()
    {
        var h = Create();
        (await h.Service.IsEpochValidAsync(A, 5)).ShouldBeTrue();
        h.Db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(Task.FromResult((RedisValue)"6"));

        await h.DeliverInvalidationAsync(A);

        (await h.Service.IsEpochValidAsync(A, 6)).ShouldBeTrue();
        (await h.Service.IsEpochValidAsync(A, 5)).ShouldBeFalse();
        await h.Db.DidNotReceive().ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task OwnInvalidationEcho_DoesNotForceASecondBump()
    {
        var h = Create();
        (await h.Service.GetCurrentEpochAsync(A)).ShouldBe(5);
        IncrementReturns(h.Db, 6);

        await h.Service.InvalidateEpochAsync(A);
        await h.DeliverInvalidationAsync(A);
        h.Db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(Task.FromResult((RedisValue)"6"));

        (await h.Service.IsEpochValidAsync(A, 6)).ShouldBeTrue();
        await h.Db.Received(1).StringIncrementAsync(Arg.Any<RedisKey>(), Arg.Any<long>(), Arg.Any<CommandFlags>());
        await h.Db.DidNotReceive().ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task IncrementBelowTheFloor_AfterARedisRestart_IsLiftedAboveThePreRevokeEpoch()
    {
        var h = Create();
        (await h.Service.GetCurrentEpochAsync(A)).ShouldBe(5);
        IncrementReturns(h.Db, 1); // Redis lost the key: INCR starts again at 1
        h.Db.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(RedisResult.Create((RedisValue)6L)));

        await h.Service.InvalidateEpochAsync(A);

        await h.Db.Received(1).ScriptEvaluateAsync(
            Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Is<RedisValue[]>(v => v.Length == 1 && v[0] == "6"), Arg.Any<CommandFlags>());
        h.Db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(Task.FromResult((RedisValue)"6"));
        (await h.Service.IsEpochValidAsync(A, 6)).ShouldBeTrue();
    }

    [Fact]
    public async Task Peer_AfterReconnect_WithinGrace_TreatsRedisEpochAsNonAuthoritative()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);
        var h = Create(time);
        (await h.Service.GetCurrentEpochAsync(A)).ShouldBe(5);

        // Redis reconnects
        await h.Service.OnConnectionRestoredAsync();

        // Within grace period (5 seconds default): Redis epoch is treated as non-authoritative
        time.Advance(TimeSpan.FromSeconds(2));
        var authEpochs = await h.Service.TryGetAuthoritativeEpochsAsync([A]);
        authEpochs.ShouldBeNull();
    }

    [Fact]
    public async Task Peer_AfterGraceExpired_RedisAuthoritativeAgain()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);
        var h = Create(time);
        (await h.Service.GetCurrentEpochAsync(A)).ShouldBe(5);

        // Redis reconnects
        await h.Service.OnConnectionRestoredAsync();

        // Advance past grace period (5 seconds)
        time.Advance(TimeSpan.FromSeconds(6));

        var authEpochs = await h.Service.TryGetAuthoritativeEpochsAsync([A]);
        authEpochs.ShouldNotBeNull();
        authEpochs[A].ShouldBe(5);
    }
}
