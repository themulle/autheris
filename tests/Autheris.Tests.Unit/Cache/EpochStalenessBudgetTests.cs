namespace Autheris.Tests.Unit.Cache;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Cache;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using StackExchange.Redis;
using Xunit;

/// <summary>WP-PF-07 / SEC-SC-05: bounded staleness budget; zero for sensitive tables and delegated sources.</summary>
public sealed class EpochStalenessBudgetTests
{
    private static readonly TableIdentifier Table = new("corp", "hr", "orders");

    private static (EpochValidationService Service, IDatabase Db, FakeTimeProvider Time, ITableSensitivityLookup Sensitivity) Create(bool sensitive = false)
    {
        var db = Substitute.For<IDatabase>();
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(Task.FromResult((RedisValue)"3"));
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.IsConnected.Returns(true);
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);
        var sensitivity = Substitute.For<ITableSensitivityLookup>();
        sensitivity.IsSensitiveAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>()).Returns(new ValueTask<bool>(sensitive));
        var time = new FakeTimeProvider();
        var service = new EpochValidationService(
            Options.Create(new GatewayOptions()), Substitute.For<IEventBus>(), sensitivity, mux, timeProvider: time);
        return (service, db, time, sensitivity);
    }

    private static Task<IReadOnlyDictionary<TableIdentifier, bool>> Check(EpochValidationService s, bool delegated = false, bool flaggedSensitive = false) =>
        s.AreEpochsValidAsync([new EpochCheck(Table, 3, flaggedSensitive, delegated)]).AsTask();

    [Fact]
    public void DefaultBudget_Is150Milliseconds_WithinTheValidatedRange()
    {
        new EpochValidationOptions().LocalStalenessBudgetMilliseconds.ShouldBe(150);
    }

    [Fact]
    public async Task NonSensitiveTable_IsServedLocally_WithinBudget_ThenRefreshed()
    {
        var (service, db, time, _) = Create();

        (await Check(service))[Table].ShouldBeTrue();
        time.Advance(TimeSpan.FromMilliseconds(100));
        (await Check(service))[Table].ShouldBeTrue();
        await db.Received(1).StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());

        time.Advance(TimeSpan.FromMilliseconds(100));
        (await Check(service))[Table].ShouldBeTrue();
        await db.Received(2).StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task SEC_SC_05_SensitiveTable_ByMetadataLookup_NeverUsesBudget()
    {
        var (service, db, _, _) = Create(sensitive: true);

        await Check(service);
        await Check(service);

        await db.Received(2).StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task SEC_SC_05_CallerFlaggedSensitiveTable_NeverUsesBudget()
    {
        var (service, db, _, _) = Create();

        await Check(service, flaggedSensitive: true);
        await Check(service, flaggedSensitive: true);

        await db.Received(2).StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task SEC_SC_05_DelegatedSource_HasZeroBudget()
    {
        var (service, db, _, _) = Create();

        await Check(service, delegated: true);
        await Check(service, delegated: true);

        await db.Received(2).StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task SEC_SC_05_LocalInvalidation_TakesEffectInsideTheBudget()
    {
        var (service, db, _, _) = Create();
        db.StringIncrementAsync(Arg.Any<RedisKey>(), Arg.Any<long>(), Arg.Any<CommandFlags>()).Returns(Task.FromResult(4L));

        (await Check(service))[Table].ShouldBeTrue();
        await service.InvalidateEpochAsync(Table);

        (await Check(service))[Table].ShouldBeFalse();
    }

    [Fact]
    public async Task SEC_SC_05_RedisFailureAfterBudgetExpiry_FailsClosed()
    {
        var (service, db, time, _) = Create();
        (await Check(service))[Table].ShouldBeTrue();

        time.Advance(TimeSpan.FromMilliseconds(200));
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns<Task<RedisValue>>(_ => throw new TimeoutException());

        (await Check(service))[Table].ShouldBeFalse();
    }

    [Fact]
    public async Task SEC_SC_05_SingleTableValidation_HonoursTheDelegatedSourceFlag_WithZeroBudget()
    {
        var (service, db, _, _) = Create();
        (await Check(service))[Table].ShouldBeTrue(); // fills the local budget entry
        await db.Received(1).StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());

        (await ((IEpochValidationService)service).IsEpochValidAsync(Table, 3, isDelegatedSource: false)).ShouldBeTrue();
        await db.Received(1).StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());

        (await ((IEpochValidationService)service).IsEpochValidAsync(Table, 3, isDelegatedSource: true)).ShouldBeTrue();
        await db.Received(2).StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
        (await ((IEpochValidationService)service).IsEpochValidAsync(Table, 2, isDelegatedSource: true)).ShouldBeFalse();
    }
}
