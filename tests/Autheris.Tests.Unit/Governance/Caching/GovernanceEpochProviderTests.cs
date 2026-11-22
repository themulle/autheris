using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Autheris.Application.Governance.Caching;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Cache;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using StackExchange.Redis;
using Xunit;

namespace Autheris.Tests.Unit.Governance.Caching;

[Trait("Category", "Security")]
public sealed class GovernanceEpochProviderTests
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly TenantId TenantB = new("tenant-b");
    private static readonly DateTimeOffset T0 = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Epoch_is_cached_within_poll_interval_and_refetched_after_bound()
    {
        var time = new FakeTimeProvider(T0);
        var store = new FakeEpochStore();
        store.SetEpoch(TenantA, 1);
        using var provider = new GovernanceEpochProvider(
            store,
            Options.Create(new GovernanceCacheSettings { EpochPollSeconds = 2 }),
            timeProvider: time);

        var advanced = new List<string>();
        using var _ = provider.OnTenantEpochAdvanced(advanced.Add);

        (await provider.GetEpochAsync(TenantA)).ShouldBe(1L);
        store.GetCalls(TenantA).ShouldBe(1);

        time.Advance(TimeSpan.FromMilliseconds(1900));
        store.SetEpoch(TenantA, 2);
        (await provider.GetEpochAsync(TenantA)).ShouldBe(1L);
        store.GetCalls(TenantA).ShouldBe(1);
        advanced.ShouldBeEmpty();

        time.Advance(TimeSpan.FromMilliseconds(150));
        (await provider.GetEpochAsync(TenantA)).ShouldBe(2L);
        store.GetCalls(TenantA).ShouldBe(2);
        advanced.ShouldBe([TenantA.Value]);
    }

    [Fact]
    public async Task Store_failure_fails_closed_without_serving_stale_epoch()
    {
        var time = new FakeTimeProvider(T0);
        var store = new FakeEpochStore();
        store.SetEpoch(TenantA, 5);
        using var provider = new GovernanceEpochProvider(
            store,
            Options.Create(new GovernanceCacheSettings { EpochPollSeconds = 2 }),
            timeProvider: time);

        (await provider.GetEpochAsync(TenantA)).ShouldBe(5L);

        time.Advance(TimeSpan.FromSeconds(3));
        store.Failure = new InvalidOperationException("governance db unavailable");

        await Should.ThrowAsync<InvalidOperationException>(() => provider.GetEpochAsync(TenantA).AsTask());

        store.Failure = null;
        store.SetEpoch(TenantA, -1);
        await Should.ThrowAsync<InvalidOperationException>(() => provider.GetEpochAsync(TenantA).AsTask());

        await Should.ThrowAsync<ArgumentException>(() => provider.GetEpochAsync(default).AsTask());
        await Should.ThrowAsync<ArgumentException>(() => provider.GetEpochAsync(TenantId.Platform).AsTask());
    }

    [Fact]
    public async Task Change_notification_evicts_tenant_epoch_immediately_before_poll_interval()
    {
        var time = new FakeTimeProvider(T0);
        var store = new FakeEpochStore();
        store.SetEpoch(TenantA, 1);
        store.SetEpoch(TenantB, 10);
        using var channel = new RedisGovernanceChangeChannel(multiplexer: null, timeProvider: time);
        using var provider = new GovernanceEpochProvider(
            store,
            Options.Create(new GovernanceCacheSettings { EpochPollSeconds = 30 }),
            channel,
            time);

        var advanced = new List<string>();
        using var _ = provider.OnTenantEpochAdvanced(advanced.Add);

        (await provider.GetEpochAsync(TenantA)).ShouldBe(1L);
        (await provider.GetEpochAsync(TenantB)).ShouldBe(10L);

        time.Advance(TimeSpan.FromMilliseconds(50));
        store.SetEpoch(TenantA, 2);
        await channel.NotifyAsync(new GovernanceChangeEvent(TenantA.Value, 2, null, time.GetUtcNow()));

        advanced.ShouldContain(TenantA.Value);
        (await provider.GetEpochAsync(TenantA)).ShouldBe(2L);
        store.GetCalls(TenantA).ShouldBe(2);

        (await provider.GetEpochAsync(TenantB)).ShouldBe(10L);
        store.GetCalls(TenantB).ShouldBe(1);
    }

    [Fact]
    public async Task RedisGovernanceChangeChannel_corrupt_or_empty_payload_triggers_full_invalidation_and_hosted_service_fails_closed()
    {
        var time = new FakeTimeProvider(T0);
        var store = new FakeEpochStore();
        store.SetEpoch(TenantA, 1);
        store.SetEpoch(TenantB, 10);

        var redisStub = new FakeRedisPubSub();
        using var channel = new RedisGovernanceChangeChannel(redisStub.CreateMultiplexer(), timeProvider: time);
        using var provider = new GovernanceEpochProvider(
            store,
            Options.Create(new GovernanceCacheSettings { EpochPollSeconds = 60 }),
            timeProvider: time);
        var throwingTarget = new ThrowOnTenantInvalidator();
        using var hosted = new GovernanceCacheInvalidationHostedService(channel, [provider, throwingTarget]);
        await hosted.StartAsync(CancellationToken.None);

        (await provider.GetEpochAsync(TenantA)).ShouldBe(1L);
        (await provider.GetEpochAsync(TenantB)).ShouldBe(10L);

        store.SetEpoch(TenantA, 2);
        store.SetEpoch(TenantB, 11);

        // Publish pushes JSON payload on the Redis channel.
        var tableId = Guid.NewGuid();
        await channel.NotifyAsync(new GovernanceChangeEvent(TenantA.Value, 2, tableId, time.GetUtcNow()));
        redisStub.PublishedPayloads.Count.ShouldBe(1);
        var published = JsonSerializer.Deserialize<GovernanceChangeEvent>(redisStub.PublishedPayloads[0]);
        published.ShouldNotBeNull();
        published.TenantId.ShouldBe(TenantA.Value);
        published.TableId.ShouldBe(tableId);

        // Handler throwing on InvalidateTenant triggers fail-closed InvalidateAll fallback.
        throwingTarget.InvalidateAllCalls.ShouldBeGreaterThanOrEqualTo(1);
        (await provider.GetEpochAsync(TenantA)).ShouldBe(2L);

        // Corrupt / empty / unparseable Redis pub-sub payloads fail closed to full invalidation.
        foreach (var badPayload in new RedisValue[]
                 {
                     RedisValue.EmptyString,
                     "{corrupt-json",
                     "{\"TenantId\":\"invalid tenant!\",\"Epoch\":1,\"OccurredAtUtc\":\"2026-11-22T12:00:00Z\"}"
                 })
        {
            var priorCount = throwingTarget.InvalidateAllCalls;
            redisStub.Deliver(badPayload);
            throwingTarget.InvalidateAllCalls.ShouldBeGreaterThan(priorCount);
        }

        (await provider.GetEpochAsync(TenantB)).ShouldBe(11L);
        store.GetCalls(TenantB).ShouldBe(2);

        await hosted.StopAsync(CancellationToken.None);
    }

    private sealed class FakeEpochStore : IGovernanceEpochStore
    {
        private readonly ConcurrentDictionary<string, long> _epochs = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, int> _calls = new(StringComparer.Ordinal);

        public Exception? Failure { get; set; }

        public void SetEpoch(TenantId tenant, long epoch) => _epochs[tenant.Value] = epoch;
        public int GetCalls(TenantId tenant) => _calls.GetValueOrDefault(tenant.Value);

        public Task<long> GetEpochAsync(TenantId tenant, CancellationToken ct = default)
        {
            _calls.AddOrUpdate(tenant.Value, 1, static (_, c) => c + 1);
            if (Failure is { } ex)
            {
                return Task.FromException<long>(ex);
            }

            return Task.FromResult(_epochs.GetValueOrDefault(tenant.Value, 0L));
        }

        public Task<long> BumpEpochAsync(TenantId tenant, CancellationToken ct = default)
        {
            var next = _epochs.AddOrUpdate(tenant.Value, 1, static (_, e) => e + 1);
            return Task.FromResult(next);
        }
    }

    private sealed class ThrowOnTenantInvalidator : IGovernanceCacheInvalidator
    {
        public int InvalidateAllCalls { get; private set; }
        public void InvalidateTenant(string tenantId) => throw new InvalidOperationException("simulated handler fault");
        public void InvalidateAll() => InvalidateAllCalls++;
    }

    public sealed class FakeRedisPubSub
    {
        private Action<RedisChannel, RedisValue>? _handler;
        public List<string> PublishedPayloads { get; } = [];

        public IConnectionMultiplexer CreateMultiplexer()
        {
            var subscriber = DispatchProxy.Create<ISubscriber, SubscriberProxy>();
            ((SubscriberProxy)(object)subscriber).Owner = this;

            var mux = DispatchProxy.Create<IConnectionMultiplexer, MultiplexerProxy>();
            ((MultiplexerProxy)(object)mux).Subscriber = subscriber;
            return mux;
        }

        public void Deliver(RedisValue payload) =>
            _handler?.Invoke(RedisChannel.Literal(RedisGovernanceChangeChannel.ChannelName), payload);

        public class MultiplexerProxy : DispatchProxy
        {
            public ISubscriber Subscriber { get; set; } = null!;

            protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
            {
                "get_IsConnected" => true,
                nameof(IConnectionMultiplexer.GetSubscriber) => Subscriber,
                _ => null
            };
        }

        public class SubscriberProxy : DispatchProxy
        {
            public FakeRedisPubSub Owner { get; set; } = null!;

            protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            {
                switch (targetMethod?.Name)
                {
                    case nameof(ISubscriber.Subscribe):
                        Owner._handler = (Action<RedisChannel, RedisValue>?)args?[1];
                        return null;
                    case nameof(ISubscriber.Unsubscribe):
                        Owner._handler = null;
                        return null;
                    case nameof(ISubscriber.PublishAsync):
                        Owner.PublishedPayloads.Add(((RedisValue)args![1]!).ToString());
                        return Task.FromResult(1L);
                    default:
                        return null;
                }
            }
        }
    }
}
