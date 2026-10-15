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
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.GraphQL.Subscriptions;
using Autheris.Infrastructure.Streaming;
using Autheris.Tests.Unit.TestDoubles;
using HotChocolate;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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

    [Fact]
    public async Task PublishAsync_WhenConnected_PublishesToLocalAndRedisChannel()
    {
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        var subscriber = Substitute.For<ISubscriber>();
        multiplexer.IsConnected.Returns(true);
        multiplexer.GetSubscriber(Arg.Any<object?>()).Returns(subscriber);

        var inner = new InMemoryCdcEventChannel(NullLogger<InMemoryCdcEventChannel>.Instance);
        await using var channel = new RedisCdcEventChannel(
            multiplexer,
            inner,
            NullLogger<RedisCdcEventChannel>.Instance,
            instanceId: "node-a");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var enumerator = channel.SubscribeAsync("cdc_orders", cts.Token).GetAsyncEnumerator(cts.Token);

        var ev = CreateEvent("evt-local-1");
        await channel.PublishAsync(ev, cts.Token);

        (await enumerator.MoveNextAsync()).ShouldBeTrue();
        enumerator.Current.EventId.ShouldBe("evt-local-1");

        await subscriber.Received(1).PublishAsync(
            RedisChannel.Literal(RedisCdcEventChannel.PubSubChannelName),
            Arg.Is<RedisValue>(v => v.ToString().Contains("\"node-a\"") && v.ToString().Contains("\"evt-local-1\"")),
            CommandFlags.None);
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

        var inner = new InMemoryCdcEventChannel(NullLogger<InMemoryCdcEventChannel>.Instance);
        await using var channel = new RedisCdcEventChannel(
            multiplexer,
            inner,
            NullLogger<RedisCdcEventChannel>.Instance,
            instanceId: "node-a");

        capturedHandler.ShouldNotBeNull();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var enumerator = channel.SubscribeAsync("cdc_orders", cts.Token).GetAsyncEnumerator(cts.Token);

        var remoteEnvelope = new RedisCdcEventChannel.RedisCdcEnvelope("node-b", CreateEvent("evt-remote-1"));
        var json = JsonSerializer.Serialize(remoteEnvelope);
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

        var inner = new InMemoryCdcEventChannel(NullLogger<InMemoryCdcEventChannel>.Instance);
        await using var channel = new RedisCdcEventChannel(
            multiplexer,
            inner,
            NullLogger<RedisCdcEventChannel>.Instance,
            instanceId: "node-a");

        capturedHandler.ShouldNotBeNull();

        // Simulate Redis echoing back an event with the same instanceId ("node-a") but a new EventId
        // that was not yet seen by the local subscriber; if instanceId filtering works, it will never be enqueued.
        var echoedEnvelope = new RedisCdcEventChannel.RedisCdcEnvelope("node-a", CreateEvent("evt-echoed"));
        capturedHandler!(RedisChannel.Literal(RedisCdcEventChannel.PubSubChannelName), JsonSerializer.Serialize(echoedEnvelope));

        // Follow with a valid remote event from node-b so MoveNextAsync completes deterministically.
        var remoteEnvelope = new RedisCdcEventChannel.RedisCdcEnvelope("node-b", CreateEvent("evt-from-b"));
        capturedHandler!(RedisChannel.Literal(RedisCdcEventChannel.PubSubChannelName), JsonSerializer.Serialize(remoteEnvelope));

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

        var inner = new InMemoryCdcEventChannel(NullLogger<InMemoryCdcEventChannel>.Instance);
        await using var channel = new RedisCdcEventChannel(
            multiplexer,
            inner,
            NullLogger<RedisCdcEventChannel>.Instance);

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
            GraphQL = new GraphQLGatewayOptions { MaxSubscriptionsPerSubject = 2 }
        });
        var clusterState = Substitute.For<IDistributedClusterStateProvider>();
        var topology = new ClusterStateTopology(ClusterStateMode.External);
        var governor = new CdcSubscriptionGovernor(
            options,
            NullLogger<CdcSubscriptionGovernor>.Instance,
            clusterState,
            topology);

        var resolver = Substitute.For<ITableAccessResolver>();
        resolver.ResolveTableAccessAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<TableIdentifier>(), null, null, Arg.Any<CancellationToken>())
            .Returns(new ResolvedTableAccess(
                new TableMetadata { Identifier = new TableIdentifier("default", "public", "orders") },
                new PolicyAccessDecision(true, null, false, new Dictionary<string, ColumnAccessResult>()),
                Array.Empty<RowFilterCondition>(),
                null));

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sid", "S-1-5-21-100"),
            new Claim("tenant_id", "tenant-a")
        ], "test"));

        clusterState.IncrementAsync("cdc:subs:tenant-a", 1, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult<long?>(1L));
        clusterState.IncrementAsync("cdc:subs:tenant-a", -1, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult<long?>(0L));

        var lease = await governor.AdmitAsync(
            principal,
            "cdc_orders",
            new TableIdentifier("default", "public", "orders"),
            resolver,
            NullAuditLogRepository.Instance,
            CancellationToken.None);

        await lease.DisposeAsync();

        await clusterState.Received(1).IncrementAsync("cdc:subs:tenant-a", 1, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        await clusterState.Received(1).IncrementAsync("cdc:subs:tenant-a", -1, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());

        // Now simulate ClusterStateUnavailableException -> fail closed
        clusterState.IncrementAsync("cdc:subs:tenant-a", 1, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns<ValueTask<long?>>(_ => throw new ClusterStateUnavailableException("increment", "cdc:subs:tenant-a", new InvalidOperationException("redis down")));

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
        clusterServices.AddAutherisStreaming();

        using var clusterProvider = clusterServices.BuildServiceProvider();
        clusterProvider.GetRequiredService<ICdcEventChannel>().ShouldBeOfType<RedisCdcEventChannel>();

        var localServices = new ServiceCollection();
        localServices.AddLogging();
        localServices.AddSingleton(Options.Create(new GatewayOptions()));
        localServices.AddSingleton<IClusterStateTopology>(new ClusterStateTopology(ClusterStateMode.InMemory));
        localServices.AddAutherisStreaming();

        using var localProvider = localServices.BuildServiceProvider();
        localProvider.GetRequiredService<ICdcEventChannel>().ShouldBeOfType<InMemoryCdcEventChannel>();
    }
}
