namespace Autheris.Tests.Unit.Cdc;

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.State;
using Autheris.Application.Streaming;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Extensions.Cdc;
using Shouldly;
using Xunit;
using static CdcProducerHarness;

/// <summary>The producer appends complete change versions to the durable stream in one fenced transaction (PLAN-CDC-99 D-A4, D-A5).</summary>
public sealed class MssqlChangeTrackingPollerTests
{
    private static async Task<CdcProducerHarness> Seeded(CdcProducerHarness h, long watermark = 5)
    {
        await h.SeedWatermarkAsync(watermark);
        return h;
    }

    [Fact]
    public async Task FirstRun_StartsAtCurrentVersion_WithoutReplayingHistory()
    {
        var h = new CdcProducerHarness();
        h.Source.CurrentVersion = 100;
        h.Source.Changes.Add(Change(3, "I", 1));
        h.Source.Changes.Add(Change(50, "U", 2));

        var appended = await h.Poller.PollTableChangesAsync(TableId, 1);

        appended.ShouldBe(0);
        h.InnerStore.Only.Watermark.ShouldBe(100);
        h.InnerStore.Only.Head.ShouldBe(0);
        h.InnerStore.Only.Events.ShouldBeEmpty();
    }

    [Fact]
    public async Task Batch_IsAppendedWithDenseOffsets_ChangeVersionsAndWatermark()
    {
        var h = await Seeded(new CdcProducerHarness());
        h.Source.Changes.Add(Change(10, "I", 1));
        h.Source.Changes.Add(Change(11, "U", 2));
        h.Source.Changes.Add(Change(11, "D", 3));

        var appended = await h.Poller.PollTableChangesAsync(TableId, 1);

        appended.ShouldBe(3);
        var state = h.InnerStore.Only;
        state.Events.Select(e => e.Offset).ShouldBe([1L, 2, 3]);
        state.Events.Select(e => e.ChangeVersion).ShouldBe([10L, 11, 11]);
        state.Events.Select(e => e.Operation).ShouldBe([CdcOperation.Insert, CdcOperation.Update, CdcOperation.Delete]);
        state.Watermark.ShouldBe(11);
        state.Head.ShouldBe(3);
        h.Signal.Notified.ShouldBe([(state.Id, 3L)]);
    }

    [Fact]
    public async Task PayloadsAreEncrypted_AndCarryKeyAndAfterImage()
    {
        var h = await Seeded(new CdcProducerHarness());
        h.Source.Changes.Add(Change(10, "I", 7, "seven"));

        await h.Poller.PollTableChangesAsync(TableId, 1);

        var (record, payload) = (await h.ReadChangesAsync()).Single();
        System.Text.Encoding.UTF8.GetString(record.Payload.Data).ShouldNotContain("seven");
        payload.Key["Id"].ShouldBe(7);
        payload.After!["Name"].ShouldBe("seven");
        payload.Before.ShouldBeNull();
        payload.BeforeUnknown.ShouldBeFalse();
    }

