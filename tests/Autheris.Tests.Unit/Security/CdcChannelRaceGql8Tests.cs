namespace Autheris.Tests.Unit.Security;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Streaming;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

/// <summary>
/// GQL-8: when the last subscriber of a topic leaves while a new one joins, the new subscriber must not end up in the
/// removed topic dictionary (it would never receive an event).
/// </summary>
public sealed class CdcChannelRaceGql8Tests
{
    private static CdcEvent Event(string id) =>
        new(id, new TableIdentifier("default", "public", "orders"), CdcOperation.Insert, "tenant-a", null, null, DateTimeOffset.UtcNow);

    [Fact]
    public async Task JoinWhileLastSubscriberLeaves_NewSubscriberStillReceivesEvents()
    {
        for (var round = 0; round < 200; round++)
        {
            var channel = new InMemoryCdcEventChannel(NullLogger<InMemoryCdcEventChannel>.Instance);
            const string topic = "cdc_orders";

            using var leavingCts = new CancellationTokenSource();
            var leaving = channel.SubscribeAsync(topic, leavingCts.Token).GetAsyncEnumerator(leavingCts.Token);
            var leavingMove = leaving.MoveNextAsync().AsTask();
            await Task.Yield();

            using var joiningCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var joining = channel.SubscribeAsync(topic, joiningCts.Token).GetAsyncEnumerator(joiningCts.Token);

            // Leave and join concurrently.
            var join = Task.Run(() => joining.MoveNextAsync().AsTask());
            leavingCts.Cancel();
            try { await leavingMove; } catch (OperationCanceledException) { }
            await leaving.DisposeAsync();

            await Task.Delay(1);
            await channel.PublishAsync(Event($"e{round}"));

            (await join).ShouldBeTrue($"round {round}: the joining subscriber missed the event");
            await joining.DisposeAsync();
        }
    }
}
