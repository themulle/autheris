using System.Runtime.CompilerServices;
using Autheris.Application.State;
using Autheris.Application.Streaming.Crypto;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Application.Streaming.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.CdcStream;

/// <summary>REQ-CDC-99 I1-C: compatibility feed (D-A13) and merged GraphQL source. SEC-CDC99-01, -05, -18, -19.</summary>
public sealed class CdcStreamCompatFeedTests
{
    private static readonly CdcStreamId Id = new("0123456789abcdef0123456789abcdef");
    private static readonly TableIdentifier Table = new("finance", "dbo", "invoices");
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(20);
    private static readonly DateTimeOffset Produced = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private static CdcStreamDelivery Change(
        long offset, CdcOperation op, string? tenant, CdcTenantScope scope = CdcTenantScope.Row, string? prior = null,
        IReadOnlyDictionary<string, object?>? before = null, IReadOnlyDictionary<string, object?>? after = null) =>
        new(new CdcStreamEventRecord(Id, offset, 99_000 + offset, CdcStreamEventKind.Change, op, scope, tenant, prior, new CdcProtectedBlob("d", []), Produced),
            new CdcChangePayload(new Dictionary<string, object?> { ["Id"] = 1 }, before, after, false, false), null, Table);

    private static Dictionary<string, object?> Row(string v) => new() { ["Amount"] = v };

    [Fact]
    public void Map_ProducesOpaqueEventId_AndNoVersionOrKeyBearingMetadata()
    {
        var evt = CdcStreamCompatFeed.Map(Change(7, CdcOperation.Insert, "acme", after: Row("1"))).Single();
        evt.EventId.ShouldBe(CdcEventId.Format(Id, 7));
        System.Text.RegularExpressions.Regex.IsMatch(evt.EventId, "^evt_[0-9a-f]{32}_[0-9]+$").ShouldBeTrue();
        evt.Metadata.ShouldBeNull();
        evt.EventId.ShouldNotContain("99007");
        evt.TenantId.ShouldBe("acme");
        evt.Timestamp.ShouldBe(Produced);
        evt.Table.ShouldBe(Table);
    }

    [Theory]
    [InlineData(CdcTenantScope.Shared, "acme")]
    [InlineData(CdcTenantScope.Unresolved, "acme")]
    [InlineData(CdcTenantScope.Row, null)]
    [InlineData(CdcTenantScope.Row, "")]
    public void Map_NeverFansOutUntenantedOrUnresolvedChanges(CdcTenantScope scope, string? tenant) =>
        CdcStreamCompatFeed.Map(Change(1, CdcOperation.Insert, tenant, scope, after: Row("1"))).ShouldBeEmpty();

    [Fact]
    public void Map_ControlAndGapDeliveries_MapToNothing()
    {
        var control = new CdcStreamDelivery(
            new CdcStreamEventRecord(Id, 1, 1, CdcStreamEventKind.Resync, null, CdcTenantScope.Shared, null, null, new CdcProtectedBlob("d", []), Produced),
            null, new CdcControlPayload("resync", null, null, null), Table);
        CdcStreamCompatFeed.Map(control).ShouldBeEmpty();
        CdcStreamCompatFeed.Map(CdcStreamTailHubGap()).ShouldBeEmpty();
    }

    private static CdcStreamDelivery CdcStreamTailHubGap() =>
        new(new CdcStreamEventRecord(Id, 3, 0, CdcStreamEventKind.Gap, null, CdcTenantScope.Unresolved, null, null, new CdcProtectedBlob("", []), Produced),
            null, new CdcControlPayload("retention", 1, 4, null), Table);

