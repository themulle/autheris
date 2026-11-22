namespace Autheris.Tests.Unit.Streaming;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Streaming.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Streaming;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

public sealed class InMemoryCdcEventChannelTests
{
    private static readonly TimeSpan GuardTimeout = TimeSpan.FromSeconds(5);

    private static CdcEvent CreateEvent(string id) =>
        new(id, new TableIdentifier("default", "public", "orders"), CdcOperation.Insert, "tenant-a", null, null, DateTimeOffset.UtcNow);

    [Fact]
    public async Task SubscribeAsync_DoesNotPrefillPreviouslyPublishedEvents()
    {
        var channel = new InMemoryCdcEventChannel(NullLogger<InMemoryCdcEventChannel>.Instance);

        for (int i = 0; i < 10; i++)
        {
            await channel.PublishAsync(CreateEvent($"old-{i}"));
        }

        using var cts = new CancellationTokenSource(GuardTimeout);
        await using var enumerator = channel.SubscribeAsync("cdc_orders", cts.Token).GetAsyncEnumerator(cts.Token);
        var nextTask = enumerator.MoveNextAsync().AsTask();

        // Give the subscriber loop a chance to drain any prefill if present before publishing the live event.
        await Task.Yield();
        await channel.PublishAsync(CreateEvent("live-1"), cts.Token);

        (await nextTask.WaitAsync(GuardTimeout)).ShouldBeTrue();
        enumerator.Current.EventId.ShouldBe("live-1");
    }

    [Fact]
    public async Task SubscribeAsync_WhenSlowSubscriberOverflows_ThrowsCdcResyncRequiredException_AndFastSubscriberContinues()
    {
        var channel = new InMemoryCdcEventChannel(NullLogger<InMemoryCdcEventChannel>.Instance);
        using var cts = new CancellationTokenSource(GuardTimeout);

        await using var slow = channel.SubscribeAsync("cdc_orders", cts.Token).GetAsyncEnumerator(cts.Token);
        await using var fast = channel.SubscribeAsync("cdc_orders", cts.Token).GetAsyncEnumerator(cts.Token);

        var fastReceived = new List<string>(1005);
        int published = 0;

        // Publish in batches while fast drains promptly and slow never reads, so only slow hits the 1,000-event buffer limit.
        foreach (var batchSize in new[] { 500, 500, 5 })
        {
            for (int i = 0; i < batchSize; i++)
            {
                await channel.PublishAsync(CreateEvent($"evt-{published++}"), cts.Token);
            }

            for (int i = 0; i < batchSize; i++)
            {
                (await fast.MoveNextAsync().AsTask().WaitAsync(GuardTimeout)).ShouldBeTrue();
                fastReceived.Add(fast.Current.EventId);
            }
        }

        fastReceived.Count.ShouldBe(1005);

        // Slow subscriber drains its buffered items and then must surface CdcResyncRequiredException rather than silently dropping events.
        var ex = await Should.ThrowAsync<CdcResyncRequiredException>(async () =>
        {
            while (await slow.MoveNextAsync().AsTask().WaitAsync(GuardTimeout)) { }
        });
        ex.Message.ShouldBe(CdcResyncRequiredException.Code);
    }
}
