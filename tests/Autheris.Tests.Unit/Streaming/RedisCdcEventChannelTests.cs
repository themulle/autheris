namespace Autheris.Tests.Unit.Streaming;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Application.Interfaces;
using Autheris.Application.State;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.GraphQL.Subscriptions;
using Autheris.Infrastructure.Streaming;
using Autheris.Tests.Unit.CdcStream;
using Autheris.Tests.Unit.TestDoubles;
using HotChocolate;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using StackExchange.Redis;
using Xunit;

public sealed class RedisCdcEventChannelTests
{
    private static CdcEvent CreateEvent(string id = "evt-1") =>
        new(
            id,
            new TableIdentifier("default", "public", "orders"),
            CdcOperation.Insert,
            "tenant-a",
            null,
            new Dictionary<string, object?> { ["id"] = 42L, ["status"] = "open" },
            DateTimeOffset.UtcNow);

    private const string KekId = "kek-1";

    private static (FakeKekProvider Keys, ReadOnlyMemory<byte> Kek) CreateKeys()
    {
        var keys = new FakeKekProvider().Add(KekId);
        keys.TryGetKek(KekId, out var kek).ShouldBeTrue();
        return (keys, kek);
    }

    private static string SealFrom(string instanceId, CdcEvent ev, ReadOnlyMemory<byte> kek, DateTimeOffset? at = null, string kekId = KekId) =>
        RedisCdcEventChannel.Seal(ev, instanceId, kekId, kek.Span, (at ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds());

    private static RedisCdcEventChannel CreateChannel(
        ICdcKeyEncryptionKeyProvider? keys,
        string instanceId = "node-a",
        TimeProvider? time = null,
        IConnectionMultiplexer? multiplexer = null)
    {
        if (multiplexer is null)
        {
            multiplexer = Substitute.For<IConnectionMultiplexer>();
            multiplexer.IsConnected.Returns(true);
            multiplexer.GetSubscriber(Arg.Any<object?>()).Returns(Substitute.For<ISubscriber>());
        }

        return new RedisCdcEventChannel(
            multiplexer,
            new InMemoryCdcEventChannel(NullLogger<InMemoryCdcEventChannel>.Instance),
            NullLogger<RedisCdcEventChannel>.Instance,
            keys,
            instanceId,
            time);
    }

    [Fact]
    public async Task PublishAsync_WhenConnected_PublishesToLocalAndEncryptedEnvelopeToRedis()
    {
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        var subscriber = Substitute.For<ISubscriber>();
        multiplexer.IsConnected.Returns(true);
        multiplexer.GetSubscriber(Arg.Any<object?>()).Returns(subscriber);
        RedisValue published = default;
        subscriber.PublishAsync(Arg.Any<RedisChannel>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>())
            .Returns(callInfo =>
            {
                published = callInfo.ArgAt<RedisValue>(1);
                return Task.FromResult(1L);
            });

        var (keys, _) = CreateKeys();
        await using var channel = CreateChannel(keys, "node-a", multiplexer: multiplexer);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var enumerator = channel.SubscribeAsync("cdc_orders", cts.Token).GetAsyncEnumerator(cts.Token);

        var ev = CreateEvent("evt-local-1");
        await channel.PublishAsync(ev, cts.Token);

        (await enumerator.MoveNextAsync()).ShouldBeTrue();
        enumerator.Current.EventId.ShouldBe("evt-local-1");

        await subscriber.Received(1).PublishAsync(
            RedisChannel.Literal(RedisCdcEventChannel.PubSubChannelName),
            Arg.Any<RedisValue>(),
            CommandFlags.None);

        // Nothing of the event (id, tenant, row values) is readable on the shared channel.
        var wire = published.ToString();
        wire.ShouldContain("\"iid\":\"node-a\"");
        wire.ShouldNotContain("evt-local-1");
        wire.ShouldNotContain("tenant-a");
        wire.ShouldNotContain("\"status\"");
        wire.ShouldNotContain("\"open\"");

        // Another node holding the same KEK authenticates and opens it.
        await using var nodeB = CreateChannel(keys, "node-b");
        nodeB.TryOpen(published, out var opened).ShouldBe(RedisCdcEventChannel.OpenOutcome.Accepted);
        opened!.EventId.ShouldBe("evt-local-1");
        opened.TenantId.ShouldBe("tenant-a");
    }

    [Fact]
    public void Constructor_WithoutUsableKek_RefusesToStart()
    {
        Should.Throw<InvalidOperationException>(() => CreateChannel(null));
        Should.Throw<InvalidOperationException>(() => CreateChannel(new FakeKekProvider()));
        Should.Throw<InvalidOperationException>(() => CreateChannel(new FakeKekProvider { ActiveKekId = "missing" }));
    }

    [Fact]
    public async Task TryOpen_RejectsPlaintextTamperedForeignKeyStaleAndReplayedEnvelopes()
    {
        var (keys, kek) = CreateKeys();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var channel = CreateChannel(keys, "node-a", time);
        var now = time.GetUtcNow();

        // Legacy plaintext envelope (no authentication) is never dispatched.
        var legacy = JsonSerializer.Serialize(new { InstanceId = "node-b", Event = CreateEvent("evt-plain") });
        channel.TryOpen(legacy, out _).ShouldBe(RedisCdcEventChannel.OpenOutcome.Malformed);

        // Tampered ciphertext
        var sealedEnvelope = SealFrom("node-b", CreateEvent("evt-1"), kek, now);
        var node = System.Text.Json.Nodes.JsonNode.Parse(sealedEnvelope)!;
        var ct = Convert.FromBase64String(node["c"]!.GetValue<string>());
        ct[0] ^= 0x01;
        node["c"] = Convert.ToBase64String(ct);
        channel.TryOpen(node.ToJsonString(), out _).ShouldBe(RedisCdcEventChannel.OpenOutcome.AuthenticationFailed);

        // Spoofed sender instance id (bound in the AAD)
        channel.TryOpen(sealedEnvelope.Replace("\"iid\":\"node-b\"", "\"iid\":\"node-c\"", StringComparison.Ordinal), out _)
            .ShouldBe(RedisCdcEventChannel.OpenOutcome.AuthenticationFailed);

        // Shifted timestamp (bound in the AAD)
        var shifted = System.Text.Json.Nodes.JsonNode.Parse(sealedEnvelope)!;
        shifted["ts"] = now.ToUnixTimeMilliseconds() + 1;
        channel.TryOpen(shifted.ToJsonString(), out _).ShouldBe(RedisCdcEventChannel.OpenOutcome.AuthenticationFailed);

        // Same KEK id, different key material
        var (_, foreignKek) = CreateKeys();
        channel.TryOpen(SealFrom("node-b", CreateEvent("evt-2"), foreignKek, now), out _).ShouldBe(RedisCdcEventChannel.OpenOutcome.AuthenticationFailed);

        // Unknown KEK id
        channel.TryOpen(SealFrom("node-b", CreateEvent("evt-3"), kek, now, kekId: "kek-unknown"), out _).ShouldBe(RedisCdcEventChannel.OpenOutcome.UnknownKey);

        // Too old / too far in the future
        channel.TryOpen(SealFrom("node-b", CreateEvent("evt-4"), kek, now - TimeSpan.FromMinutes(5)), out _).ShouldBe(RedisCdcEventChannel.OpenOutcome.Stale);
        channel.TryOpen(SealFrom("node-b", CreateEvent("evt-5"), kek, now + TimeSpan.FromMinutes(5)), out _).ShouldBe(RedisCdcEventChannel.OpenOutcome.Stale);

        // Valid once, replay rejected
        channel.TryOpen(sealedEnvelope, out var first).ShouldBe(RedisCdcEventChannel.OpenOutcome.Accepted);
        first!.EventId.ShouldBe("evt-1");
        channel.TryOpen(sealedEnvelope, out _).ShouldBe(RedisCdcEventChannel.OpenOutcome.Replayed);

        // Replay after the window has passed is stale
        time.Advance(RedisCdcEventChannel.MaxMessageAge + TimeSpan.FromSeconds(1));
        channel.TryOpen(sealedEnvelope, out _).ShouldBe(RedisCdcEventChannel.OpenOutcome.Stale);
    }

    [Fact]
    public async Task TryRegisterNonce_WhenCacheFull_EvictsOldestAndRejectsAtOrBelowWatermark()
    {
        var (keys, _) = CreateKeys();
        await using var channel = CreateChannel(keys);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        for (var i = 0; i < RedisCdcEventChannel.MaxSeenNonces; i++)
        {
            channel.TryRegisterNonce("n" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), now - 1000 + (i % 10), now).ShouldBeTrue();
        }

        // Full: the next insert evicts the oldest entry and raises the watermark to its timestamp.
        channel.TryRegisterNonce("fresh", now, now).ShouldBeTrue();
        channel.TryRegisterNonce("n0", now - 1000, now).ShouldBeFalse();
        channel.TryRegisterNonce("older-than-watermark", now - 2000, now).ShouldBeFalse();
        channel.TryRegisterNonce("fresh", now, now).ShouldBeFalse();
    }

