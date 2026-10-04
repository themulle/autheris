using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Cache;
using Autheris.Infrastructure.Garnet;
using Autheris.Infrastructure.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using StackExchange.Redis;
using Xunit;

namespace Autheris.Tests.Unit;

public class MemoryPackAndGarnetTests
{
    [Fact]
    public void MemoryPackCacheSerializer_SerializesAndDeserializes_CachedConsentEnvelope()
    {
        var serializer = new MemoryPackCacheSerializer();

        var table = new TableIdentifier("sales", "crm", "customers");
        var colMap = new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = ColumnAccessLevel.Clear,
            ["email"] = ColumnAccessLevel.Mask,
            ["ssn"] = ColumnAccessLevel.Deny
        };

        var decision = TableAccessDecision.Allowed(table, colMap, "tenant_id = 42", hasUnconstrainedColumnAllow: false);
        var envelope = CachedConsentEnvelope.FromDecision(decision, epoch: 105);

        // Serialize
        byte[] bytes = serializer.Serialize(envelope);
        bytes.ShouldNotBeNull();
        bytes.Length.ShouldBeGreaterThan(0);
        bytes[0].ShouldBe((byte)0x4D); // 'M' magic prefix for MemoryPack

        // Deserialize
        bool ok = serializer.TryDeserialize<CachedConsentEnvelope>(bytes, out var deserialized);
        ok.ShouldBeTrue();
        deserialized.ShouldNotBeNull();
        deserialized.Epoch.ShouldBe(105);
        deserialized.IsAllowed.ShouldBeTrue();
        deserialized.RowFilterSql.ShouldBe("tenant_id = 42");

