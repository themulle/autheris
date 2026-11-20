namespace Autheris.Tests.Unit.Cache;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Cache;
using Autheris.Tests.Unit.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using StackExchange.Redis;
using Xunit;

public sealed class EpochBatchingAndPipeliningTests
{
    private readonly IEventBus _eventBus = StubEventBus.Instance;
    private readonly ITableSensitivityLookup _sensitivityLookup = StubTableSensitivityLookup.Insensitive;
    private readonly IDatabase _db = Substitute.For<IDatabase>();
    private readonly IConnectionMultiplexer _multiplexer = Substitute.For<IConnectionMultiplexer>();

    public EpochBatchingAndPipeliningTests()
    {
        _multiplexer.IsConnected.Returns(true);
        _multiplexer.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(_db);
    }

    private EpochValidationService CreateService(EpochValidationOptions epochOptions) =>
        new(
            Options.Create(new GatewayOptions { Caching = new CachingOptions { EpochValidation = epochOptions } }),
            _eventBus,
            _sensitivityLookup,
            _multiplexer,
            NullLogger<EpochValidationService>.Instance);

    [Fact]
    public async Task AreEpochsValidAsync_With5Tables_UsesSingleKeyGets_AndNeverMultiKeyMGet()
    {
        var tables = Enumerable.Range(1, 5)
            .Select(i => new TableIdentifier("default", "public", $"table_{i}"))
            .ToList();

        var checks = tables
            .Select(t => new EpochCheck(t, CachedEpoch: 1, IsHighlySensitive: false))
            .ToList();

        _db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult((RedisValue)"1"));

        var service = CreateService(new EpochValidationOptions { PipelinedMGetEnabled = true, LocalStalenessBudgetMilliseconds = 0 });
        var result = await service.AreEpochsValidAsync(checks);

        result.Count.ShouldBe(5);
        result.Values.All(v => v).ShouldBeTrue();

