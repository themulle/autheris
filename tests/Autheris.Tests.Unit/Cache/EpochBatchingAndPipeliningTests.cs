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
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using StackExchange.Redis;
using Xunit;

public sealed class EpochBatchingAndPipeliningTests
{
    private readonly IEventBus _eventBus = Substitute.For<IEventBus>();
    private readonly ITableSensitivityLookup _sensitivityLookup = Substitute.For<ITableSensitivityLookup>();

    [Fact]
    public async Task AreEpochsValidAsync_With5Tables_ExecutesExactlyOneMGet_AndNoIndividualGet()
    {
        var db = Substitute.For<IDatabase>();
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        multiplexer.IsConnected.Returns(true);
        multiplexer.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);

        var tables = Enumerable.Range(1, 5)
            .Select(i => new TableIdentifier("default", "public", $"table_{i}"))
            .ToList();

        var checks = tables
            .Select(t => new EpochCheck(t, CachedEpoch: 1, IsHighlySensitive: false))
            .ToList();

        // Setup MGET return values
        db.StringGetAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>())
            .Returns(ci =>
            {
                var keys = ci.Arg<RedisKey[]>();
                return Task.FromResult(keys.Select(_ => (RedisValue)"1").ToArray());
            });

        var options = Options.Create(new GatewayOptions
        {
            Caching = new CachingOptions
            {
                EpochValidation = new EpochValidationOptions
                {
                    PipelinedMGetEnabled = true,
                    LocalStalenessBudgetMilliseconds = 0
                }
            }
        });

        var service = new EpochValidationService(
            options,
            _eventBus,
            _sensitivityLookup,
            multiplexer,
            NullLogger<EpochValidationService>.Instance);

        var result = await service.AreEpochsValidAsync(checks);

        result.Count.ShouldBe(5);
        result.Values.All(v => v).ShouldBeTrue();

        // Exactly ONE batch call
        await db.Received(1).StringGetAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>());
        // ZERO individual StringGetAsync calls
        await db.DidNotReceive().StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task AreEpochsValidAsync_WhenPipelinedMGetDisabled_UsesIndividualGets()
    {
        var db = Substitute.For<IDatabase>();
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        multiplexer.IsConnected.Returns(true);
        multiplexer.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);

        var tables = Enumerable.Range(1, 3)
            .Select(i => new TableIdentifier("default", "public", $"table_{i}"))
            .ToList();

        var checks = tables
            .Select(t => new EpochCheck(t, CachedEpoch: 1, IsHighlySensitive: false))
            .ToList();

        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult((RedisValue)"1"));

        var options = Options.Create(new GatewayOptions
        {
            Caching = new CachingOptions
            {
                EpochValidation = new EpochValidationOptions
                {
                    PipelinedMGetEnabled = false,
                    LocalStalenessBudgetMilliseconds = 0
                }
            }
        });

        var service = new EpochValidationService(
            options,
            _eventBus,
            _sensitivityLookup,
            multiplexer,
            NullLogger<EpochValidationService>.Instance);

        var result = await service.AreEpochsValidAsync(checks);

        result.Count.ShouldBe(3);
        result.Values.All(v => v).ShouldBeTrue();

        // ZERO batch MGET calls
        await db.DidNotReceive().StringGetAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>());
        // Exactly 3 individual StringGetAsync calls
        await db.Received(3).StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task RollbackRecovery_ExecutesExactlyOneScriptEvaluate_AndNoStringSet()
    {
        var db = Substitute.For<IDatabase>();
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        multiplexer.IsConnected.Returns(true);
        multiplexer.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);

        var table = new TableIdentifier("default", "public", "orders");
        var options = Options.Create(new GatewayOptions
        {
            Caching = new CachingOptions
            {
                EpochValidation = new EpochValidationOptions
                {
                    PipelinedMGetEnabled = true,
                    LocalStalenessBudgetMilliseconds = 0
                }
            }
        });

        var service = new EpochValidationService(
            options,
            _eventBus,
            _sensitivityLookup,
            multiplexer,
            NullLogger<EpochValidationService>.Instance);

        // First read sees epoch 5
        db.StringGetAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(new[] { (RedisValue)"5" }));

        await service.AreEpochsValidAsync([new EpochCheck(table, 5, false)]);

        // Second read returns 3 (Redis rollback/tamper)
        db.StringGetAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(new[] { (RedisValue)"3" }));

        db.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(RedisResult.Create((RedisValue)6)));

        await service.AreEpochsValidAsync([new EpochCheck(table, 5, false)]);

        // Must execute atomic Lua ScriptEvaluateAsync, NEVER StringSetAsync
        await db.Received(1).ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>());
        await db.DidNotReceive().StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<bool>(), Arg.Any<When>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task MicroCache_AfterInvalidateEpoch_ImmediatelyReturnsFalseForOldEpoch()
    {
        var db = Substitute.For<IDatabase>();
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        multiplexer.IsConnected.Returns(true);
        multiplexer.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);

        var table = new TableIdentifier("default", "public", "products");
        var options = Options.Create(new GatewayOptions
        {
            Caching = new CachingOptions
            {
                EpochValidation = new EpochValidationOptions
                {
                    LocalStalenessBudgetMilliseconds = 250,
                    PipelinedMGetEnabled = true
                }
            }
        });

        db.StringGetAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(new[] { (RedisValue)"1" }));
        db.StringIncrementAsync(Arg.Any<RedisKey>(), Arg.Any<long>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(2L));

        var service = new EpochValidationService(
            options,
            _eventBus,
            _sensitivityLookup,
            multiplexer,
            NullLogger<EpochValidationService>.Instance);

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
        var db = Substitute.For<IDatabase>();
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        multiplexer.IsConnected.Returns(true);
        multiplexer.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);

        var table = new TableIdentifier("default", "public", "salaries");
        var options = Options.Create(new GatewayOptions
        {
            Caching = new CachingOptions
            {
                EpochValidation = new EpochValidationOptions
                {
                    LocalStalenessBudgetMilliseconds = 250,
                    PipelinedMGetEnabled = true
                }
            }
        });

        db.StringGetAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(new[] { (RedisValue)"1" }));

        var service = new EpochValidationService(
            options,
            _eventBus,
            _sensitivityLookup,
            multiplexer,
            NullLogger<EpochValidationService>.Instance);

        // First check
        await service.AreEpochsValidAsync([new EpochCheck(table, 1, IsHighlySensitive: true)]);

        // Second check with IsHighlySensitive: true must still read Redis, bypassing micro-cache budget
        await service.AreEpochsValidAsync([new EpochCheck(table, 1, IsHighlySensitive: true)]);

        await db.Received(2).StringGetAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task MicroCache_NonAuthoritativeValue_NotBuffered_ReturnsFalse()
    {
        var db = Substitute.For<IDatabase>();
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        multiplexer.IsConnected.Returns(true);
        multiplexer.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);

        var table = new TableIdentifier("default", "public", "accounts");
        var options = Options.Create(new GatewayOptions
        {
            Caching = new CachingOptions
            {
                EpochValidation = new EpochValidationOptions
                {
                    LocalStalenessBudgetMilliseconds = 250,
                    PipelinedMGetEnabled = true
                }
            }
        });

#pragma warning disable CS0618
        db.StringGetAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>())
            .Throws(new RedisConnectionException(ConnectionFailureType.SocketClosed, "Connection failed"));
#pragma warning restore CS0618

        var service = new EpochValidationService(
            options,
            _eventBus,
            _sensitivityLookup,
            multiplexer,
            NullLogger<EpochValidationService>.Instance);

        var result = await service.AreEpochsValidAsync([new EpochCheck(table, 1, false)]);

        // INF-1: Redis error -> non-authoritative -> fail closed (false)
        result[table].ShouldBeFalse();
    }
}
