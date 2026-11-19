namespace Autheris.Tests.Unit.Cdc;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.State;
using Autheris.Application.Streaming;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

/// <summary>
/// PLAN-CDC-99 WP-I1-A: dialect-neutral contract of <see cref="ICdcStreamStore"/> and <see cref="ICdcStreamKeyStore"/>. The in-memory
/// store and SQLite run in the unit project; the PostgreSQL and SQL Server subclasses live in the integration project (this file is
/// linked there). A subclass returns <c>null</c> from <see cref="OpenAsync"/> when its database is unavailable. Calling
/// <see cref="OpenAsync"/> twice yields two store instances over the SAME data (restart simulation).
/// </summary>
public abstract class CdcStreamStoreContractTests : IAsyncLifetime
{
    protected const string Source = "mssql_ct";
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeMilliseconds(1_760_000_000_000);
    private static readonly TableIdentifier Orders = new("sales", "dbo", "orders");
    private static readonly TableIdentifier Invoices = new("sales", "dbo", "invoices");
    private readonly List<object> _disposables = [];

    public virtual Task InitializeAsync() => Task.CompletedTask;

    public virtual async Task DisposeAsync()
    {
        foreach (var d in _disposables)
        {
            if (d is IAsyncDisposable ad)
            {
                await ad.DisposeAsync();
            }
            else if (d is IDisposable sd)
            {
                sd.Dispose();
            }
        }
    }

    /// <summary>Opens a store pair; <c>null</c> when the backend is unavailable.</summary>
    protected abstract Task<(ICdcStreamStore Streams, ICdcStreamKeyStore Keys)?> OpenAsync();

    protected T Track<T>(T disposable) where T : notnull
    {
        _disposables.Add(disposable);
        return disposable;
    }

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

    private static CdcWrappedKey Key(CdcStreamId id, string dek, CdcKeyPurpose purpose = CdcKeyPurpose.Payload, string kek = "k1", DateTimeOffset? retired = null) =>
        new(id, dek, purpose, kek, [1, 2, 3, (byte)dek.Length, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16], T0, retired);

    private async Task<(ICdcStreamStore Streams, ICdcStreamKeyStore Keys)?> Open() => await OpenAsync();

    [Fact]
    public async Task Ensure_CreatesEmptyActiveStream()
    {
        if (await Open() is not var (store, _)) return;

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
        store.IsDurable.ShouldBe(IsDurable);
    }

    protected virtual bool IsDurable => true;

    [Fact]
    public async Task Ensure_IsIdempotent_CaseInsensitive_AndSurvivesRestart()
    {
        if (await Open() is not var (store, _)) return;
        var first = await store.EnsureStreamAsync(Source, Orders);

        var again = await store.EnsureStreamAsync(Source, new TableIdentifier("SALES", "DBO", "Orders"));
        var restarted = await Open();
        var afterRestart = await restarted!.Value.Streams.EnsureStreamAsync(Source, Orders);

        again.Id.ShouldBe(first.Id);
        afterRestart.Id.ShouldBe(first.Id);
        (await store.ListStreamsAsync()).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Ensure_Concurrent_CreatesExactlyOneStream()
    {
        if (await Open() is not var (store, _)) return;

        var ids = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(async () => (await store.EnsureStreamAsync(Source, Orders)).Id)));

        ids.Distinct().Count().ShouldBe(1);
        (await store.ListStreamsAsync()).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Ensure_DistinctTablesAndSources_GetDistinctStreams()
    {
        if (await Open() is not var (store, _)) return;

        var a = await store.EnsureStreamAsync(Source, Orders);
        var b = await store.EnsureStreamAsync(Source, Invoices);
        var c = await store.EnsureStreamAsync("other", Orders);

        new[] { a.Id, b.Id, c.Id }.Distinct().Count().ShouldBe(3);
        (await store.ListStreamsAsync()).Select(x => x.Id).ShouldBe([a.Id, b.Id, c.Id], ignoreOrder: true);
    }

