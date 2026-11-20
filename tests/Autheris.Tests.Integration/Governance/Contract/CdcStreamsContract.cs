using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.State;
using Autheris.Application.Streaming;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Tests.Common;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Integration.Governance.Contract;

/// <summary>
/// Contract suite for <c>ICdcStreamStore</c> (durable, ordered, per-table CDC streams) across PostgreSQL and SQL Server engines.
/// Streams are keyed by source and table, not by tenant: the isolation cases separate rows by stream id. Offsets are dense and immutable;
/// safety rests on the fenced watermark upsert and the head-offset compare-and-swap, which the append and purge cases pin.
/// </summary>
public abstract partial class CdcStreamsContract : GovernanceContractBase
{
    private const string Source = "mssql_ct";
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeMilliseconds(1_760_000_000_000);
    private static readonly TableIdentifier Orders = new("sales", "dbo", "orders");
    private static readonly TableIdentifier Invoices = new("sales", "dbo", "invoices");

    private static byte[] Fp(int n)
    {
        var b = new byte[32];
        b[0] = (byte)n;
        b[31] = (byte)(n >> 8);
        return b;
    }

    private static CdcProtectedBlob Blob(string dek, byte fill, int length = 40) => new(dek, Enumerable.Repeat(fill, length).ToArray());

    private static CdcStreamEventRecord Ev(CdcStreamId id, long offset, long version, string dek = "dek1", string? tenant = "acme", string? prior = null,
        CdcStreamEventKind kind = CdcStreamEventKind.Change, CdcOperation? op = CdcOperation.Insert, CdcTenantScope scope = CdcTenantScope.Row, int payloadLength = 40) =>
        new(id, offset, version, kind, kind == CdcStreamEventKind.Change ? op : null, scope, tenant, prior, Blob(dek, (byte)offset, payloadLength), T0.AddSeconds(offset));

    private static CdcAppendBatch Batch(CdcStreamDescriptor stream, long expectedHead, int count, long token = 1, long watermark = 100, long firstVersion = 1,
        IReadOnlyList<CdcRowImageWrite>? images = null, TableIdentifier? table = null, string dek = "dek1") =>
        new(stream.Id, Source, table ?? stream.Table, token, expectedHead, watermark,
            Enumerable.Range(1, count).Select(i => Ev(stream.Id, expectedHead + i, firstVersion + i - 1, dek)).ToList(), images ?? []);

    [RequiresContainerRuntimeFact]
    public async Task Ensure_CreatesEmptyActiveStream()
    {
        if (!Store.IsAvailable) return;
        var store = Store.CdcStreams;

        var s = await store.EnsureStreamAsync(Source, Orders);

        CdcStreamId.IsValid(s.Id.Value).ShouldBeTrue();
        s.Source.ShouldBe(Source);
        s.Table.ShouldBe(Orders);
        s.Status.ShouldBe(CdcStreamStatus.Active);
        s.ActivationCode.ShouldBeNull();
        s.HeadOffset.ShouldBe(0);
        s.TailOffset.ShouldBe(1);
        s.SourceWatermark.ShouldBeNull();
        s.LastPolledAt.ShouldBeNull();
        store.IsDurable.ShouldBeTrue();
    }

    [RequiresContainerRuntimeFact]
    public async Task Ensure_IsIdempotent_CaseInsensitive_AndSurvivesRestart()
    {
        if (!Store.IsAvailable) return;
        var store = Store.CdcStreams;
        var first = await store.EnsureStreamAsync(Source, Orders);

        var again = await store.EnsureStreamAsync(Source, new TableIdentifier("SALES", "DBO", "Orders"));
        await using var restarted = await Store.CreateAdditionalStoreAsync();
        var afterRestart = await restarted.CdcStreams.EnsureStreamAsync(Source, Orders);

        again.Id.ShouldBe(first.Id);
        afterRestart.Id.ShouldBe(first.Id);
        (await store.ListStreamsAsync()).Count.ShouldBe(1);
    }