        // Convert back to TableAccessDecision
        var restoredDecision = deserialized.ToDecision();
        restoredDecision.Table.Domain.ShouldBe("sales");
        restoredDecision.Table.Schema.ShouldBe("crm");
        restoredDecision.Table.TableName.ShouldBe("customers");
        restoredDecision.GetColumnAccess("id").ShouldBe(ColumnAccessLevel.Clear);
        restoredDecision.GetColumnAccess("email").ShouldBe(ColumnAccessLevel.Mask);
        restoredDecision.GetColumnAccess("ssn").ShouldBe(ColumnAccessLevel.Deny);
    }

    [Fact]
    public void MemoryPackCacheSerializer_FallbackToJson_ForArbitraryPoco()
    {
        var serializer = new MemoryPackCacheSerializer();
        var testObj = new NonMemoryPackPoco { Name = "Autheris", Value = 42 };

        byte[] bytes = serializer.Serialize(testObj);
        bytes.ShouldNotBeNull();
        bytes[0].ShouldBe((byte)0x4A); // 'J' magic prefix for JSON fallback

        bool ok = serializer.TryDeserialize<NonMemoryPackPoco>(bytes, out var deserialized);
        ok.ShouldBeTrue();
        deserialized.ShouldNotBeNull();
        deserialized.Name.ShouldBe("Autheris");
        deserialized.Value.ShouldBe(42);
    }

    [Fact]
    public async Task GarnetServerManager_StartsServerAndAllowsRedisOperations()
    {
        int port = 3280;
        var gatewayOptions = new GatewayOptions
        {
            Caching = new CachingOptions
            {
                Garnet = new GarnetOptions
                {
                    EnableEmbeddedServer = true,
                    Host = "127.0.0.1",
                    Port = port,
                    DisableConsole = true
                }
            }
        };

        using var manager = new GarnetServerManager(Options.Create(gatewayOptions));
        manager.StartServer();
        manager.IsRunning.ShouldBeTrue();

        try
        {
            var config = new ConfigurationOptions
            {
                EndPoints = { $"127.0.0.1:{port}" },
                Password = manager.ClientPassword, // SEC H-01: embedded Garnet requires authentication
                AbortOnConnectFail = false,
                ConnectTimeout = 2000,
                SyncTimeout = 1000
            };

            using var redis = await ConnectionMultiplexer.ConnectAsync(config);
            var db = redis.GetDatabase();

            bool setOk = await db.StringSetAsync("garnet:integration:key", "high-performance-c#");
            setOk.ShouldBeTrue();

            var val = await db.StringGetAsync("garnet:integration:key");
            val.ToString().ShouldBe("high-performance-c#");
        }
        finally
        {
            manager.StopServer();
            manager.IsRunning.ShouldBeFalse();
        }
    }

    [Fact]
    public async Task ConsentCacheService_TwoTierCaching_WithGarnetAndMemoryPack()
    {
        int port = 3281;
        var gatewayOptions = new GatewayOptions
        {
            Caching = new CachingOptions
            {
                Garnet = new GarnetOptions
                {
                    EnableEmbeddedServer = true,
                    Host = "127.0.0.1",
                    Port = port,
                    DisableConsole = true
                },
                Redis = new RedisOptions
                {
                    InstanceName = "TestGarnet:"
                }
            }
        };

        using var manager = new GarnetServerManager(Options.Create(gatewayOptions));
        manager.StartServer();

        try
        {
            var config = new ConfigurationOptions
            {
                EndPoints = { $"127.0.0.1:{port}" },
                Password = manager.ClientPassword, // SEC H-01: embedded Garnet requires authentication
                AbortOnConnectFail = false,
                ConnectTimeout = 2000,
                SyncTimeout = 1000
            };

            using var redis = await ConnectionMultiplexer.ConnectAsync(config);
            var serializer = new MemoryPackCacheSerializer();
            var memoryCache = new MemoryCache(new MemoryCacheOptions());
            var epochService = Substitute.For<IEpochValidationService>();
            var eventBus = Substitute.For<IEventBus>();

            var table = new TableIdentifier("corp", "hr", "employees");
            epochService.GetCurrentEpochAsync(table, Arg.Any<CancellationToken>()).Returns(1);
            epochService.IsEpochValidAsync(table, 1, Arg.Any<CancellationToken>()).Returns(true);

            using var consentCache = new ConsentCacheService(
                memoryCache,
                epochService,
                eventBus,
                Options.Create(gatewayOptions),
                serializer,
                redis);

            var userSid = new Sid("S-1-5-21-999");
            var colMap = new Dictionary<string, ColumnAccessLevel>
            {
                ["salary"] = ColumnAccessLevel.Mask
            };
            var decision = TableAccessDecision.Allowed(table, colMap);

            // 1. Cache Miss initially
            var cached = await consentCache.GetCachedDecisionAsync(userSid, table);
            cached.ShouldBeNull();

            // 2. Set Decision (stores in L1 MemoryCache + L2 Garnet via MemoryPack)
            await consentCache.SetCachedDecisionAsync(userSid, table, decision, TimeSpan.FromMinutes(10));

            // 3. Cache Hit in L1
            var hit1 = await consentCache.GetCachedDecisionAsync(userSid, table);
            hit1.ShouldNotBeNull();
            hit1.GetColumnAccess("salary").ShouldBe(ColumnAccessLevel.Mask);

            // 4. Simulate L1 Eviction / Clear L1 Cache
            await consentCache.ClearL1CacheAsync();

            // 5. Cache Hit via L2 (Garnet) with MemoryPack binary deserialization and repopulation into L1
            var hitL2 = await consentCache.GetCachedDecisionAsync(userSid, table);
            hitL2.ShouldNotBeNull();
            hitL2.Table.Schema.ShouldBe("hr");
            hitL2.Table.TableName.ShouldBe("employees");
            hitL2.GetColumnAccess("salary").ShouldBe(ColumnAccessLevel.Mask);
        }
        finally
        {
            manager.StopServer();
        }
    }

    public class NonMemoryPackPoco
    {
        public string Name { get; set; } = string.Empty;
        public int Value { get; set; }
    }
}