    [Fact]
    public async Task Get_UnknownStream_ReturnsNull()
    {
        if (await Open() is not var (store, _)) return;

        (await store.GetStreamAsync(CdcStreamId.New())).ShouldBeNull();
    }

    [Fact]
    public async Task SetStatus_AndTouchPolled_Persist()
    {
        if (await Open() is not var (store, _)) return;
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

    [Fact]
    public async Task Append_PersistsEventsDensely_AdvancesHeadAndWatermark()
    {
        if (await Open() is not var (store, _)) return;
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
    [Fact]
    public async Task Append_PersistsPriorTenantId()
    {
        if (await Open() is not var (store, _)) return;
        var s = await store.EnsureStreamAsync(Source, Orders);
        var moved = Ev(s.Id, 1, 1, tenant: "tenant-b", prior: "tenant-a", op: CdcOperation.Update);
        var plain = Ev(s.Id, 2, 2, tenant: "tenant-b", prior: null, op: CdcOperation.Update);

        await store.AppendAsync(new CdcAppendBatch(s.Id, Source, Orders, 1, 0, 5, [moved, plain], []));

        var read = (await store.ReadAsync(s.Id, 1, 10)).Events;
        read[0].TenantId.ShouldBe("tenant-b");
        read[0].PriorTenantId.ShouldBe("tenant-a");
        read[1].PriorTenantId.ShouldBeNull();
    }

    [Fact]
    public async Task Append_WrongExpectedHead_Conflicts_AndWritesNothing()
    {
        if (await Open() is not var (store, _)) return;
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

    [Fact]
    public async Task Append_StaleFencingToken_IsRejected_AndWritesNothing()
    {
        if (await Open() is not var (store, _)) return;
        var s = await store.EnsureStreamAsync(Source, Orders);
        await store.AppendAsync(Batch(s, 0, 1, token: 5, watermark: 10));

        await Should.ThrowAsync<FencingTokenRejectedException>(() => store.AppendAsync(Batch(s, 1, 2, token: 4, watermark: 999)));

        var loaded = await store.GetStreamAsync(s.Id);
        loaded!.HeadOffset.ShouldBe(1);
        loaded.SourceWatermark.ShouldBe(10);
        (await store.ReadAsync(s.Id, 1, 100)).Events.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Append_EqualOrHigherToken_IsAccepted()
    {
        if (await Open() is not var (store, _)) return;
        var s = await store.EnsureStreamAsync(Source, Orders);

        await store.AppendAsync(Batch(s, 0, 1, token: 5));
        await store.AppendAsync(Batch(s, 1, 1, token: 5));
        await store.AppendAsync(Batch(s, 2, 1, token: 9));

        (await store.GetStreamAsync(s.Id))!.HeadOffset.ShouldBe(3);
    }

    [Fact]
    public async Task Append_Watermark_IsMonotonic()
    {
        if (await Open() is not var (store, _)) return;
        var s = await store.EnsureStreamAsync(Source, Orders);

        await store.AppendAsync(Batch(s, 0, 1, watermark: 100));
        await store.AppendAsync(Batch(s, 1, 1, watermark: 50));

        (await store.GetStreamAsync(s.Id))!.SourceWatermark.ShouldBe(100);
    }

    [Fact]
    public async Task Append_WithoutEvents_AdvancesWatermarkUnderFence()
    {
        if (await Open() is not var (store, _)) return;
        var s = await store.EnsureStreamAsync(Source, Orders);
        await store.AppendAsync(Batch(s, 0, 1, token: 3, watermark: 10));

        await store.AppendAsync(new CdcAppendBatch(s.Id, Source, Orders, 3, 1, 20, [], []));
        await Should.ThrowAsync<FencingTokenRejectedException>(() => store.AppendAsync(new CdcAppendBatch(s.Id, Source, Orders, 2, 1, 30, [], [])));

        var loaded = await store.GetStreamAsync(s.Id);
        loaded!.HeadOffset.ShouldBe(1);
        loaded.SourceWatermark.ShouldBe(20);
    }

    [Fact]
    public async Task Append_RejectsMalformedBatches_BeforeWriting()
    {
        if (await Open() is not var (store, _)) return;
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

    [Fact]
    public async Task Append_Images_UpsertAndDelete_LastWriteWinsWithinBatch()
    {
        if (await Open() is not var (store, _)) return;
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

    [Fact]
    public async Task GetRowImages_IsScopedToTheStream()
    {
        if (await Open() is not var (store, _)) return;
        var a = await store.EnsureStreamAsync(Source, Orders);
        var b = await store.EnsureStreamAsync(Source, Invoices);
        await store.AppendAsync(Batch(a, 0, 1, images: [new CdcRowImageWrite(Fp(1), CdcTenantScope.Row, "acme", Blob("img", 1), 1)]));

        (await store.GetRowImagesAsync(b.Id, [Fp(1)])).ShouldBeEmpty();
        (await store.GetRowImagesAsync(a.Id, [])).ShouldBeEmpty();
        (await store.GetRowImagesAsync(a.Id, [Fp(1)])).Count.ShouldBe(1);
    }

    [Fact]
    public async Task GetRowImages_HandlesLargeLookups()
    {
        if (await Open() is not var (store, _)) return;
        var s = await store.EnsureStreamAsync(Source, Orders);
        var images = Enumerable.Range(1, 130).Select(i => new CdcRowImageWrite(Fp(i), CdcTenantScope.Row, "acme", Blob("img", (byte)i), 1)).ToList();
        await store.AppendAsync(Batch(s, 0, 1, images: images));

        var loaded = await store.GetRowImagesAsync(s.Id, Enumerable.Range(1, 400).Select(Fp).ToList());

        loaded.Count.ShouldBe(130);
    }

    [Fact]
    public async Task Append_LargeBatch_IsOneAtomicRange()
    {
        if (await Open() is not var (store, _)) return;
        var s = await store.EnsureStreamAsync(Source, Orders);

        await store.AppendAsync(Batch(s, 0, 175));

        var read = await store.ReadAsync(s.Id, 1, 1000);
        read.HeadOffset.ShouldBe(175);
        read.Events.Select(e => e.Offset).ShouldBe(Enumerable.Range(1, 175).Select(i => (long)i));
    }

    [Fact]
    public async Task Read_ReturnsOrderedWindowFromOffset_AndTailHead()
    {
        if (await Open() is not var (store, _)) return;
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

    [Fact]
    public async Task Read_UnknownStream_IsEmpty()
    {
        if (await Open() is not var (store, _)) return;

        var read = await store.ReadAsync(CdcStreamId.New(), 1, 10);

        read.Events.ShouldBeEmpty();
        read.HeadOffset.ShouldBe(0);
    }

    [Fact]
    public async Task Read_NonPositiveMax_StillBounded()
    {
        if (await Open() is not var (store, _)) return;
        var s = await store.EnsureStreamAsync(Source, Orders);
        await store.AppendAsync(Batch(s, 0, 3));

        (await store.ReadAsync(s.Id, 1, 0)).Events.Count.ShouldBeLessThanOrEqualTo(1);
    }

    [Fact]
    public async Task Purge_RemovesBelowNewTail_InChunks_AndReturnsTail()
    {
        if (await Open() is not var (store, _)) return;
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

    [Fact]
    public async Task Purge_BeyondHead_CapsAtHeadPlusOne_AndAppendContinues()
    {
        if (await Open() is not var (store, _)) return;
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

    [Fact]
    public async Task Purge_StaleRetentionToken_IsRejected()
    {
        if (await Open() is not var (store, _)) return;
        var s = await store.EnsureStreamAsync(Source, Orders);
        await store.AppendAsync(Batch(s, 0, 5));
        await store.PurgeAsync(s.Id, 2, 100, 5);

        await Should.ThrowAsync<FencingTokenRejectedException>(() => store.PurgeAsync(s.Id, 4, 100, 4));

        (await store.GetStreamAsync(s.Id))!.TailOffset.ShouldBe(2);
        (await store.ReadAsync(s.Id, 1, 100)).Events.Count.ShouldBe(4);
    }

    [Fact]
    public async Task Purge_UnknownStream_ReturnsZero()
    {
        if (await Open() is not var (store, _)) return;

        (await store.PurgeAsync(CdcStreamId.New(), 5, 10, 1)).ShouldBe(0);
    }

    [Fact]
    public async Task PurgeImages_RemovesInChunks_OnlyOfTheStream()
    {
        if (await Open() is not var (store, _)) return;
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

    [Fact]
    public async Task DiscardUnreadable_RemovesEventsImagesAndKeysOfTheDeks_Only()
    {
        if (await Open() is not var (store, keys)) return;
        var s = await store.EnsureStreamAsync(Source, Orders);
        await keys.TryAddKeyAsync(Key(s.Id, "bad"));
        await keys.TryAddKeyAsync(Key(s.Id, "good"));
        await store.AppendAsync(new CdcAppendBatch(s.Id, Source, Orders, 1, 0, 1,
            [Ev(s.Id, 1, 1, "bad"), Ev(s.Id, 2, 2, "good"), Ev(s.Id, 3, 3, "bad")],
            [new CdcRowImageWrite(Fp(1), CdcTenantScope.Row, "a", Blob("bad", 1), 1), new CdcRowImageWrite(Fp(2), CdcTenantScope.Row, "a", Blob("good", 1), 2)]));

        var removed = await store.DiscardUnreadableAsync(s.Id, ["bad"]);

        removed.ShouldBe(3);
        (await store.ReadAsync(s.Id, 1, 100)).Events.Select(e => e.Offset).ShouldBe([2L]);
        (await store.GetRowImagesAsync(s.Id, [Fp(1), Fp(2)])).Select(i => i.KeyFingerprint[0]).ShouldBe([(byte)2]);
        (await keys.GetKeysAsync(s.Id)).Select(k => k.DekId).ShouldBe(["good"]);
        (await store.DiscardUnreadableAsync(s.Id, [])).ShouldBe(0);
    }

    [Fact]
    public async Task ExactlyOnce_ConcurrentProducersWithOverlappingLeases_NeverDuplicateOrSkip()
    {
        if (await Open() is not var (store, _)) return;
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

    [Fact]
    public async Task ExactlyOnce_ProducersWithDistinctTokens_StaleOnesAreFencedOut()
    {
        if (await Open() is not var (store, _)) return;
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
    [Fact]
    public async Task NoSilentLoss_ReaderSeesEveryOffsetDeliveredOrCoveredByATailGap()
    {
        if (await Open() is not var (store, _)) return;
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

    [Fact]
    public async Task Keys_RoundTrip_AndSurviveRestart()
    {
        if (await Open() is not var (store, keys)) return;
        var s = await store.EnsureStreamAsync(Source, Orders);
        var key = Key(s.Id, "dek1", CdcKeyPurpose.Image, "k7");

        (await keys.TryAddKeyAsync(key)).ShouldBeTrue();
        var restarted = (await Open())!.Value.Keys;
        var loaded = await restarted.GetKeysAsync(s.Id);

        loaded.Count.ShouldBe(1);
        loaded[0].DekId.ShouldBe("dek1");
        loaded[0].Purpose.ShouldBe(CdcKeyPurpose.Image);
        loaded[0].KekId.ShouldBe("k7");
        loaded[0].WrappedKey.ShouldBe(key.WrappedKey);
        loaded[0].CreatedAt.ShouldBe(T0);
        loaded[0].RetiredAt.ShouldBeNull();
        loaded[0].StreamId.ShouldBe(s.Id);
    }

    [Fact]
    public async Task Keys_ConcurrentAddOfTheSameDek_HasExactlyOneWinner()
    {
        if (await Open() is not var (store, keys)) return;
        var s = await store.EnsureStreamAsync(Source, Orders);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => keys.TryAddKeyAsync(Key(s.Id, "dek1")))));

        results.Count(r => r).ShouldBe(1);
        (await keys.GetKeysAsync(s.Id)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Keys_AreScopedToTheStream()
    {
        if (await Open() is not var (store, keys)) return;
        var a = await store.EnsureStreamAsync(Source, Orders);
        var b = await store.EnsureStreamAsync(Source, Invoices);
        await keys.TryAddKeyAsync(Key(a.Id, "dek1"));
        (await keys.TryAddKeyAsync(Key(b.Id, "dek1"))).ShouldBeTrue();

        (await keys.GetKeysAsync(a.Id)).Count.ShouldBe(1);
        (await keys.GetKeysAsync(b.Id)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Keys_Retire_SetsOnce_AndReplaceWrappingRewraps()
    {
        if (await Open() is not var (store, keys)) return;
        var s = await store.EnsureStreamAsync(Source, Orders);
        await keys.TryAddKeyAsync(Key(s.Id, "dek1"));

        await keys.RetireKeyAsync(s.Id, "dek1", T0.AddHours(1));
        await keys.RetireKeyAsync(s.Id, "dek1", T0.AddHours(9));
        await keys.ReplaceWrappingAsync(s.Id, "dek1", "k2", [9, 9, 9]);

        var k = (await keys.GetKeysAsync(s.Id)).Single();
        k.RetiredAt.ShouldBe(T0.AddHours(1));
        k.KekId.ShouldBe("k2");
        k.WrappedKey.ShouldBe([(byte)9, 9, 9]);
    }

    [Fact]
    public async Task Keys_DeleteUnreferencedPayloadKeys_ShredsOnlyRetiredUnreferencedPayloadKeys()
    {
        if (await Open() is not var (store, keys)) return;
        var s = await store.EnsureStreamAsync(Source, Orders);
        await keys.TryAddKeyAsync(Key(s.Id, "old-unused", retired: T0));
        await keys.TryAddKeyAsync(Key(s.Id, "old-used", retired: T0));
        await keys.TryAddKeyAsync(Key(s.Id, "active"));
        await keys.TryAddKeyAsync(Key(s.Id, "image", CdcKeyPurpose.Image, retired: T0));
        await keys.TryAddKeyAsync(Key(s.Id, "fp", CdcKeyPurpose.Fingerprint, retired: T0));
        await store.AppendAsync(Batch(s, 0, 1, dek: "old-used"));

        var removed = await keys.DeleteUnreferencedPayloadKeysAsync(s.Id);

        removed.ShouldBe(1);
        (await keys.GetKeysAsync(s.Id)).Select(k => k.DekId).ShouldBe(["old-used", "active", "image", "fp"], ignoreOrder: true);

        await store.PurgeAsync(s.Id, 2, 10, 1);
        (await keys.DeleteUnreferencedPayloadKeysAsync(s.Id)).ShouldBe(1);
        (await keys.GetKeysAsync(s.Id)).Select(k => k.DekId).ShouldNotContain("old-used");
    }

    [Fact]
    public async Task Keys_CountByKek_GroupsAcrossStreams()
    {
        if (await Open() is not var (store, keys)) return;
        var a = await store.EnsureStreamAsync(Source, Orders);
        var b = await store.EnsureStreamAsync(Source, Invoices);
        await keys.TryAddKeyAsync(Key(a.Id, "d1", kek: "k1"));
        await keys.TryAddKeyAsync(Key(a.Id, "d2", kek: "k2"));
        await keys.TryAddKeyAsync(Key(b.Id, "d1", kek: "k1"));

        var counts = await keys.CountByKekAsync();

        counts["k1"].ShouldBe(2);
        counts["k2"].ShouldBe(1);
        counts.Count.ShouldBe(2);
    }
}