    /// <summary>SEC-CDC99-01: a row moving tenants never exposes the old tenant's before-image to the new tenant.</summary>
    [Fact]
    public void Map_TenantMove_InsertForNewTenantWithoutBefore_DeleteForOldTenant()
    {
        var events = CdcStreamCompatFeed.Map(Change(5, CdcOperation.Update, "globex", prior: "acme", before: Row("old-acme"), after: Row("new")));
        events.Count.ShouldBe(2);

        var forNew = events.Single(e => e.TenantId == "globex");
        forNew.Operation.ShouldBe(CdcOperation.Insert);
        forNew.Before.ShouldBeNull();
        forNew.After!["Amount"].ShouldBe("new");

        var forOld = events.Single(e => e.TenantId == "acme");
        forOld.Operation.ShouldBe(CdcOperation.Delete);
        forOld.After.ShouldBeNull();
        forOld.Before!["Amount"].ShouldBe("old-acme");
    }

    [Fact]
    public void Map_SameTenantUpdate_KeepsBeforeAndAfter()
    {
        var e = CdcStreamCompatFeed.Map(Change(5, CdcOperation.Update, "acme", before: Row("a"), after: Row("b"))).Single();
        e.Operation.ShouldBe(CdcOperation.Update);
        e.Before!["Amount"].ShouldBe("a");
    }

    private sealed class FakeHub(Dictionary<string, List<CdcStreamDelivery>> deliveries) : ICdcStreamTailHub
    {
        public List<string> Attached { get; } = [];

        public async IAsyncEnumerable<CdcStreamDelivery> AttachAsync(CdcStreamId id, long? fromOffset, [EnumeratorCancellation] CancellationToken ct)
        {
            Attached.Add(id.Value);
            foreach (var d in deliveries.GetValueOrDefault(id.Value) ?? [])
            {
                yield return d;
            }

            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
        }
    }

    private static (CdcStreamCompatFeed Feed, FakeHub Hub, FakeTimeProvider Time, IDistributedClusterStateProvider State, CancellationTokenSource LeaseLost)
        Build(bool lease = true, params CdcStreamDescriptor[] streams)
    {
        var time = new FakeTimeProvider(Produced);
        var store = Substitute.For<ICdcStreamStore>();
        store.ListStreamsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<CdcStreamDescriptor>>(streams));
        var hub = new FakeHub(new() { [Id.Value] = [Change(1, CdcOperation.Insert, "acme", after: Row("x"))] });
        var lost = new CancellationTokenSource();
        var state = Substitute.For<IDistributedClusterStateProvider>();
        var l = Substitute.For<IDistributedLease>();
        l.LeaseLost.Returns(lost.Token);
        l.Token.Returns(5);
        state.TryAcquireLeaseAsync(CdcStreamCompatFeed.LeaseKey, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(_ => ValueTask.FromResult<IDistributedLease?>(lease ? l : null));
        var sp = new ServiceCollection().AddSingleton(store).AddSingleton<ICdcStreamTailHub>(hub).AddSingleton(state).BuildServiceProvider();
        return (new CdcStreamCompatFeed(sp, time, NullLogger<CdcStreamCompatFeed>.Instance), hub, time, state, lost);
    }

    private static CdcStreamDescriptor Stream(CdcStreamStatus status = CdcStreamStatus.Active, string source = "mssql_ct") =>
        new(Id, source, Table, status, null, 1, 1, null, null);

    [Fact]
    public async Task Feed_RunsOnlyOnTheLeaseHolder_AndForwardsMappedEvents()
    {
        var (feed, hub, _, _, _) = Build(true, Stream());
        var got = new TaskCompletionSource<CdcEvent>();
        using var cts = new CancellationTokenSource();
        var run = feed.RunAsync((e, _) => { got.TrySetResult(e); return Task.CompletedTask; }, cts.Token);

        var evt = await got.Task.WaitAsync(Guard);
        evt.EventId.ShouldBe(CdcEventId.Format(Id, 1));
        hub.Attached.ShouldBe([Id.Value]);
        await cts.CancelAsync();
        await run.WaitAsync(Guard);
    }

