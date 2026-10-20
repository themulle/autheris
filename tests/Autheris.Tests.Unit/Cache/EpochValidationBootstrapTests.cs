namespace Autheris.Tests.Unit.Cache;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Cache;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using StackExchange.Redis;
using Xunit;

public sealed class EpochValidationBootstrapTests
{
    private static readonly TableIdentifier Table = new("corp", "hr", "employees");

    private sealed class TestLogger : ILogger<EpochValidationService>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }

    private static (EpochValidationService Service, IDatabase Db, FakeTimeProvider Time, TestLogger Logger) CreateService()
    {
        var db = Substitute.For<IDatabase>();
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.IsConnected.Returns(true);
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);

        var sensitivity = Substitute.For<ITableSensitivityLookup>();
        sensitivity.IsSensitiveAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<bool>(false));

        var time = new FakeTimeProvider();
        var logger = new TestLogger();
        var service = new EpochValidationService(
            Options.Create(new GatewayOptions()),
            Substitute.For<IEventBus>(),
            sensitivity,
            mux,
            logger,
            time);

        return (service, db, time, logger);
    }

    [Fact]
    public async Task EmptyCacheBootstrap_LogsDebug_DoesNotLogWarning()
    {
        var (service, db, _, logger) = CreateService();

        // Redis has no epoch key set (returns RedisValue.Null)
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(Task.FromResult(RedisValue.Null));
        db.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(RedisResult.Create((RedisValue)1L)));

        var epoch = await service.GetCurrentEpochAsync(Table);

        epoch.ShouldBe(1L);
        logger.Entries.Any(e => e.Level == LogLevel.Debug && e.Message.Contains("Empty policy epoch cache bootstrap")).ShouldBeTrue();
        logger.Entries.Any(e => e.Level == LogLevel.Warning).ShouldBeFalse();
    }

    [Fact]
    public async Task GenuineRollback_LogsWarning_AndRateLimitsTo30Seconds()
    {
        var (service, db, time, logger) = CreateService();

        // First call: Redis returns epoch 5 (establishing highestSeen = 5)
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(Task.FromResult((RedisValue)"5"));
        var ep1 = await service.GetCurrentEpochAsync(Table);
        ep1.ShouldBe(5L);

        // Redis regresses to epoch 2 (< highestSeen 5)
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(Task.FromResult((RedisValue)"2"));
        db.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(RedisResult.Create((RedisValue)6L)));

        // Call 1 after rollback: should log Warning
        var ep2 = await service.GetCurrentEpochAsync(Table);
        ep2.ShouldBe(6L);

        var warnings1 = logger.Entries.Where(e => e.Level == LogLevel.Warning && e.Message.Contains("Policy epoch rollback detected")).ToList();
        warnings1.Count.ShouldBe(1);

        // Advance 10 seconds: another rollback check occurs
        time.Advance(TimeSpan.FromSeconds(10));
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(Task.FromResult((RedisValue)"2"));

        var ep3 = await service.GetCurrentEpochAsync(Table);
        ep3.ShouldBe(6L);

        // Warning count should still be 1 (suppressed due to 30s rate-limit)
        var warnings2 = logger.Entries.Where(e => e.Level == LogLevel.Warning && e.Message.Contains("Policy epoch rollback detected")).ToList();
        warnings2.Count.ShouldBe(1);

        // Advance 25 more seconds (total 35s > 30s): another rollback check occurs
        time.Advance(TimeSpan.FromSeconds(25));
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(Task.FromResult((RedisValue)"2"));

        var ep4 = await service.GetCurrentEpochAsync(Table);
        ep4.ShouldBe(6L);

        // Warning count should now be 2
        var warnings3 = logger.Entries.Where(e => e.Level == LogLevel.Warning && e.Message.Contains("Policy epoch rollback detected")).ToList();
        warnings3.Count.ShouldBe(2);
    }
}