    [RequiresContainerRuntimeFact]
    public async Task Ensure_Concurrent_CreatesExactlyOneStream()
    {
        if (!Store.IsAvailable) return;
        var store = Store.CdcStreams;

        var ids = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(async () => (await store.EnsureStreamAsync(Source, Orders)).Id)));

        ids.Distinct().Count().ShouldBe(1);
        (await store.ListStreamsAsync()).Count.ShouldBe(1);
    }

    [RequiresContainerRuntimeFact]
    public async Task Ensure_DistinctTablesAndSources_GetDistinctStreams()
    {
        if (!Store.IsAvailable) return;
        var store = Store.CdcStreams;

        var a = await store.EnsureStreamAsync(Source, Orders);
        var b = await store.EnsureStreamAsync(Source, Invoices);
        var c = await store.EnsureStreamAsync("other", Orders);

        new[] { a.Id, b.Id, c.Id }.Distinct().Count().ShouldBe(3);
        (await store.ListStreamsAsync()).Select(x => x.Id).ShouldBe([a.Id, b.Id, c.Id], ignoreOrder: true);
    }

    [RequiresContainerRuntimeFact]
    public async Task Get_UnknownStream_ReturnsNull()
    {
        if (!Store.IsAvailable) return;
        var store = Store.CdcStreams;

        (await store.GetStreamAsync(CdcStreamId.New())).ShouldBeNull();
    }

    [RequiresContainerRuntimeFact]
    public async Task SetStatus_AndTouchPolled_Persist()
    {
        if (!Store.IsAvailable) return;
        var store = Store.CdcStreams;
        var s = await store.EnsureStreamAsync(Source, Orders);

        await store.SetStatusAsync(s.Id, CdcStreamStatus.ActivationFailed, CdcActivationCodes.PkMismatch);
        await store.TouchPolledAsync(s.Id, T0);
        var loaded = await store.GetStreamAsync(s.Id);

        loaded!.Status.ShouldBe(CdcStreamStatus.ActivationFailed);
        loaded.ActivationCode.ShouldBe(CdcActivationCodes.PkMismatch);
        loaded.LastPolledAt.ShouldBe(T0);

        await store.SetStatusAsync(s.Id, CdcStreamStatus.Active, null);
        (await store.GetStreamAsync(s.Id))!.ActivationCode.ShouldBeNull();
    }

    [RequiresContainerRuntimeFact]
    public async Task Append_PersistsEventsDensely_AdvancesHeadAndWatermark()
    {
        if (!Store.IsAvailable) return;
        var store = Store.CdcStreams;
        var s = await store.EnsureStreamAsync(Source, Orders);
        var events = new List<CdcStreamEventRecord>
        {
            Ev(s.Id, 1, 11, tenant: "acme", op: CdcOperation.Insert, payloadLength: 9000),
            Ev(s.Id, 2, 11, tenant: null, op: CdcOperation.Delete, scope: CdcTenantScope.Shared),
            Ev(s.Id, 3, 12, kind: CdcStreamEventKind.WatermarkExpired, tenant: null, scope: CdcTenantScope.Owner),
        };

        await store.AppendAsync(new CdcAppendBatch(s.Id, Source, Orders, 7, 0, 555, events, []));

        var loaded = await store.GetStreamAsync(s.Id);
        loaded!.HeadOffset.ShouldBe(3);
        loaded.TailOffset.ShouldBe(1);
        loaded.SourceWatermark.ShouldBe(555);
        var read = await store.ReadAsync(s.Id, 1, 100);
        read.HeadOffset.ShouldBe(3);
        read.TailOffset.ShouldBe(1);
        read.Events.Count.ShouldBe(3);
        for (var i = 0; i < 3; i++)
        {
            var got = read.Events[i];
            var exp = events[i];
            got.StreamId.ShouldBe(exp.StreamId);
            got.Offset.ShouldBe(exp.Offset);
            got.ChangeVersion.ShouldBe(exp.ChangeVersion);
            got.Kind.ShouldBe(exp.Kind);
            got.Operation.ShouldBe(exp.Operation);
            got.TenantScope.ShouldBe(exp.TenantScope);
            got.TenantId.ShouldBe(exp.TenantId);
            got.PriorTenantId.ShouldBeNull();
            got.Payload.DekId.ShouldBe(exp.Payload.DekId);
            got.Payload.Data.ShouldBe(exp.Payload.Data);
            got.ProducedAt.ShouldBe(exp.ProducedAt);
        }
    }

    /// <summary>SEC-CDC99-01: the before-image tenant of a tenant move is persisted next to the event tenant.</summary>
    [RequiresContainerRuntimeFact]
    public async Task Append_PersistsPriorTenantId()
    {
        if (!Store.IsAvailable) return;
        var store = Store.CdcStreams;
        var s = await store.EnsureStreamAsync(Source, Orders);
        var moved = Ev(s.Id, 1, 1, tenant: "tenant-b", prior: "tenant-a", op: CdcOperation.Update);
        var plain = Ev(s.Id, 2, 2, tenant: "tenant-b", prior: null, op: CdcOperation.Update);

        await store.AppendAsync(new CdcAppendBatch(s.Id, Source, Orders, 1, 0, 5, [moved, plain], []));

        var read = (await store.ReadAsync(s.Id, 1, 10)).Events;
        read[0].TenantId.ShouldBe("tenant-b");
        read[0].PriorTenantId.ShouldBe("tenant-a");
        read[1].PriorTenantId.ShouldBeNull();
    }

    [RequiresContainerRuntimeFact]
    public async Task Append_WrongExpectedHead_Conflicts_AndWritesNothing()
    {
        if (!Store.IsAvailable) return;
        var store = Store.CdcStreams;
        var s = await store.EnsureStreamAsync(Source, Orders);
        await store.AppendAsync(Batch(s, 0, 2, watermark: 10));

        var stale = Batch(s, 0, 3, watermark: 999, images: [new CdcRowImageWrite(Fp(1), CdcTenantScope.Row, "acme", Blob("img", 1), 1)]);
        await Should.ThrowAsync<CdcStreamConflictException>(() => store.AppendAsync(stale));

        var loaded = await store.GetStreamAsync(s.Id);
        loaded!.HeadOffset.ShouldBe(2);
        loaded.SourceWatermark.ShouldBe(10);
        (await store.ReadAsync(s.Id, 1, 100)).Events.Count.ShouldBe(2);
        (await store.GetRowImagesAsync(s.Id, [Fp(1)])).ShouldBeEmpty();
    }

    [RequiresContainerRuntimeFact]
    public async Task Append_StaleFencingToken_IsRejected_AndWritesNothing()
    {
        if (!Store.IsAvailable) return;
        var store = Store.CdcStreams;
        var s = await store.EnsureStreamAsync(Source, Orders);
        await store.AppendAsync(Batch(s, 0, 1, token: 5, watermark: 10));

        await Should.ThrowAsync<FencingTokenRejectedException>(() => store.AppendAsync(Batch(s, 1, 2, token: 4, watermark: 999)));

        var loaded = await store.GetStreamAsync(s.Id);
        loaded!.HeadOffset.ShouldBe(1);
        loaded.SourceWatermark.ShouldBe(10);
        (await store.ReadAsync(s.Id, 1, 100)).Events.Count.ShouldBe(1);
    }

    [RequiresContainerRuntimeFact]
    public async Task Append_EqualOrHigherToken_IsAccepted()
    {
        if (!Store.IsAvailable) return;
        var store = Store.CdcStreams;
        var s = await store.EnsureStreamAsync(Source, Orders);

        await store.AppendAsync(Batch(s, 0, 1, token: 5));
        await store.AppendAsync(Batch(s, 1, 1, token: 5));
        await store.AppendAsync(Batch(s, 2, 1, token: 9));

        (await store.GetStreamAsync(s.Id))!.HeadOffset.ShouldBe(3);
    }

    [RequiresContainerRuntimeFact]
    public async Task Append_Watermark_IsMonotonic()
    {
        if (!Store.IsAvailable) return;
        var store = Store.CdcStreams;
        var s = await store.EnsureStreamAsync(Source, Orders);

        await store.AppendAsync(Batch(s, 0, 1, watermark: 100));
        await store.AppendAsync(Batch(s, 1, 1, watermark: 50));

        (await store.GetStreamAsync(s.Id))!.SourceWatermark.ShouldBe(100);
    }

    [RequiresContainerRuntimeFact]
    public async Task Append_WithoutEvents_AdvancesWatermarkUnderFence()
    {
        if (!Store.IsAvailable) return;
        var store = Store.CdcStreams;
        var s = await store.EnsureStreamAsync(Source, Orders);
        await store.AppendAsync(Batch(s, 0, 1, token: 3, watermark: 10));

        await store.AppendAsync(new CdcAppendBatch(s.Id, Source, Orders, 3, 1, 20, [], []));
        await Should.ThrowAsync<FencingTokenRejectedException>(() => store.AppendAsync(new CdcAppendBatch(s.Id, Source, Orders, 2, 1, 30, [], [])));

        var loaded = await store.GetStreamAsync(s.Id);
        loaded!.HeadOffset.ShouldBe(1);
        loaded.SourceWatermark.ShouldBe(20);
    }

    [RequiresContainerRuntimeFact]
    public async Task Append_RejectsMalformedBatches_BeforeWriting()
    {
        if (!Store.IsAvailable) return;
        var store = Store.CdcStreams;
        var s = await store.EnsureStreamAsync(Source, Orders);
        var gapOffset = new CdcAppendBatch(s.Id, Source, Orders, 1, 0, 1, [Ev(s.Id, 2, 1)], []);
        var gapKind = new CdcAppendBatch(s.Id, Source, Orders, 1, 0, 1, [Ev(s.Id, 1, 1, kind: CdcStreamEventKind.Gap)], []);
        var foreign = new CdcAppendBatch(s.Id, Source, Orders, 1, 0, 1, [Ev(CdcStreamId.New(), 1, 1)], []);
        var badFingerprint = new CdcAppendBatch(s.Id, Source, Orders, 1, 0, 1, [Ev(s.Id, 1, 1)], [new CdcRowImageWrite([1, 2, 3], CdcTenantScope.Row, "a", Blob("i", 1), 1)]);
        var badImageOffset = new CdcAppendBatch(s.Id, Source, Orders, 1, 0, 1, [Ev(s.Id, 1, 1)], [new CdcRowImageWrite(Fp(1), CdcTenantScope.Row, "a", Blob("i", 1), 2)]);

        foreach (var bad in new[] { gapOffset, gapKind, foreign, badFingerprint, badImageOffset })
        {
            await Should.ThrowAsync<ArgumentException>(() => store.AppendAsync(bad));
        }

        (await store.GetStreamAsync(s.Id))!.HeadOffset.ShouldBe(0);
    }

    [RequiresContainerRuntimeFact]
    public async Task Append_Images_UpsertAndDelete_LastWriteWinsWithinBatch()
    {
        if (!Store.IsAvailable) return;
        var store = Store.CdcStreams;
        var s = await store.EnsureStreamAsync(Source, Orders);
        await store.AppendAsync(Batch(s, 0, 3, images:
        [
            new CdcRowImageWrite(Fp(1), CdcTenantScope.Row, "acme", Blob("img", 1), 1),
            new CdcRowImageWrite(Fp(2), CdcTenantScope.Shared, null, Blob("img", 2), 2),
            new CdcRowImageWrite(Fp(1), CdcTenantScope.Row, "acme", Blob("img", 3), 3),
        ]));

        var images = (await store.GetRowImagesAsync(s.Id, [Fp(1), Fp(2), Fp(3)])).ToDictionary(i => i.KeyFingerprint[0]);

        images.Count.ShouldBe(2);
        images[1].Offset.ShouldBe(3);
        images[1].Image.Data.ShouldBe(Blob("img", 3).Data);
        images[1].Image.DekId.ShouldBe("img");
        images[1].TenantId.ShouldBe("acme");
        images[2].TenantScope.ShouldBe(CdcTenantScope.Shared);
        images[2].TenantId.ShouldBeNull();

        await store.AppendAsync(Batch(s, 3, 2, images:
        [
            new CdcRowImageWrite(Fp(1), CdcTenantScope.Row, "acme", null, 4),
            new CdcRowImageWrite(Fp(2), CdcTenantScope.Row, "acme", Blob("img", 9), 5),
        ]));

        var after = (await store.GetRowImagesAsync(s.Id, [Fp(1), Fp(2)])).ToList();
        after.Count.ShouldBe(1);
        after[0].KeyFingerprint.ShouldBe(Fp(2));
        after[0].Offset.ShouldBe(5);
        after[0].TenantScope.ShouldBe(CdcTenantScope.Row);
    }

    [RequiresContainerRuntimeFact]
    public async Task GetRowImages_IsScopedToTheStream()
    {
        if (!Store.IsAvailable) return;
        var store = Store.CdcStreams;
        var a = await store.EnsureStreamAsync(Source, Orders);
        var b = await store.EnsureStreamAsync(Source, Invoices);
        await store.AppendAsync(Batch(a, 0, 1, images: [new CdcRowImageWrite(Fp(1), CdcTenantScope.Row, "acme", Blob("img", 1), 1)]));

        (await store.GetRowImagesAsync(b.Id, [Fp(1)])).ShouldBeEmpty();
        (await store.GetRowImagesAsync(a.Id, [])).ShouldBeEmpty();
        (await store.GetRowImagesAsync(a.Id, [Fp(1)])).Count.ShouldBe(1);
    }

    [RequiresContainerRuntimeFact]
    public async Task GetRowImages_HandlesLargeLookups()
    {
        if (!Store.IsAvailable) return;
        var store = Store.CdcStreams;
        var s = await store.EnsureStreamAsync(Source, Orders);
        var images = Enumerable.Range(1, 130).Select(i => new CdcRowImageWrite(Fp(i), CdcTenantScope.Row, "acme", Blob("img", (byte)i), 1)).ToList();
        await store.AppendAsync(Batch(s, 0, 1, images: images));

        var loaded = await store.GetRowImagesAsync(s.Id, Enumerable.Range(1, 400).Select(Fp).ToList());

        loaded.Count.ShouldBe(130);
    }

    [RequiresContainerRuntimeFact]
    public async Task Append_LargeBatch_IsOneAtomicRange()
    {
        if (!Store.IsAvailable) return;
        var store = Store.CdcStreams;
        var s = await store.EnsureStreamAsync(Source, Orders);

        await store.AppendAsync(Batch(s, 0, 175));

        var read = await store.ReadAsync(s.Id, 1, 1000);
        read.HeadOffset.ShouldBe(175);
        read.Events.Select(e => e.Offset).ShouldBe(Enumerable.Range(1, 175).Select(i => (long)i));
    }

    [RequiresContainerRuntimeFact]
    public async Task Read_ReturnsOrderedWindowFromOffset_AndTailHead()
    {
        if (!Store.IsAvailable) return;
        var store = Store.CdcStreams;
        var s = await store.EnsureStreamAsync(Source, Orders);
        await store.AppendAsync(Batch(s, 0, 10));

        var window = await store.ReadAsync(s.Id, 4, 3);
        var beyond = await store.ReadAsync(s.Id, 11, 5);
        var all = await store.ReadAsync(s.Id, 1, 1000);

        window.Events.Select(e => e.Offset).ShouldBe([4L, 5, 6]);
        window.HeadOffset.ShouldBe(10);
        window.TailOffset.ShouldBe(1);
        beyond.Events.ShouldBeEmpty();
        beyond.HeadOffset.ShouldBe(10);
        all.Events.Count.ShouldBe(10);
    }

    [RequiresContainerRuntimeFact]
    public async Task Read_UnknownStream_IsEmpty()
    {
        if (!Store.IsAvailable) return;
        var store = Store.CdcStreams;

        var read = await store.ReadAsync(CdcStreamId.New(), 1, 10);

        read.Events.ShouldBeEmpty();
        read.HeadOffset.ShouldBe(0);
    }

    [RequiresContainerRuntimeFact]
    public async Task Read_NonPositiveMax_StillBounded()
    {
        if (!Store.IsAvailable) return;
        var store = Store.CdcStreams;
        var s = await store.EnsureStreamAsync(Source, Orders);
        await store.AppendAsync(Batch(s, 0, 3));

        (await store.ReadAsync(s.Id, 1, 0)).Events.Count.ShouldBeLessThanOrEqualTo(1);
    }

    [RequiresContainerRuntimeFact]
    public async Task Purge_RemovesBelowNewTail_InChunks_AndReturnsTail()
    {
        if (!Store.IsAvailable) return;
        var store = Store.CdcStreams;
        var s = await store.EnsureStreamAsync(Source, Orders);
        await store.AppendAsync(Batch(s, 0, 10));

        var first = await store.PurgeAsync(s.Id, 8, 3, 1);
        first.ShouldBe(4);
        (await store.GetStreamAsync(s.Id))!.TailOffset.ShouldBe(4);
        var second = await store.PurgeAsync(s.Id, 8, 100, 1);

        second.ShouldBe(8);
        var read = await store.ReadAsync(s.Id, 1, 100);
        read.TailOffset.ShouldBe(8);
        read.HeadOffset.ShouldBe(10);
        read.Events.Select(e => e.Offset).ShouldBe([8L, 9, 10]);
        (await store.PurgeAsync(s.Id, 5, 100, 1)).ShouldBe(8);
    }

    [RequiresContainerRuntimeFact]
    public async Task Purge_BeyondHead_CapsAtHeadPlusOne_AndAppendContinues()
    {
        if (!Store.IsAvailable) return;
        var store = Store.CdcStreams;
        var s = await store.EnsureStreamAsync(Source, Orders);
        await store.AppendAsync(Batch(s, 0, 4));

        (await store.PurgeAsync(s.Id, 1000, 100, 1)).ShouldBe(5);
        var loaded = await store.GetStreamAsync(s.Id);
        loaded!.TailOffset.ShouldBe(5);
        loaded.HeadOffset.ShouldBe(4);
        (await store.ReadAsync(s.Id, 1, 10)).Events.ShouldBeEmpty();

        await store.AppendAsync(Batch(s, 4, 2));
        var read = await store.ReadAsync(s.Id, 1, 10);
        read.TailOffset.ShouldBe(5);
        read.Events.Select(e => e.Offset).ShouldBe([5L, 6]);
    }

    [RequiresContainerRuntimeFact]
    public async Task Purge_StaleRetentionToken_IsRejected()
    {
        if (!Store.IsAvailable) return;
        var store = Store.CdcStreams;
        var s = await store.EnsureStreamAsync(Source, Orders);
        await store.AppendAsync(Batch(s, 0, 5));
        await store.PurgeAsync(s.Id, 2, 100, 5);

        await Should.ThrowAsync<FencingTokenRejectedException>(() => store.PurgeAsync(s.Id, 4, 100, 4));

        (await store.GetStreamAsync(s.Id))!.TailOffset.ShouldBe(2);
        (await store.ReadAsync(s.Id, 1, 100)).Events.Count.ShouldBe(4);
    }

    [RequiresContainerRuntimeFact]
    public async Task Purge_UnknownStream_ReturnsZero()
    {
        if (!Store.IsAvailable) return;
        var store = Store.CdcStreams;

        (await store.PurgeAsync(CdcStreamId.New(), 5, 10, 1)).ShouldBe(0);
    }

    [RequiresContainerRuntimeFact]
    public async Task PurgeImages_RemovesInChunks_OnlyOfTheStream()
    {
        if (!Store.IsAvailable) return;
        var store = Store.CdcStreams;
        var a = await store.EnsureStreamAsync(Source, Orders);
        var b = await store.EnsureStreamAsync(Source, Invoices);
        var images = Enumerable.Range(1, 5).Select(i => new CdcRowImageWrite(Fp(i), CdcTenantScope.Row, "acme", Blob("img", (byte)i), 1)).ToList();
        await store.AppendAsync(Batch(a, 0, 1, images: images));
        await store.AppendAsync(Batch(b, 0, 1, images: [images[0] with { Offset = 1 }], table: Invoices));

        (await store.PurgeImagesAsync(a.Id, 3)).ShouldBe(3);
        (await store.PurgeImagesAsync(a.Id, 3)).ShouldBe(2);
        (await store.PurgeImagesAsync(a.Id, 3)).ShouldBe(0);

        (await store.GetRowImagesAsync(a.Id, images.Select(i => i.KeyFingerprint).ToList())).ShouldBeEmpty();
        (await store.GetRowImagesAsync(b.Id, [Fp(1)])).Count.ShouldBe(1);
    }

    [RequiresContainerRuntimeFact]
    public async Task ExactlyOnce_ConcurrentProducersWithOverlappingLeases_NeverDuplicateOrSkip()
    {
        if (!Store.IsAvailable) return;
        var store = Store.CdcStreams;
        var s = await store.EnsureStreamAsync(Source, Orders);
        const int producers = 6;
        const int appendsEach = 4;
        const int eventsPerAppend = 3;

        // Every producer believes it owns the table (same token): only the head compare-and-set keeps the stream consistent.
        await Task.WhenAll(Enumerable.Range(1, producers).Select(p => Task.Run(async () =>
        {
            for (var seq = 0; seq < appendsEach; seq++)
            {
                while (true)
                {
                    var head = (await store.GetStreamAsync(s.Id))!.HeadOffset;
                    var events = Enumerable.Range(1, eventsPerAppend).Select(i => Ev(s.Id, head + i, p * 1000 + seq)).ToList();
                    try
                    {
                        await store.AppendAsync(new CdcAppendBatch(s.Id, Source, Orders, 1, head, p * 1000 + seq, events, []));
                        break;
                    }
                    catch (CdcStreamConflictException)
                    {
                    }
                }
            }
        })));

        var read = await store.ReadAsync(s.Id, 1, 10_000);
        read.HeadOffset.ShouldBe(producers * appendsEach * eventsPerAppend);
        read.Events.Select(e => e.Offset).ShouldBe(Enumerable.Range(1, producers * appendsEach * eventsPerAppend).Select(i => (long)i));
        var perVersion = read.Events.GroupBy(e => e.ChangeVersion).ToList();
        perVersion.Count.ShouldBe(producers * appendsEach);
        perVersion.ShouldAllBe(g => g.Count() == eventsPerAppend);
    }

    [RequiresContainerRuntimeFact]
    public async Task ExactlyOnce_ProducersWithDistinctTokens_StaleOnesAreFencedOut()
    {
        if (!Store.IsAvailable) return;
        var store = Store.CdcStreams;
        var s = await store.EnsureStreamAsync(Source, Orders);
        const int producers = 5;
        const int appendsEach = 4;
        var committed = new System.Collections.Concurrent.ConcurrentBag<long>();

        await Task.WhenAll(Enumerable.Range(1, producers).Select(p => Task.Run(async () =>
        {
            for (var seq = 0; seq < appendsEach; seq++)
            {
                while (true)
                {
                    var head = (await store.GetStreamAsync(s.Id))!.HeadOffset;
                    var version = p * 1000 + seq;
                    try
                    {
                        await store.AppendAsync(new CdcAppendBatch(s.Id, Source, Orders, p, head, version, [Ev(s.Id, head + 1, version), Ev(s.Id, head + 2, version)], []));
                        committed.Add(version);
                        break;
                    }
                    catch (CdcStreamConflictException)
                    {
                    }
                    catch (FencingTokenRejectedException)
                    {
                        return; // a newer producer owns the stream: this one stops, nothing of it is half written
                    }
                }
            }
        })));

        var read = await store.ReadAsync(s.Id, 1, 10_000);
        read.Events.Count.ShouldBe(committed.Count * 2);
        read.HeadOffset.ShouldBe(committed.Count * 2);
        read.Events.Select(e => e.Offset).ShouldBe(Enumerable.Range(1, committed.Count * 2).Select(i => (long)i));
        read.Events.GroupBy(e => e.ChangeVersion).Select(g => g.Key).OrderBy(v => v).ShouldBe(committed.OrderBy(v => v));
        read.Events.GroupBy(e => e.ChangeVersion).ShouldAllBe(g => g.Count() == 2);
        committed.Count(v => v / 1000 == producers).ShouldBe(appendsEach); // the highest token always completes
    }

    /// <summary>SEC-CDC99-18 (store part): a reader never silently misses an offset; everything not delivered lies inside a reported tail gap.</summary>
    [RequiresContainerRuntimeFact]
    public async Task NoSilentLoss_ReaderSeesEveryOffsetDeliveredOrCoveredByATailGap()
    {
        if (!Store.IsAvailable) return;
        var store = Store.CdcStreams;
        var s = await store.EnsureStreamAsync(Source, Orders);
        var rng = new Random(20261015);
        long cursor = 1;
        var delivered = new HashSet<long>();
        var gapped = new HashSet<long>();
        var retentionToken = 1L;

        for (var step = 0; step < 60; step++)
        {
            var current = await store.GetStreamAsync(s.Id);
            switch (rng.Next(3))
            {
                case 0:
                    await store.AppendAsync(Batch(s, current!.HeadOffset, rng.Next(1, 6), firstVersion: current.HeadOffset + 1));
                    break;
                case 1:
                    await store.PurgeAsync(s.Id, current!.TailOffset + rng.Next(0, 5), rng.Next(1, 4), retentionToken++);
                    break;
                default:
                    var read = await store.ReadAsync(s.Id, cursor, rng.Next(1, 4));
                    if (cursor < read.TailOffset)
                    {
                        for (var o = cursor; o < read.TailOffset; o++)
                        {
                            gapped.Add(o);
                        }

                        cursor = read.TailOffset;
                    }

                    foreach (var e in read.Events.Where(e => e.Offset >= cursor))
                    {
                        e.Offset.ShouldBe(cursor, "an offset was skipped without a gap");
                        delivered.Add(e.Offset);
                        cursor++;
                    }

                    break;
            }
        }

        while (true)
        {
            var read = await store.ReadAsync(s.Id, cursor, 50);
            if (cursor < read.TailOffset)
            {
                for (var o = cursor; o < read.TailOffset; o++)
                {
                    gapped.Add(o);
                }

                cursor = read.TailOffset;
            }

            if (read.Events.Count == 0)
            {
                break;
            }

            foreach (var e in read.Events.Where(e => e.Offset >= cursor))
            {
                e.Offset.ShouldBe(cursor);
                delivered.Add(e.Offset);
                cursor++;
            }
        }

        var head = (await store.GetStreamAsync(s.Id))!.HeadOffset;
        cursor.ShouldBe(head + 1);
        for (long o = 1; o <= head; o++)
        {
            (delivered.Contains(o) ^ gapped.Contains(o)).ShouldBeTrue($"offset {o} must be delivered xor covered by a gap");
        }
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Reads_never_return_other_tenant_rows()
    {
        if (!Store.IsAvailable) return;
        var streams = Store.CdcStreams;
        var a = await streams.EnsureStreamAsync(Source, Orders);
        var b = await streams.EnsureStreamAsync(Source, Invoices);
        await streams.AppendAsync(Batch(a, 0, 2, images: [new CdcRowImageWrite(Fp(1), CdcTenantScope.Row, "acme", Blob("img", 1), 1)]));

        (await streams.ReadAsync(b.Id, 1, 100)).Events.ShouldBeEmpty();
        (await streams.GetRowImagesAsync(b.Id, [Fp(1)])).ShouldBeEmpty();
        (await streams.GetWatermarkStateAsync(b.Id)).ShouldBeNull();
        (await streams.ReadAsync(a.Id, 1, 100)).Events.ShouldAllBe(e => e.StreamId == a.Id && e.TenantId == "acme");
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Writes_cannot_modify_other_tenant_rows()
    {
        if (!Store.IsAvailable) return;
        var streams = Store.CdcStreams;
        var a = await streams.EnsureStreamAsync(Source, Orders);
        var b = await streams.EnsureStreamAsync(Source, Invoices);
        await streams.AppendAsync(Batch(a, 0, 3, images: [new CdcRowImageWrite(Fp(1), CdcTenantScope.Row, "acme", Blob("img", 1), 1)]));

        // An event of another stream inside a batch is refused; purge, status and image purge of stream b never touch stream a.
        var foreign = new CdcAppendBatch(a.Id, Source, Orders, 1, 3, 1, [Ev(b.Id, 4, 4)], []);
        await Should.ThrowAsync<ArgumentException>(() => streams.AppendAsync(foreign));
        await streams.SetStatusAsync(b.Id, CdcStreamStatus.ActivationFailed, CdcActivationCodes.PkMismatch);
        await streams.PurgeAsync(b.Id, 100, 100, 1);
        await streams.PurgeImagesAsync(b.Id, 100);

        var loaded = await streams.GetStreamAsync(a.Id);
        loaded!.Status.ShouldBe(CdcStreamStatus.Active);
        loaded.HeadOffset.ShouldBe(3);
        loaded.TailOffset.ShouldBe(1);
        (await streams.ReadAsync(a.Id, 1, 100)).Events.Count.ShouldBe(3);
        (await streams.GetRowImagesAsync(a.Id, [Fp(1)])).Count.ShouldBe(1);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Missing_or_unparseable_tenant_id_throws()
    {
        if (!Store.IsAvailable) return;

        Should.Throw<ArgumentException>(() => { _ = new CdcStreamId("not-a-stream-id"); });
        await Should.ThrowAsync<ArgumentException>(() => Store.CdcStreams.EnsureStreamAsync(" ", Orders));
        await Should.ThrowAsync<ArgumentException>(() => Store.CdcStreams.AppendAsync(new CdcAppendBatch(default, Source, Orders, 1, 0, 1, [], [])));
        (await Store.CdcStreams.ListStreamsAsync()).ShouldBeEmpty();
    }

    [RequiresContainerRuntimeFact]
    public override async Task Upsert_is_idempotent()
    {
        if (!Store.IsAvailable) return;

        var first = await Store.CdcStreams.EnsureStreamAsync(Source, Orders);
        var again = await Store.CdcStreams.EnsureStreamAsync(Source, Orders);

        again.Id.ShouldBe(first.Id);
        (await Store.CdcStreams.ListStreamsAsync()).Count.ShouldBe(1);
    }

    [RequiresContainerRuntimeFact]
    public override async Task Unique_violation_maps_to_conflict_exception()
    {
        if (!Store.IsAvailable) return;
        var streams = Store.CdcStreams;
        var s = await streams.EnsureStreamAsync(Source, Orders);
        await streams.AppendAsync(Batch(s, 0, 2));

        await Should.ThrowAsync<CdcStreamConflictException>(() => streams.AppendAsync(Batch(s, 0, 2)));

        (await streams.ReadAsync(s.Id, 1, 100)).Events.Count.ShouldBe(2);
    }

    [RequiresContainerRuntimeFact]
    public override async Task Paging_is_stable_and_ordered()
    {
        if (!Store.IsAvailable) return;
        var s = await Store.CdcStreams.EnsureStreamAsync(Source, Orders);
        await Store.CdcStreams.AppendAsync(Batch(s, 0, 9));

        var first = await Store.CdcStreams.ReadAsync(s.Id, 1, 4);
        var second = await Store.CdcStreams.ReadAsync(s.Id, first.Events[^1].Offset + 1, 4);
        var third = await Store.CdcStreams.ReadAsync(s.Id, second.Events[^1].Offset + 1, 4);

        first.Events.Concat(second.Events).Concat(third.Events).Select(e => e.Offset).ShouldBe(Enumerable.Range(1, 9).Select(i => (long)i));
        (await Store.CdcStreams.ReadAsync(s.Id, 1, 4)).Events.Select(e => e.Offset).ShouldBe(first.Events.Select(e => e.Offset));
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Page_size_above_cap_is_clamped_or_rejected()
    {
        if (!Store.IsAvailable) return;
        var s = await Store.CdcStreams.EnsureStreamAsync(Source, Orders);
        await Store.CdcStreams.AppendAsync(Batch(s, 0, 12));

        // The read window is clamped to 1..10000: a non-positive size returns one row, an absurd size is bounded and returns every row.
        (await Store.CdcStreams.ReadAsync(s.Id, 1, -3)).Events.Count.ShouldBe(1);
        (await Store.CdcStreams.ReadAsync(s.Id, 1, int.MaxValue)).Events.Count.ShouldBe(12);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Soft_deleted_rows_are_invisible_to_reads()
    {
        if (!Store.IsAvailable) return;
        var streams = Store.CdcStreams;
        var s = await streams.EnsureStreamAsync(Source, Orders);
        await streams.AppendAsync(Batch(s, 0, 6, images: [new CdcRowImageWrite(Fp(1), CdcTenantScope.Row, "acme", Blob("img", 1), 1)]));

        (await streams.PurgeAsync(s.Id, 4, 100, 1)).ShouldBe(4);
        (await streams.PurgeImagesAsync(s.Id, 100)).ShouldBe(1);

        var read = await streams.ReadAsync(s.Id, 1, 100);
        read.TailOffset.ShouldBe(4);
        read.Events.Select(e => e.Offset).ShouldBe([4L, 5, 6]);
        (await streams.GetRowImagesAsync(s.Id, [Fp(1)])).ShouldBeEmpty();
    }
}

[Collection(GovernancePostgresCollection.Name)]
public sealed class PostgreSqlCdcStreamsContractTests(GovernancePostgresFixture fixture) : CdcStreamsContract
{
    protected override Task<GovernanceTestStore> CreateStoreAsync() =>
        GovernanceTestStore.CreatePostgreSqlAsync(fixture, "cdc_streams_contract");
}

[Collection(GovernanceSqlServerCollection.Name)]
public sealed class SqlServerCdcStreamsContractTests(GovernanceSqlServerFixture fixture) : CdcStreamsContract
{
    protected override Task<GovernanceTestStore> CreateStoreAsync() =>
        GovernanceTestStore.CreateSqlServerAsync(fixture, "cdc_streams_contract");
}