    [Fact]
    public async Task SealAndOpen_PreservesClrTypesOfRowValues()
    {
        var (keys, kek) = CreateKeys();
        await using var channel = CreateChannel(keys, "node-a");
        var guid = Guid.NewGuid();
        var createdAt = new DateTime(2026, 10, 21, 8, 30, 15, DateTimeKind.Utc);
        var updatedAt = new DateTimeOffset(2026, 10, 21, 10, 0, 0, TimeSpan.FromHours(2));
        var ev = new CdcEvent(
            "evt-types",
            new TableIdentifier("default", "public", "orders"),
            CdcOperation.Update,
            "tenant-a",
            new Dictionary<string, object?> { ["amount"] = 1.10m },
            new Dictionary<string, object?>
            {
                ["id"] = 42L,
                ["amount"] = 12.3400m,
                ["created_at"] = createdAt,
                ["updated_at"] = updatedAt,
                ["uid"] = guid,
                ["blob"] = new byte[] { 1, 2, 3 },
                ["note"] = null
            },
            DateTimeOffset.UtcNow,
            new Dictionary<string, string> { ["source"] = "mssql_ct" });

        channel.TryOpen(SealFrom("node-b", ev, kek), out var opened).ShouldBe(RedisCdcEventChannel.OpenOutcome.Accepted);
        var after = opened!.After!;
        after["id"].ShouldBe(42L);
        after["amount"].ShouldBeOfType<decimal>().ShouldBe(12.3400m);
        after["created_at"].ShouldBeOfType<DateTime>().ShouldBe(createdAt);
        after["updated_at"].ShouldBeOfType<DateTimeOffset>().ShouldBe(updatedAt);
        after["uid"].ShouldBeOfType<Guid>().ShouldBe(guid);
        after["blob"].ShouldBeOfType<byte[]>().ShouldBe(new byte[] { 1, 2, 3 });
        after["note"].ShouldBeNull();
        after["AMOUNT"].ShouldBe(12.3400m);
        opened.Before!["amount"].ShouldBeOfType<decimal>();
        opened.Operation.ShouldBe(CdcOperation.Update);
        opened.Table.ShouldBe(ev.Table);
        opened.Metadata!["source"].ShouldBe("mssql_ct");
    }