    [Fact]
    public async Task Feed_WithoutTheLease_NeverAttaches()
    {
        var (feed, hub, time, state, _) = Build(false, Stream());
        using var cts = new CancellationTokenSource();
        var run = feed.RunAsync((_, _) => Task.CompletedTask, cts.Token);
        for (var i = 0; i < 5; i++)
        {
            time.Advance(TimeSpan.FromSeconds(5));
            await Task.Yield();
        }

        hub.Attached.ShouldBeEmpty();
        await cts.CancelAsync();
        await run.WaitAsync(Guard);
        await state.Received().TryAcquireLeaseAsync(CdcStreamCompatFeed.LeaseKey, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(CdcStreamStatus.Halted, "mssql_ct")]
    [InlineData(CdcStreamStatus.Disabled, "mssql_ct")]
    [InlineData(CdcStreamStatus.ActivationFailed, "mssql_ct")]
    [InlineData(CdcStreamStatus.Active, "other")]
    public async Task Feed_SkipsInactiveAndForeignStreams(CdcStreamStatus status, string source)
    {
        var (feed, hub, time, _, _) = Build(true, Stream(status, source));
        using var cts = new CancellationTokenSource();
        var run = feed.RunAsync((_, _) => Task.CompletedTask, cts.Token);
        for (var i = 0; i < 20; i++)
        {
            time.Advance(TimeSpan.FromSeconds(10));
            await Task.Yield();
        }

        hub.Attached.ShouldBeEmpty();
        await cts.CancelAsync();
        await run.WaitAsync(Guard);
    }

    [Fact]
    public async Task Feed_WithoutStreamServices_IsInertAndCompletes()
    {
        var feed = new CdcStreamCompatFeed(new ServiceCollection().BuildServiceProvider(), TimeProvider.System, NullLogger<CdcStreamCompatFeed>.Instance);
        await feed.RunAsync((_, _) => Task.CompletedTask, CancellationToken.None).WaitAsync(Guard);
    }

    [Theory]
    [InlineData("cdc_all", true)]
    [InlineData("cdc_dbo.invoices", true)]
    [InlineData("cdc_invoices", true)]
    [InlineData("cdc_dbo.orders", false)]
    [InlineData("cdc_orders", false)]
    public void TopicMatching_FollowsTheGovernorTopics(string topic, bool expected) =>
        CdcStreamEventSource.Matches(Table, topic, "cdc_all").ShouldBe(expected);

    [Fact]
    public async Task Merge_CombinesChannelAndStreamEvents_AndSurfacesResyncRequired()
    {
        var channelEvent = new CdcEvent("evt-ch", new TableIdentifier("d", "s", "other"), CdcOperation.Insert, "acme", null, Row("c"), Produced);
        async IAsyncEnumerable<CdcEvent> Channel([EnumeratorCancellation] CancellationToken ct)
        {
            yield return channelEvent;
            await Task.Delay(Timeout.Infinite, ct);
        }

        var hub = new FakeHub(new() { [Id.Value] = [Change(1, CdcOperation.Insert, "acme", after: Row("x"))] });
        var seen = new List<string>();
        using var cts = new CancellationTokenSource();
        await foreach (var e in CdcStreamEventSource.MergeAsync(Channel(cts.Token), [Id], hub, 10, cts.Token))
        {
            seen.Add(e.EventId);
            if (seen.Count == 2) break;
        }

        seen.ShouldBe(["evt-ch", CdcEventId.Format(Id, 1)], ignoreOrder: true);
        await cts.CancelAsync();

        var failing = Substitute.For<ICdcStreamTailHub>();
        failing.AttachAsync(Id, null, Arg.Any<CancellationToken>()).Returns(_ => Throwing());
        await Should.ThrowAsync<CdcResyncRequiredException>(async () =>
        {
            await foreach (var _ in CdcStreamEventSource.MergeAsync(Channel(CancellationToken.None), [Id], failing, 10, CancellationToken.None)) { }
        });
    }

    private static async IAsyncEnumerable<CdcStreamDelivery> Throwing()
    {
        await Task.Yield();
        throw new CdcResyncRequiredException(3);
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }
}
