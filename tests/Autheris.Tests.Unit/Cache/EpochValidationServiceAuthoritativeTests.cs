namespace Autheris.Tests.Unit.Cache;

using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Cache;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using StackExchange.Redis;
using Autheris.Tests.Unit.VirtualFilters.Federation;
using Xunit;

/// <summary>Issue #107 SEC-RF107-05 (C-6): the authoritative epoch read is all-or-nothing, never throws, and is null whenever Redis cannot confirm every table.</summary>
[Trait("Category", "Security")]
public sealed class EpochValidationServiceAuthoritativeTests
{
    private static readonly TableIdentifier A = new("d", "s", "a");
    private static readonly TableIdentifier B = new("d", "s", "b");
    private static readonly TableIdentifier C = new("d", "s", "c");

    private readonly List<(LogLevel Level, string Message)> _logs = [];

    private EpochValidationService Service(IConnectionMultiplexer? multiplexer)
        => new(Options.Create(new GatewayOptions()), Substitute.For<IEventBus>(), Substitute.For<ITableSensitivityLookup>(), multiplexer,
            new ListLogger<EpochValidationService>(_logs));

    private static (IConnectionMultiplexer Multiplexer, IDatabase Db) Redis(bool connected = true)
    {
        var db = Substitute.For<IDatabase>();
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        multiplexer.IsConnected.Returns(connected);
        multiplexer.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);
        return (multiplexer, db);
    }

    [Fact]
    public async Task Connected_AllReadsConfirmed_ReturnsTheRedisEpochs()
    {
        var (multiplexer, db) = Redis();
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(ci =>
            Task.FromResult((RedisValue)(((string?)(RedisKey)ci[0])!.EndsWith(":d.s.b", StringComparison.Ordinal) ? "9" : "4")));

        var epochs = await Service(multiplexer).TryGetAuthoritativeEpochsAsync([A, B, A]);

        epochs.ShouldNotBeNull();
        epochs![A].ShouldBe(4);
        epochs[B].ShouldBe(9);
        epochs.Count.ShouldBe(2);
    }

    [Fact]
    public async Task ClientCancellation_IsRethrown_NotSwallowedAsNull()
    {
        var (multiplexer, _) = Redis();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(async () => await Service(multiplexer).TryGetAuthoritativeEpochsAsync([A], cts.Token));
        _logs.ShouldBeEmpty();
    }

    [Fact]
    public async Task Disconnected_ReturnsNull_WithoutReadingRedis_SEC_RF107_05()
    {
        var (multiplexer, db) = Redis(connected: false);

        (await Service(multiplexer).TryGetAuthoritativeEpochsAsync([A])).ShouldBeNull();

        await db.DidNotReceiveWithAnyArgs().StringGetAsync(default(RedisKey));
    }

    [Fact]
    public async Task OneFailingKeyOfABatch_ReturnsNull_NoPartialResult_SEC_RF107_05()
    {
        var (multiplexer, db) = Redis();
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(ci =>
            ((string?)(RedisKey)ci[0])!.EndsWith(":d.s.b", StringComparison.Ordinal)
                ? Task.FromException<RedisValue>(new InvalidOperationException("boom"))
                : Task.FromResult((RedisValue)"4"));

        (await Service(multiplexer).TryGetAuthoritativeEpochsAsync([A, B, C])).ShouldBeNull();
    }

    [Fact]
    public async Task ExceptionOfTheMultiplexer_ReturnsNull_NeverThrows_LogsTypeOnly_SEC_RF107_05()
    {
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        multiplexer.IsConnected.Throws(new InvalidOperationException("secret detail table_x"));

        (await Service(multiplexer).TryGetAuthoritativeEpochsAsync([A])).ShouldBeNull();

        _logs.ShouldContain(l => l.Level == LogLevel.Warning);
        _logs.ShouldAllBe(l => !l.Message.Contains("secret detail"));
    }

    [Fact]
    public async Task GetDatabaseThrows_ReturnsNull_SEC_RF107_05()
    {
        var (multiplexer, _) = Redis();
        multiplexer.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Throws(new InvalidOperationException("down"));

        (await Service(multiplexer).TryGetAuthoritativeEpochsAsync([A, B])).ShouldBeNull();
    }

    [Fact]
    public async Task WithoutRedis_TheLocalEpochIsAuthoritative_SingleNode()
    {
        var service = Service(null);
        await service.InvalidateEpochAsync(A);

        var epochs = await service.TryGetAuthoritativeEpochsAsync([A, B]);

        epochs.ShouldNotBeNull();
        epochs![A].ShouldBe(2);
        epochs[B].ShouldBe(1);
    }

    [Fact]
    public async Task EmptyTableList_ReturnsAnEmptyDictionary()
        => (await Service(null).TryGetAuthoritativeEpochsAsync([]))!.Count.ShouldBe(0);

    [Fact]
    public async Task ReadsRedisEveryTime_NeverTheLocalStalenessBudget()
    {
        var (multiplexer, db) = Redis();
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(Task.FromResult((RedisValue)"4"));
        var service = Service(multiplexer);

        await service.TryGetAuthoritativeEpochsAsync([A]);
        await service.TryGetAuthoritativeEpochsAsync([A]);

        await db.Received(2).StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
    }
}