    [Fact]
    public async Task RemoteRedisMessage_FromOtherInstance_IsForwardedToLocalSubscribers()
    {
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        var subscriber = Substitute.For<ISubscriber>();
        Action<RedisChannel, RedisValue>? capturedHandler = null;

        multiplexer.IsConnected.Returns(true);
        multiplexer.GetSubscriber(Arg.Any<object?>()).Returns(subscriber);
        subscriber.When(s => s.Subscribe(
                RedisChannel.Literal(RedisCdcEventChannel.PubSubChannelName),
                Arg.Any<Action<RedisChannel, RedisValue>>(),
                Arg.Any<CommandFlags>()))
            .Do(callInfo => capturedHandler = callInfo.ArgAt<Action<RedisChannel, RedisValue>>(1));

        var (keys, kek) = CreateKeys();
        await using var channel = CreateChannel(keys, "node-a", multiplexer: multiplexer);

        capturedHandler.ShouldNotBeNull();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var enumerator = channel.SubscribeAsync("cdc_orders", cts.Token).GetAsyncEnumerator(cts.Token);

        var json = SealFrom("node-b", CreateEvent("evt-remote-1"), kek);
        capturedHandler!(RedisChannel.Literal(RedisCdcEventChannel.PubSubChannelName), json);

        (await enumerator.MoveNextAsync()).ShouldBeTrue();
        enumerator.Current.EventId.ShouldBe("evt-remote-1");
        enumerator.Current.After.ShouldNotBeNull();
        enumerator.Current.After!["status"].ShouldBe("open");
    }

