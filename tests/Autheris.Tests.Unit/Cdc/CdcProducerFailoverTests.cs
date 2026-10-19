namespace Autheris.Tests.Unit.Cdc;

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.State;
using Autheris.Application.Streaming;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Shouldly;
using Xunit;
using static CdcProducerHarness;

/// <summary>
/// AC-6 / SEC-CDC99-09: producer failover. Safety rests on the fenced watermark upsert and the head offset CAS inside the append
/// transaction, never on the lease: the loser of an overlap is rejected and nothing is produced twice or lost.
/// </summary>
public sealed class CdcProducerFailoverTests
{
    private static async Task<CdcProducerHarness> ThreeVersionsAsync(int batchSize = 3)
    {
        var h = new CdcProducerHarness(o => new MssqlChangeTrackingOptions { Enabled = true, TrackedTables = ["finance.dbo.invoices"], BatchSize = batchSize });
        await h.SeedWatermarkAsync(5);
        h.Source.Changes.Add(Change(10, "I", 1));
        h.Source.Changes.Add(Change(11, "I", 2));
        h.Source.Changes.Add(Change(12, "I", 3));
        return h;
    }

    private static void AssertDenseAndContinuous(CdcProducerHarness h, params long[] versions)
    {
        var events = h.InnerStore.Only.Events;
        events.Select(e => e.Offset).ShouldBe(Enumerable.Range(1, versions.Length).Select(i => (long)i));
        events.Select(e => e.ChangeVersion).ShouldBe(versions);
        events.Select(e => e.ChangeVersion).Distinct().Count().ShouldBe(versions.Length); // no duplicate
        h.InnerStore.Only.Head.ShouldBe(versions.Length);
    }

    [Fact]
    public async Task FailoverMidBatch_CrashBeforeCommit_SecondProducerWithHigherTokenAppendsTheSameVersions()
    {
        var h = await ThreeVersionsAsync();
        h.Store.ThrowBeforeAppend = true;
        await Should.ThrowAsync<System.InvalidOperationException>(() => h.Poller.PollTableChangesAsync(TableId, 5));
        h.InnerStore.Only.Events.ShouldBeEmpty(); // the crash left nothing behind
        h.InnerStore.Only.Watermark.ShouldBe(5);

        var second = h.CreateSecondPoller(new FaultInjectingStreamStore(h.InnerStore));
        (await second.PollTableChangesAsync(TableId, 6)).ShouldBe(2);
        (await second.PollTableChangesAsync(TableId, 6)).ShouldBe(1);

        AssertDenseAndContinuous(h, 10, 11, 12);
        h.InnerStore.Only.Watermark.ShouldBe(12);
    }

    [Fact]
    public async Task FailoverMidBatch_TheStaleProducerIsFenced_AfterTheSecondOneWrote()
    {
        var h = await ThreeVersionsAsync();
        h.Store.ThrowBeforeAppend = true;
        await Should.ThrowAsync<System.InvalidOperationException>(() => h.Poller.PollTableChangesAsync(TableId, 5));
        var second = h.CreateSecondPoller(new FaultInjectingStreamStore(h.InnerStore));
        await second.PollTableChangesAsync(TableId, 6);

        h.Store.ThrowBeforeAppend = false; // the old producer comes back with its stale token
        await Should.ThrowAsync<FencingTokenRejectedException>(() => h.Poller.PollTableChangesAsync(TableId, 5));

        AssertDenseAndContinuous(h, 10, 11);
    }

    [Fact]
    public async Task CrashAfterCommit_TheTakeoverDoesNotProduceTheBatchTwice()
    {
        var h = await ThreeVersionsAsync(batchSize: 10);
        h.Store.ThrowAfterAppend = true; // committed, but the producer died before it learned about it
        await Should.ThrowAsync<System.InvalidOperationException>(() => h.Poller.PollTableChangesAsync(TableId, 5));

        var second = h.CreateSecondPoller(new FaultInjectingStreamStore(h.InnerStore));
        (await second.PollTableChangesAsync(TableId, 6)).ShouldBe(0);

        AssertDenseAndContinuous(h, 10, 11, 12);
    }

    [Fact]
    public async Task OverlappingLeases_LowerTokenIsFenced_NoDuplicate()
    {
        // both producers believe they own the table: the one with the LOWER token reads first, the other commits in between
        var h = await ThreeVersionsAsync(batchSize: 10);
        var rival = h.CreateSecondPoller(new FaultInjectingStreamStore(h.InnerStore));
        h.Store.BeforeAppend = async () =>
        {
            h.Store.BeforeAppend = null;
            await rival.PollTableChangesAsync(TableId, 8);
        };

        await Should.ThrowAsync<FencingTokenRejectedException>(() => h.Poller.PollTableChangesAsync(TableId, 7));

        AssertDenseAndContinuous(h, 10, 11, 12);
    }

    [Fact]
    public async Task OverlappingLeases_StaleHead_LosesTheCompareAndSwap_NoDuplicate()
    {
        // the HIGHER token reads the head first (it is then stale), the rival with the lower token commits in between
        var h = await ThreeVersionsAsync(batchSize: 10);
        var rival = h.CreateSecondPoller(new FaultInjectingStreamStore(h.InnerStore));
        h.Store.BeforeAppend = async () =>
        {
            h.Store.BeforeAppend = null;
            await rival.PollTableChangesAsync(TableId, 7);
        };

        (await h.Poller.PollTableChangesAsync(TableId, 8)).ShouldBe(0); // lost the head race: nothing written

        AssertDenseAndContinuous(h, 10, 11, 12);
        (await h.Poller.PollTableChangesAsync(TableId, 8)).ShouldBe(0); // the next cycle re-reads the head: nothing new
        AssertDenseAndContinuous(h, 10, 11, 12);
    }

    [Fact]
    public async Task EqualTokens_WithTheSameStaleHead_StillCannotDuplicate()
    {
        var h = await ThreeVersionsAsync(batchSize: 10);
        var rival = h.CreateSecondPoller(new FaultInjectingStreamStore(h.InnerStore));
        h.Store.BeforeAppend = async () =>
        {
            h.Store.BeforeAppend = null;
            await rival.PollTableChangesAsync(TableId, 7);
        };

        (await h.Poller.PollTableChangesAsync(TableId, 7)).ShouldBe(0);

        AssertDenseAndContinuous(h, 10, 11, 12);
    }
}