    [Fact]
    public async Task SourceIsReadInOneSnapshotTransaction_WithExplicitColumns_AndTheConfiguredTimeout()
    {
        var h = await Seeded(new CdcProducerHarness(o => new MssqlChangeTrackingOptions
        {
            Enabled = true,
            QueryTimeoutSeconds = 30,
            Tables = [new CdcTrackedTableOptions { Table = "finance.dbo.invoices", QueryTimeoutSeconds = 7 }]
        }));
        await h.Poller.PollTableChangesAsync(TableId, 1); // activates (probes) once
        h.Source.Changes.Add(Change(10, "I", 1));
        h.Source.Isolations.Clear();
        h.Source.Statements.Clear();
        h.Source.CommandTimeouts.Clear();
        h.Source.Commits = 0;

        await h.Poller.PollTableChangesAsync(TableId, 1);

        h.Source.Isolations.ShouldBe([IsolationLevel.Snapshot]);
        h.Source.Commits.ShouldBe(1);
        var read = h.Source.Statements.Single(s => s.Contains("CHANGETABLE(CHANGES [dbo].[invoices], @lastVersion)", StringComparison.Ordinal));
        read.ShouldNotContain("T.*");
        read.ShouldContain("T.[Id]");
        read.ShouldContain("T.[Name]");
        read.ShouldContain("TOP (@batchSize)");
        read.ShouldContain("ORDER BY CT.SYS_CHANGE_VERSION ASC");
        h.Source.CommandTimeouts.ShouldNotBeEmpty();
        h.Source.CommandTimeouts.ShouldAllBe(t => t == 7);
        h.Source.Statements.ShouldAllBe(s => !s.Contains("sys.", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task NonSnapshotReadOptIn_UsesReadCommitted()
    {
        var h = await Seeded(new CdcProducerHarness(o => new MssqlChangeTrackingOptions { Enabled = true, TrackedTables = ["finance.dbo.invoices"], AllowNonSnapshotRead = true }));
        h.Source.Changes.Add(Change(10, "I", 1));

        await h.Poller.PollTableChangesAsync(TableId, 1);

        h.Source.Isolations.ShouldBe([IsolationLevel.ReadCommitted]);
    }

    [Fact]
    public async Task FullBatch_DoesNotSkipRowsOfASplitVersion()
    {
        var h = await Seeded(new CdcProducerHarness(o => new MssqlChangeTrackingOptions { Enabled = true, TrackedTables = ["finance.dbo.invoices"], BatchSize = 3 }));
        h.Source.Changes.Add(Change(10, "I", 1));
        h.Source.Changes.Add(Change(10, "I", 2));
        foreach (var id in new[] { 3, 4, 5, 6 })
        {
            h.Source.Changes.Add(Change(11, "U", id)); // one version with more rows than a batch
        }

        var first = await h.Poller.PollTableChangesAsync(TableId, 1);
        h.InnerStore.Only.Watermark.ShouldBe(10); // version 11 was cut by the batch: not committed yet
        var second = await h.Poller.PollTableChangesAsync(TableId, 1);
        var third = await h.Poller.PollTableChangesAsync(TableId, 1);

        first.ShouldBe(2);
        second.ShouldBe(4); // the whole version is fetched without TOP
        third.ShouldBe(0);
        h.InnerStore.Only.Events.Select(e => e.Offset).ShouldBe([1L, 2, 3, 4, 5, 6]);
        h.InnerStore.Only.Watermark.ShouldBe(11);
    }

    [Fact]
    public async Task EmptyPoll_WritesNothing_AndTouchesLastPolledAtMostEveryMinute()
    {
        var h = await Seeded(new CdcProducerHarness());
        var appendsBefore = h.InnerStore.AppendCalls;

        await h.Poller.PollTableChangesAsync(TableId, 1);
        await h.Poller.PollTableChangesAsync(TableId, 1);
        h.InnerStore.TouchCalls.ShouldBe(1);
        h.Time.Advance(TimeSpan.FromSeconds(61));
        await h.Poller.PollTableChangesAsync(TableId, 1);

        h.InnerStore.TouchCalls.ShouldBe(2);
        h.InnerStore.AppendCalls.ShouldBe(appendsBefore);
        h.Signal.Notified.ShouldBeEmpty();
    }

    [Fact]
    public async Task BeforeImage_FollowsTheLastImage_AndUnknownBeforeIsMarked()
    {
        var h = await Seeded(new CdcProducerHarness());
        h.Source.Changes.Add(Change(10, "I", 1, "first"));
        await h.Poller.PollTableChangesAsync(TableId, 1);
        h.Source.Changes.Add(Change(11, "U", 1, "second"));
        h.Source.Changes.Add(Change(12, "U", 2, "never-seen")); // key without a stored image
        await h.Poller.PollTableChangesAsync(TableId, 1);
        h.Source.Changes.Add(Change(13, "D", 1));
        await h.Poller.PollTableChangesAsync(TableId, 1);

        var changes = await h.ReadChangesAsync();

        changes[1].Payload.Before!["Name"].ShouldBe("first");
        changes[1].Payload.After!["Name"].ShouldBe("second");
        changes[2].Payload.Before.ShouldBeNull();
        changes[2].Payload.BeforeUnknown.ShouldBeTrue();
        changes[3].Record.Operation.ShouldBe(CdcOperation.Delete);
        changes[3].Payload.Before!["Name"].ShouldBe("second");
        changes[3].Payload.After.ShouldBeNull();
        changes[3].Payload.Key["Id"].ShouldBe(1);
        h.InnerStore.Only.Images.Count.ShouldBe(1); // row 1 deleted, row 2 stored
    }

    [Fact]
    public async Task KeyFingerprint_IsNotInTheEvents_AndIsStablePerKey()
    {
        var h = await Seeded(new CdcProducerHarness());
        h.Source.Changes.Add(Change(10, "I", 1));
        h.Source.Changes.Add(Change(11, "I", 2));
        await h.Poller.PollTableChangesAsync(TableId, 1);

        var images = h.InnerStore.Only.Images.Values.ToList();

        images.Count.ShouldBe(2);
        images.Select(i => Convert.ToHexString(i.KeyFingerprint)).Distinct().Count().ShouldBe(2);
        images.ShouldAllBe(i => i.KeyFingerprint.Length == 32);
    }

    [Fact]
    public async Task ExpiredWatermark_Restart_AppendsAControlEvent_AndContinuesAtTheCurrentVersion()
    {
        // AC-4: change table B, then enable CT on table A (its min valid version = current), store watermark 0 for A
        var h = await Seeded(new CdcProducerHarness(), watermark: 0);
        h.Source.MinValidVersion = 10;
        h.Source.CurrentVersion = 12;
        h.Source.Changes.Add(Change(11, "I", 1));

        var appended = await h.Poller.PollTableChangesAsync(TableId, 1);

        appended.ShouldBe(1);
        var state = h.InnerStore.Only;
        state.Watermark.ShouldBe(12);
        var control = state.Events.Single();
        control.Kind.ShouldBe(CdcStreamEventKind.WatermarkExpired);
        control.Offset.ShouldBe(1);
        control.Operation.ShouldBeNull();
        control.TenantScope.ShouldBe(CdcTenantScope.Shared);
        var text = System.Text.Encoding.UTF8.GetString(await h.Crypto.UnprotectEventAsync(control));
        text.ShouldContain("\"StoredVersion\":0");
        text.ShouldContain("\"MinValidVersion\":10");
        text.ShouldContain("\"CurrentVersion\":12");

        // the poller continues from there
        h.Source.Changes.Add(Change(13, "U", 2));
        (await h.Poller.PollTableChangesAsync(TableId, 1)).ShouldBe(1);
        h.InnerStore.Only.Events.Select(e => e.Offset).ShouldBe([1L, 2]);
    }

    [Fact]
    public async Task ExpiredWatermark_Halt_StopsUntilResync()
    {
        var h = await Seeded(
            new CdcProducerHarness(o => new MssqlChangeTrackingOptions { Enabled = true, TrackedTables = ["finance.dbo.invoices"], OnWatermarkExpired = CdcOnWatermarkExpired.Halt }),
            watermark: 0);
        h.Source.MinValidVersion = 10;
        h.Source.CurrentVersion = 12;

        (await h.Poller.PollTableChangesAsync(TableId, 1)).ShouldBe(0);

        h.InnerStore.Only.Status.ShouldBe(CdcStreamStatus.Halted);
        h.InnerStore.Only.ActivationCode.ShouldBe(CdcActivationCodes.WatermarkExpired);
        h.InnerStore.Only.Events.ShouldBeEmpty();
        h.InnerStore.Only.Watermark.ShouldBe(0);

        // halted: no further source access until an administrator resyncs
        h.Source.Opened = 0;
        (await h.Poller.PollTableChangesAsync(TableId, 1)).ShouldBe(0);
        h.Source.Opened.ShouldBe(0);
    }

    [Fact]
    public async Task PerTablePolicy_OverridesTheGlobalWatermarkPolicy()
    {
        var h = await Seeded(
            new CdcProducerHarness(o => new MssqlChangeTrackingOptions
            {
                Enabled = true,
                OnWatermarkExpired = CdcOnWatermarkExpired.Restart,
                Tables = [new CdcTrackedTableOptions { Table = "finance.dbo.invoices", OnWatermarkExpired = CdcOnWatermarkExpired.Halt }]
            }),
            watermark: 0);
        h.Source.MinValidVersion = 10;

        await h.Poller.PollTableChangesAsync(TableId, 1);

        h.InnerStore.Only.Status.ShouldBe(CdcStreamStatus.Halted);
    }

    [Fact]
    public async Task StaleFencingToken_IsRejected_AndNothingIsAppended()
    {
        var h = new CdcProducerHarness();
        await h.SeedWatermarkAsync(5, token: 9); // a newer lease owner already wrote
        h.Source.Changes.Add(Change(12, "I", 1));

        await Should.ThrowAsync<FencingTokenRejectedException>(() => h.Poller.PollTableChangesAsync(TableId, 4));

        h.InnerStore.Only.Events.ShouldBeEmpty();
        h.InnerStore.Only.Watermark.ShouldBe(5);
        h.Signal.Notified.ShouldBeEmpty();
    }

    [Fact]
    public async Task FirstRun_StaleToken_IsFenced()
    {
        var h = new CdcProducerHarness();
        h.Source.CurrentVersion = 100;
        var stream = await h.Store.EnsureStreamAsync(CdcTableActivator.StreamSource, TableId);
        h.InnerStore.Streams[stream.Id].WatermarkToken = 9;

        await Should.ThrowAsync<FencingTokenRejectedException>(() => h.Poller.PollTableChangesAsync(TableId, 1));
    }

    [Fact]
    public async Task LostHeadRace_AppendsNothing_AndReturnsZero()
    {
        var h = await Seeded(new CdcProducerHarness());
        h.Source.Changes.Add(Change(10, "I", 1));
        h.Store.BeforeAppend = () =>
        {
            h.InnerStore.Only.Head += 1; // a concurrent producer advanced the stream
            return Task.CompletedTask;
        };

        (await h.Poller.PollTableChangesAsync(TableId, 1)).ShouldBe(0);

        h.InnerStore.Only.Events.ShouldBeEmpty();
        h.Signal.Notified.ShouldBeEmpty();
    }

    [Fact]
    public async Task TransientSourceFailure_Propagates_AndAppendsNothing()
    {
        var h = await Seeded(new CdcProducerHarness());
        h.Source.Changes.Add(Change(10, "I", 1));
        h.Source.ThrowOnRead = true;

        await Should.ThrowAsync<InvalidOperationException>(() => h.Poller.PollTableChangesAsync(TableId, 1));

        h.InnerStore.Only.Events.ShouldBeEmpty();
    }

    [Fact]
    public async Task FailedSignal_DoesNotFailTheAppend()
    {
        var h = await Seeded(new CdcProducerHarness());
        h.Source.Changes.Add(Change(10, "I", 1));
        h.Signal.Throws = true;

        (await h.Poller.PollTableChangesAsync(TableId, 1)).ShouldBe(1);
    }

    [Fact]
    public async Task UnsupportedColumnType_FailsTheWholeBatch_WithAStableCode_AndWritesNothing()
    {
        var h = await Seeded(new CdcProducerHarness());
        h.Source.Changes.Add(new FakeChangeSource.Change(10, "I", new Dictionary<string, object?> { ["Id"] = 1, ["Name"] = new Uri("http://unsupported") }));
        h.Source.Changes.Add(Change(11, "I", 2));

        var ex = await Should.ThrowAsync<CdcSourceConfigurationException>(() => h.Poller.PollTableChangesAsync(TableId, 1));

        ex.Message.ShouldBe(CdcActivationCodes.UnsupportedType);
        h.InnerStore.Only.Events.ShouldBeEmpty();
        h.InnerStore.Only.Watermark.ShouldBe(5);
        h.InnerStore.Only.Status.ShouldBe(CdcStreamStatus.ActivationFailed);
    }

    [Fact]
    public async Task OversizeRow_ProducesAnEventWithoutValues_AndDropsItsImage()
    {
        var h = await Seeded(new CdcProducerHarness(cdcStream: new CdcStreamOptions { MaxEventPayloadBytes = 200 }));
        h.Source.Changes.Add(Change(10, "I", 1, new string('x', 500)));

        await h.Poller.PollTableChangesAsync(TableId, 1);

        var (_, payload) = (await h.ReadChangesAsync()).Single();
        payload.PayloadOversize.ShouldBeTrue();
        payload.After.ShouldBeNull();
        payload.Key.ShouldBeEmpty();
        h.InnerStore.Only.Images.ShouldBeEmpty();
    }

    [Fact]
    public async Task ActivationFailure_ThrowsTheStableCode_AndPollsNothing()
    {
        var h = new CdcProducerHarness();
        h.Source.MinValidVersion = null;

        var ex = await Should.ThrowAsync<CdcSourceConfigurationException>(() => h.Poller.PollTableChangesAsync(TableId, 1));

        ex.Message.ShouldBe(CdcActivationCodes.TrackingDisabled);
        h.InnerStore.Only.Events.ShouldBeEmpty();
    }

    [Fact]
    public async Task UntrackedTable_IsIgnored()
    {
        var h = new CdcProducerHarness();

        (await h.Poller.PollTableChangesAsync(new TableIdentifier("finance", "dbo", "other"), 1)).ShouldBe(0);

        h.Source.Opened.ShouldBe(0);
    }

    [Theory]
    [InlineData("dbo; DROP TABLE Users;--", "invoices")]
    [InlineData("dbo", "invoices]; DROP TABLE Users;--")]
    [InlineData("dbo", "inv oices")]
    [InlineData("dbo", "invoices' OR '1'='1")]
    public async Task MaliciousIdentifiers_AreRejectedBeforeAnyStatement(string schema, string tableName)
    {
        var h = new CdcProducerHarness();

        await Should.ThrowAsync<ArgumentException>(() => h.Poller.PollTableChangesAsync(new TableIdentifier("finance", schema, tableName), 1));

        h.Source.Statements.ShouldBeEmpty();
    }

    [Fact]
    public async Task Cancellation_Propagates()
    {
        var h = await Seeded(new CdcProducerHarness());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => h.Poller.PollTableChangesAsync(TableId, 1, cts.Token));
    }
}