    [Fact]
    public async Task RemoteRedisMessage_FromSameInstance_IsIgnoredToPreventDoubleDelivery()
    {
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        var subscriber = Substitute.For<ISubscriber>();
        Action<RedisChannel, RedisValue>? capturedHandler = null;

        multiplexer.IsConnected.Returns(true);
        multiplexer.GetSubscriber(Arg.Any<object?>()).Returns(subscriber);
        subscriber.When(s => s.Subscribe(
                RedisChannel.Literal(RedisCdcEventChannel.PubSubChannelName),
                Arg.Any<Action<RedisChannel, RedisValue>>(),
                Arg.Any<CommandFlags>()))
            .Do(callInfo => capturedHandler = callInfo.ArgAt<Action<RedisChannel, RedisValue>>(1));

        var (keys, kek) = CreateKeys();
        await using var channel = CreateChannel(keys, "node-a", multiplexer: multiplexer);

        capturedHandler.ShouldNotBeNull();

        // Simulate Redis echoing back an event with the same instanceId ("node-a") but a new EventId
        // that was not yet seen by the local subscriber; if instanceId filtering works, it will never be enqueued.
        capturedHandler!(RedisChannel.Literal(RedisCdcEventChannel.PubSubChannelName), SealFrom("node-a", CreateEvent("evt-echoed"), kek));

        // An unauthenticated plaintext envelope from a "remote" node is dropped as well.
        capturedHandler!(RedisChannel.Literal(RedisCdcEventChannel.PubSubChannelName),
            JsonSerializer.Serialize(new { InstanceId = "node-x", Event = CreateEvent("evt-forged") }));

        // Follow with a valid remote event from node-b so MoveNextAsync completes deterministically.
        capturedHandler!(RedisChannel.Literal(RedisCdcEventChannel.PubSubChannelName), SealFrom("node-b", CreateEvent("evt-from-b"), kek));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var enumerator = channel.SubscribeAsync("cdc_orders", cts.Token).GetAsyncEnumerator(cts.Token);

        (await enumerator.MoveNextAsync()).ShouldBeTrue();
        enumerator.Current.EventId.ShouldBe("evt-from-b");
    }

    [Fact]
    public async Task PublishAsync_WhenRedisDisconnected_DeliversLocallyWithoutThrowing()
    {
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        var subscriber = Substitute.For<ISubscriber>();
        multiplexer.IsConnected.Returns(false);
        multiplexer.GetSubscriber(Arg.Any<object?>()).Returns(subscriber);

        await using var channel = CreateChannel(CreateKeys().Keys, "node-a", multiplexer: multiplexer);

        await channel.PublishAsync(CreateEvent("evt-disconnected"));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var enumerator = channel.SubscribeAsync("cdc_orders", cts.Token).GetAsyncEnumerator(cts.Token);
        (await enumerator.MoveNextAsync()).ShouldBeTrue();
        enumerator.Current.EventId.ShouldBe("evt-disconnected");

        await subscriber.DidNotReceiveWithAnyArgs().PublishAsync(default, default, default);
    }