        // SEC-SC-05: Redis Cluster rejects multi-key MGET across slots; only single-key GETs are allowed.
        await _db.DidNotReceive().StringGetAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>());
        await _db.Received(5).StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task AreEpochsValidAsync_WhenPipelinedMGetDisabled_UsesIndividualGets()
    {
        var tables = Enumerable.Range(1, 3)
            .Select(i => new TableIdentifier("default", "public", $"table_{i}"))
            .ToList();

        var checks = tables
            .Select(t => new EpochCheck(t, CachedEpoch: 1, IsHighlySensitive: false))
            .ToList();

        _db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult((RedisValue)"1"));

        var service = CreateService(new EpochValidationOptions { PipelinedMGetEnabled = false, LocalStalenessBudgetMilliseconds = 0 });
        var result = await service.AreEpochsValidAsync(checks);

        result.Count.ShouldBe(3);
        result.Values.All(v => v).ShouldBeTrue();

        // ZERO batch MGET calls
        await _db.DidNotReceive().StringGetAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>());
        // Exactly 3 individual StringGetAsync calls
        await _db.Received(3).StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task RollbackRecovery_ExecutesExactlyOneScriptEvaluate_AndNoStringSet()
    {
        var table = new TableIdentifier("default", "public", "orders");
        var service = CreateService(new EpochValidationOptions { PipelinedMGetEnabled = true, LocalStalenessBudgetMilliseconds = 0 });

        // First read sees epoch 5
        _db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult((RedisValue)"5"));

        await service.AreEpochsValidAsync([new EpochCheck(table, 5, false)]);

        // Second read returns 3 (Redis rollback/tamper)
        _db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult((RedisValue)"3"));

        _db.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(RedisResult.Create((RedisValue)6)));

        await service.AreEpochsValidAsync([new EpochCheck(table, 5, false)]);

        // Must execute atomic Lua ScriptEvaluateAsync, NEVER StringSetAsync
        await _db.Received(1).ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>());
        await _db.DidNotReceive().StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<bool>(), Arg.Any<When>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task MicroCache_AfterInvalidateEpoch_ImmediatelyReturnsFalseForOldEpoch()
    {
        var table = new TableIdentifier("default", "public", "products");
        _db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult((RedisValue)"1"));
        _db.StringIncrementAsync(Arg.Any<RedisKey>(), Arg.Any<long>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(2L));

        var service = CreateService(new EpochValidationOptions { LocalStalenessBudgetMilliseconds = 250, PipelinedMGetEnabled = true });

        // Initial check buffers epoch 1 in micro-cache
        var valid1 = await service.AreEpochsValidAsync([new EpochCheck(table, 1, false)]);
        valid1[table].ShouldBeTrue();

        // Invalidate epoch on same node
        await service.InvalidateEpochAsync(table);

        // Next check for cachedEpoch = 1 must IMMEDIATELY fail without waiting for 250ms budget
        var valid2 = await service.AreEpochsValidAsync([new EpochCheck(table, 1, false)]);
        valid2[table].ShouldBeFalse();
    }

    [Fact]
    public async Task MicroCache_ForHighlySensitiveTable_AlwaysReadsRedis()
    {
        var table = new TableIdentifier("default", "public", "salaries");
        _db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult((RedisValue)"1"));

        var service = CreateService(new EpochValidationOptions { LocalStalenessBudgetMilliseconds = 250, PipelinedMGetEnabled = true });

        // First check
        await service.AreEpochsValidAsync([new EpochCheck(table, 1, IsHighlySensitive: true)]);

        // Second check with IsHighlySensitive: true must still read Redis, bypassing micro-cache budget
        await service.AreEpochsValidAsync([new EpochCheck(table, 1, IsHighlySensitive: true)]);

        await _db.Received(2).StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task MicroCache_NonAuthoritativeValue_NotBuffered_ReturnsFalse()
    {
        var table = new TableIdentifier("default", "public", "accounts");

#pragma warning disable CS0618
        _db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Throws(new RedisConnectionException(ConnectionFailureType.SocketClosed, "Connection failed"));
#pragma warning restore CS0618

        var service = CreateService(new EpochValidationOptions { LocalStalenessBudgetMilliseconds = 250, PipelinedMGetEnabled = true });
        var result = await service.AreEpochsValidAsync([new EpochCheck(table, 1, false)]);

        // INF-1: Redis error -> non-authoritative -> fail closed (false)
        result[table].ShouldBeFalse();
    }

    [Fact]
    public async Task SEC_SC_05_CrossSlotRejectingRedis_MultiTableValidation_StillSucceeds()
    {
        // Emulates Redis Cluster: any multi-key command spanning slots fails.
        _db.StringGetAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>())
            .Returns<Task<RedisValue[]>>(_ => throw new InvalidOperationException("CROSSSLOT Keys in request don't hash to the same slot"));
        _db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult((RedisValue)"4"));

        var service = new EpochValidationService(multiplexer: _multiplexer);
        var checks = Enumerable.Range(1, 4)
            .Select(i => new EpochCheck(new TableIdentifier("default", "public", $"t{i}"), 4, false)).ToList();

        var result = await service.AreEpochsValidAsync(checks);

        result.Values.ShouldAllBe(v => v);
    }

    [Fact]
    public async Task SEC_SC_05_AnySingleKeyReadFailure_FailsClosedForAllPendingTables()
    {
        var tables = Enumerable.Range(1, 3).Select(i => new TableIdentifier("default", "public", $"t{i}")).ToList();
        _db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(ci =>
            ((string?)ci.Arg<RedisKey>() ?? "").EndsWith("t2", StringComparison.Ordinal)
                ? Task.FromException<RedisValue>(new TimeoutException("redis timeout"))
                : Task.FromResult((RedisValue)"1"));

        var service = new EpochValidationService(multiplexer: _multiplexer);
        var result = await service.AreEpochsValidAsync(tables.Select(t => new EpochCheck(t, 1, false)).ToList());

        result.Count.ShouldBe(3);
        result.Values.ShouldAllBe(v => !v);
    }
}
