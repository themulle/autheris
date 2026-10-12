namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Interfaces;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Application.Security.Rebac.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Diagnostics;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Messaging;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class RebacDistributedStateTests
{
    private sealed class DroppingEventBus : IEventBus
    {
        private readonly ConcurrentDictionary<string, long> _counters = new(StringComparer.OrdinalIgnoreCase);

#pragma warning disable CS0067
        public event Action? ConnectionRestored;
#pragma warning restore CS0067

        public Task PublishAsync<T>(string channel, T message, CancellationToken ct = default)
        {
            // Intentionally drops all pub/sub messages to simulate network partition or dropped packet
            return Task.CompletedTask;
        }

        public IDisposable Subscribe<T>(string channel, Func<T, Task> handler)
        {
            return new DummyDisposable();
        }

        public Task<long> IncrementCounterAsync(string key, CancellationToken ct = default)
        {
            var val = _counters.AddOrUpdate(key, 1, (_, c) => c + 1);
            return Task.FromResult(val);
        }

        public Task<long> GetCounterAsync(string key, CancellationToken ct = default)
        {
            var val = _counters.TryGetValue(key, out var c) ? c : 0L;
            return Task.FromResult(val);
        }

        private sealed class DummyDisposable : IDisposable
        {
            public void Dispose() { }
        }
    }

    [Fact]
    public async Task LostInvalidationMessage_PeerStillReevaluates()
    {
        // Setup: DroppingEventBus drops all Pub/Sub messages, but monotonic counter works
        var bus = new DroppingEventBus();
        var store = new InMemoryRebacStore(NullLogger<InMemoryRebacStore>.Instance);

        // GenerationCacheMilliseconds = 0 ensures immediate pull-validation against the broker counter
        var options = Options.Create(new GatewayOptions
        {
            Rebac = new RebacOptions
            {
                Enabled = true,
                CacheTtlSeconds = 3600,
                GenerationCacheMilliseconds = 0
            }
        });

        var replicaA = new ZanzibarRebacEvaluator(store, options, NullLogger<ZanzibarRebacEvaluator>.Instance, bus);
        var replicaB = new ZanzibarRebacEvaluator(store, options, NullLogger<ZanzibarRebacEvaluator>.Instance, bus);

        var tuple = new RebacTuple("tenant-alpha", "user:alice", "viewer", "doc:100");
        await store.AddTupleAsync(tuple);

        var check = new RebacCheckRequest("tenant-alpha", "user:alice", "viewer", "doc:100");

        // 1. Replica B evaluates and caches ALLOW
        var result1 = await replicaB.CheckAsync(check);
        result1.Allowed.ShouldBeTrue();

        // 2. Revoke tuple in authoritative store and trigger invalidation on Replica A
        await store.DeleteTupleAsync(tuple);
        await replicaA.InvalidateTenantCacheAsync("tenant-alpha");

        // 3. Replica B receives NO pub/sub message (dropped by bus), but pull-validates generation counter!
        var result2 = await replicaB.CheckAsync(check);
        result2.Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task GenerationBumpFailure_PropagatesException_AndDisablesTenantCache()
    {
        var bus = Substitute.For<IEventBus>();
        bus.IncrementCounterAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<long>>(_ => throw new InvalidOperationException("Redis unavailable"));

        var store = new InMemoryRebacStore(NullLogger<InMemoryRebacStore>.Instance);
        var options = Options.Create(new GatewayOptions
        {
            Rebac = new RebacOptions { Enabled = true, CacheTtlSeconds = 3600 }
        });

        var evaluator = new ZanzibarRebacEvaluator(store, options, NullLogger<ZanzibarRebacEvaluator>.Instance, bus);

        var tuple = new RebacTuple("tenant-degraded", "user:bob", "editor", "folder:200");
        await store.AddTupleAsync(tuple);

        // Invalidation must fail-closed: propagate exception, clear cache, mark tenant degraded
        await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await evaluator.InvalidateTenantCacheAsync("tenant-degraded");
        });

        // Mutate store directly to confirm evaluator bypasses decision cache
        await store.DeleteTupleAsync(tuple);

        var check = new RebacCheckRequest("tenant-degraded", "user:bob", "editor", "folder:200");
        var res = await evaluator.CheckAsync(check);
        res.Allowed.ShouldBeFalse(); // Must re-evaluate directly against store without serving stale cache
    }

    [Fact]
    public async Task InProcessBus_UnderSaturation_DoesNotDropInvalidationChannel()
    {
        long droppedCount = 0;
        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Name == "autheris_eventbus_dropped_total")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
        {
            Interlocked.Add(ref droppedCount, measurement);
        });
        meterListener.Start();
        meterListener.EnableMeasurementEvents(GatewayDiagnostics.EventBusDroppedCounter);

        // Bus with capacity 1
        await using var bus = new InProcessChannelEventBus(capacity: 1);

        string? receivedTenant = null;
        using var invalidationSub = bus.Subscribe<string>(ZanzibarRebacEvaluator.InvalidationChannel, tenant =>
        {
            receivedTenant = tenant;
            return Task.CompletedTask;
        });

        // Publish 10,000 telemetry events concurrently to saturate channel capacity 1
        var telemetryTasks = new Task[10_000];
        for (int i = 0; i < 10_000; i++)
        {
            telemetryTasks[i] = bus.PublishAsync("telemetry:events", $"event-{i}");
        }
        await Task.WhenAll(telemetryTasks);

        // Publish 1 invalidation event
        await bus.PublishAsync(ZanzibarRebacEvaluator.InvalidationChannel, "tenant-critical");

        // The invalidation event must be received without being dropped
        receivedTenant.ShouldBe("tenant-critical");

        // The telemetry drop counter and metric must have recorded drops
        bus.DroppedCount.ShouldBeGreaterThan(0);
        droppedCount.ShouldBeGreaterThan(0);
    }
}