    [Fact]
    public async Task CdcSubscriptionGovernor_ClusterWide_IncrementsAndDecrementsAndFailsClosedWhenUnavailable()
    {
        var options = Options.Create(new GatewayOptions
        {
            GraphQL = new GraphQLOptions { MaxSubscriptionsPerSubject = 2 }
        });
        var clusterState = Substitute.For<IDistributedClusterStateProvider>();
        var topology = new ClusterStateTopology(ClusterStateMode.External);
        var governor = new CdcSubscriptionGovernor(
            options,
            NullLogger<CdcSubscriptionGovernor>.Instance,
            clusterState,
            topology);

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-100"),
            new Claim("sid", "S-1-5-21-100"),
            new Claim("tenant_id", "tenant-a")
        ], "test"));

        var tableId = new TableIdentifier("default", "public", "orders");
        var resolver = Substitute.For<ITableAccessResolver>();
        resolver.ResolveTableAccessAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<TableIdentifier>(), null, null, Arg.Any<CancellationToken>())
            .Returns(new ResolvedTableAccess(
                new TableMetadata { Identifier = tableId },
                TableAccessDecision.Allowed(tableId, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true),
                new TenantId("tenant-a"),
                new Sid("S-1-5-21-100"),
                principal));

        clusterState.TryAcquireSlotAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<bool?>(true));
        clusterState.ReleaseSlotAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.CompletedTask);

        var lease = await governor.AdmitAsync(
            principal,
            "cdc_orders",
            new TableIdentifier("default", "public", "orders"),
            resolver,
            NullAuditLogRepository.Instance,
            CancellationToken.None);

        await lease.DisposeAsync();

        await clusterState.Received(2).TryAcquireSlotAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        await clusterState.Received(2).ReleaseSlotAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

        // Now simulate ClusterStateUnavailableException -> fail closed
        clusterState.TryAcquireSlotAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns<ValueTask<bool?>>(_ => throw new ClusterStateUnavailableException("slot", "cdc:subs:tenant-a", new InvalidOperationException("redis down")));

        var ex = await Should.ThrowAsync<GraphQLException>(() =>
            governor.AdmitAsync(
                principal,
                "cdc_orders",
                new TableIdentifier("default", "public", "orders"),
                resolver,
                NullAuditLogRepository.Instance,
                CancellationToken.None));
        ex.Errors[0].Code.ShouldBe("SERVICE_UNAVAILABLE");
    }

    [Fact]
    public void AddAutherisStreaming_ResolvesRedisChannelInClusterWideMode_AndInMemoryInSingleNodeMode()
    {
        var clusterServices = new ServiceCollection();
        clusterServices.AddLogging();
        clusterServices.AddSingleton(Options.Create(new GatewayOptions()));
        clusterServices.AddSingleton<IClusterStateTopology>(new ClusterStateTopology(ClusterStateMode.External));
        clusterServices.AddSingleton(Substitute.For<IConnectionMultiplexer>());
        clusterServices.AddSingleton<ICdcKeyEncryptionKeyProvider>(new FakeKekProvider().Add(KekId));
        clusterServices.AddAutherisStreaming();

        using var clusterProvider = clusterServices.BuildServiceProvider();
        clusterProvider.GetRequiredService<ICdcEventChannel>().ShouldBeOfType<RedisCdcEventChannel>();

        // Cluster-wide without a CDC KEK: the Redis channel refuses to start (no clear-text fallback).
        var unkeyedServices = new ServiceCollection();
        unkeyedServices.AddLogging();
        unkeyedServices.AddSingleton(Options.Create(new GatewayOptions()));
        unkeyedServices.AddSingleton<IClusterStateTopology>(new ClusterStateTopology(ClusterStateMode.External));
        unkeyedServices.AddSingleton(Substitute.For<IConnectionMultiplexer>());
        unkeyedServices.AddAutherisStreaming();

        using var unkeyedProvider = unkeyedServices.BuildServiceProvider();
        Should.Throw<InvalidOperationException>(() => unkeyedProvider.GetRequiredService<ICdcEventChannel>());

        var localServices = new ServiceCollection();
        localServices.AddLogging();
        localServices.AddSingleton(Options.Create(new GatewayOptions()));
        localServices.AddSingleton<IClusterStateTopology>(new ClusterStateTopology(ClusterStateMode.InMemory));
        localServices.AddAutherisStreaming();

        using var localProvider = localServices.BuildServiceProvider();
        localProvider.GetRequiredService<ICdcEventChannel>().ShouldBeOfType<InMemoryCdcEventChannel>();
    }
}
