using Autheris.Application.Streaming.Interfaces;
using Autheris.Application.State;
using Autheris.Application.Streaming;
using Autheris.Application.Streaming.Crypto;
using Autheris.Application.Streaming.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.CdcStream;

/// <summary>REQ-CDC-99 I1-C: shared tail reader / compatibility feed (D-A13). SEC-CDC99-05, -17, -18.</summary>
public sealed class CdcStreamTailHubTests : IAsyncDisposable
{
    private static readonly CdcStreamId Id = new("0123456789abcdef0123456789abcdef");
    private static readonly TableIdentifier Table = new("finance", "dbo", "invoices");
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(20);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeStreamStore _store = new(Id, Table);
    private readonly FakeKeyStore _keys = new();
    private readonly FakeKekProvider _keks = new FakeKekProvider().Add("k1");
    private readonly InMemoryClusterStateProvider _state = new();
    private GatewayOptions _opts = new();
    private readonly CdcStreamCrypto _crypto;
    private readonly CdcStreamSignal _signal;
    private CdcStreamTailHub? _hub;

    public CdcStreamTailHubTests()
    {
        var monitor = Substitute.For<IOptionsMonitor<GatewayOptions>>();
        monitor.CurrentValue.Returns(_ => _opts);
        _monitor = monitor;
        _crypto = new CdcStreamCrypto(new CdcStreamKeyring(_keys, _keks, monitor, _time), NullLogger<CdcStreamCrypto>.Instance);
        _signal = new CdcStreamSignal(_state, NullLogger<CdcStreamSignal>.Instance);
    }

    private readonly IOptionsMonitor<GatewayOptions> _monitor;

    private CdcStreamTailHub Hub() => _hub ??= new CdcStreamTailHub(_store, _crypto, _signal, _monitor, NullLogger<CdcStreamTailHub>.Instance, _time);

    public async ValueTask DisposeAsync()
    {
        if (_hub is not null) await _hub.DisposeAsync();
    }

    private async Task<CdcStreamEventRecord> AppendAsync(long offset, string amount = "1", string tenant = "acme")
    {
        var payload = new CdcChangePayload(
            new Dictionary<string, object?> { ["Id"] = (int)offset }, null, new Dictionary<string, object?> { ["Amount"] = amount }, false, false);
        var blob = await _crypto.ProtectEventAsync(Id, offset, CdcStreamEventKind.Change, CdcTenantScope.Row, tenant, null, CdcPayloadCodec.EncodeChange(payload));
        var rec = new CdcStreamEventRecord(Id, offset, offset, CdcStreamEventKind.Change, CdcOperation.Insert, CdcTenantScope.Row, tenant, null, blob, _time.GetUtcNow());
        _store.Add(rec);
        return rec;
    }

    private async Task<CdcStreamDelivery> NextAsync(IAsyncEnumerator<CdcStreamDelivery> e)
    {
        (await e.MoveNextAsync().AsTask().WaitAsync(Guard)).ShouldBeTrue();
        return e.Current;
    }

    [Fact]
    public async Task Attach_DeliversDecryptedEventsInOrder_AfterSignal()
    {
        await using var e = Hub().AttachAsync(Id, null, CancellationToken.None).GetAsyncEnumerator();
        var first = e.MoveNextAsync().AsTask();
        await AppendAsync(1, "a");
        await AppendAsync(2, "b");
        await _signal.NotifyAdvancedAsync(Id, 2);

        (await first.WaitAsync(Guard)).ShouldBeTrue();
        e.Current.Record.Offset.ShouldBe(1);
        e.Current.Change!.After!["Amount"].ShouldBe("a");
        e.Current.Table.ShouldBe(Table);
        (await NextAsync(e)).Record.Offset.ShouldBe(2);
    }

    [Fact]
    public async Task Attach_StartsAtTheHead_NoHistoryReplay()
    {
        await AppendAsync(1);
        await AppendAsync(2);
        await using var e = Hub().AttachAsync(Id, null, CancellationToken.None).GetAsyncEnumerator();
        var next = e.MoveNextAsync().AsTask();
        await AppendAsync(3, "new");
        await _signal.NotifyAdvancedAsync(Id, 3);
        (await next.WaitAsync(Guard)).ShouldBeTrue();
        e.Current.Record.Offset.ShouldBe(3);
    }

    [Fact]
    public async Task Poll_DeliversWithoutSignal_WhenTimeAdvances()
    {
        await using var e = Hub().AttachAsync(Id, null, CancellationToken.None).GetAsyncEnumerator();
        var next = e.MoveNextAsync().AsTask();
        await AppendAsync(1);
        for (var i = 0; i < 200_000 && !next.IsCompleted; i++)
        {
            _time.Advance(TimeSpan.FromMilliseconds(1000));
            await Task.Yield();
        }

        (await next).ShouldBeTrue();
        e.Current.Record.Offset.ShouldBe(1);
    }

    [Fact]
    public async Task TwoSubscribers_ShareOneReader_AndGetIndependentButIdenticalDeliveries()
    {
        var hub = Hub();
        await using var a = hub.AttachAsync(Id, null, CancellationToken.None).GetAsyncEnumerator();
        await using var b = hub.AttachAsync(Id, null, CancellationToken.None).GetAsyncEnumerator();
        var ta = a.MoveNextAsync().AsTask();
        var tb = b.MoveNextAsync().AsTask();
        hub.ActiveReaders.ShouldBe(1);
        await AppendAsync(1);
        await _signal.NotifyAdvancedAsync(Id, 1);
        (await ta.WaitAsync(Guard)).ShouldBeTrue();
        (await tb.WaitAsync(Guard)).ShouldBeTrue();
        a.Current.ShouldBeSameAs(b.Current); // decrypted once, shared immutable delivery
    }

    [Fact]
    public async Task Delivery_PayloadIsImmutable()
    {
        await using var e = Hub().AttachAsync(Id, null, CancellationToken.None).GetAsyncEnumerator();
        var next = e.MoveNextAsync().AsTask();
        await AppendAsync(1);
        await _signal.NotifyAdvancedAsync(Id, 1);
        await next.WaitAsync(Guard);
        Should.Throw<NotSupportedException>(() => ((IDictionary<string, object?>)e.Current.Change!.After!)["Amount"] = "tampered");
    }

    [Fact]
    public async Task LastSubscriberLeaving_StopsTheReader()
    {
        var hub = Hub();
        var e = hub.AttachAsync(Id, null, CancellationToken.None).GetAsyncEnumerator();
        var next = e.MoveNextAsync().AsTask();
        hub.ActiveReaders.ShouldBe(1);
        await AppendAsync(1);
        await _signal.NotifyAdvancedAsync(Id, 1);
        await next.WaitAsync(Guard);
        await e.DisposeAsync();
        hub.ActiveReaders.ShouldBe(0);
    }

    [Fact]
    public async Task UnknownStream_CompletesImmediately()
    {
        var other = new CdcStreamId("fedcba9876543210fedcba9876543210");
        await foreach (var _ in Hub().AttachAsync(other, null, CancellationToken.None)) throw new InvalidOperationException("no delivery expected");
    }

    [Fact]
    public async Task Overflow_CompletesThatSubscriberWithResyncRequired_OthersUnaffected()
    {
        _opts = new GatewayOptions { CdcStream = new CdcStreamOptions { SubscriberBufferSize = 2 } };
        var hub = Hub();
        await using var slow = hub.AttachAsync(Id, null, CancellationToken.None).GetAsyncEnumerator();
        var pending = slow.MoveNextAsync().AsTask(); // registers the slow subscriber; never consumed further
        for (var i = 1; i <= 5; i++) await AppendAsync(i);
        await _signal.NotifyAdvancedAsync(Id, 5);

        // The first MoveNext completes with buffered data; drain until the overflow surfaces.
        (await pending.WaitAsync(Guard)).ShouldBeTrue();
        var ex = await Should.ThrowAsync<CdcResyncRequiredException>(async () =>
        {
            while (await slow.MoveNextAsync().AsTask().WaitAsync(Guard)) { }
        });
        ex.Message.ShouldBe("CDC_RESYNC_REQUIRED");
        ex.LastDeliveredOffset.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task FromOffset_CatchesUpFromTheStore_ThenJoinsLive_WithoutDuplicateOrHole()
    {
        await AppendAsync(1);
        await AppendAsync(2);
        await AppendAsync(3);
        await using var e = Hub().AttachAsync(Id, 2, CancellationToken.None).GetAsyncEnumerator();
        var offsets = new List<long>();
        offsets.Add((await NextAsync(e)).Record.Offset);
        offsets.Add((await NextAsync(e)).Record.Offset);
        var live = e.MoveNextAsync().AsTask();
        await AppendAsync(4);
        await _signal.NotifyAdvancedAsync(Id, 4);
        (await live.WaitAsync(Guard)).ShouldBeTrue();
        offsets.Add(e.Current.Record.Offset);
        offsets.ShouldBe([2L, 3L, 4L]);
    }

    /// <summary>SEC-CDC99-02: replay older than the resume window is refused, never silent.</summary>
    [Fact]
    public async Task FromOffset_OlderThanResumeWindow_RequiresResync()
    {
        await AppendAsync(1);
        _time.Advance(TimeSpan.FromSeconds(301));
        await AppendAsync(2);
        await using var e = Hub().AttachAsync(Id, 1, CancellationToken.None).GetAsyncEnumerator();
        await Should.ThrowAsync<CdcResyncRequiredException>(async () => await e.MoveNextAsync());
    }

    [Fact]
    public async Task FromOffset_BeyondHeadPlusOne_IsRejected()
    {
        await AppendAsync(1);
        await using var e = Hub().AttachAsync(Id, 5, CancellationToken.None).GetAsyncEnumerator();
        await Should.ThrowAsync<ArgumentOutOfRangeException>(async () => await e.MoveNextAsync());
    }

    /// <summary>SEC-CDC99-17: the number of concurrent catch-up readers per node is bounded.</summary>
    [Fact]
    public async Task CatchUp_IsBoundedPerNode()
    {
        _opts = new GatewayOptions { CdcStream = new CdcStreamOptions { MaxConcurrentCatchUps = 1 } };
        await AppendAsync(1);
        await AppendAsync(2);
        var hub = Hub();
        await using var first = hub.AttachAsync(Id, 1, CancellationToken.None).GetAsyncEnumerator();
        (await first.MoveNextAsync()).ShouldBeTrue(); // catch-up of the first still active (offset 2 pending)
        await using var second = hub.AttachAsync(Id, 1, CancellationToken.None).GetAsyncEnumerator();
        await Should.ThrowAsync<CdcResyncRequiredException>(async () => await second.MoveNextAsync());
    }

    /// <summary>SEC-CDC99-18: a range removed by retention surfaces as an explicit gap.</summary>
    [Fact]
    public async Task FromOffset_BelowTail_YieldsExplicitGap_ThenContinues()
    {
        await AppendAsync(1);
        await AppendAsync(2);
        await AppendAsync(3);
        await _store.PurgeAsync(Id, 3, 100, 1);
        await using var e = Hub().AttachAsync(Id, 1, CancellationToken.None).GetAsyncEnumerator();
        var gap = await NextAsync(e);
        gap.Record.Kind.ShouldBe(CdcStreamEventKind.Gap);
        gap.Control!.StoredVersion.ShouldBe(1);
        gap.Control.MinValidVersion.ShouldBe(3);
        gap.Change.ShouldBeNull();
        (await NextAsync(e)).Record.Offset.ShouldBe(3);
    }

    [Fact]
    public async Task LiveReader_BehindTail_EmitsGap()
    {
        await using var e = Hub().AttachAsync(Id, null, CancellationToken.None).GetAsyncEnumerator();
        var next = e.MoveNextAsync().AsTask();
        await AppendAsync(1);
        await AppendAsync(2);
        await AppendAsync(3);
        await _store.PurgeAsync(Id, 3, 100, 1);
        await _signal.NotifyAdvancedAsync(Id, 3);
        (await next.WaitAsync(Guard)).ShouldBeTrue();
        e.Current.Record.Kind.ShouldBe(CdcStreamEventKind.Gap);
        (await NextAsync(e)).Record.Offset.ShouldBe(3);
    }

    /// <summary>Test crypto that refuses one offset until released, counting the attempts.</summary>
    private sealed class GatedCrypto(ICdcStreamCrypto inner, long blockedOffset) : ICdcStreamCrypto
    {
        public volatile bool Released;
        public int BlockedAttempts;

        public ValueTask<CdcProtectedBlob> ProtectEventAsync(CdcStreamId id, long offset, CdcStreamEventKind kind, CdcTenantScope scope, string? tenantId, string? priorTenantId, ReadOnlyMemory<byte> plaintext, CancellationToken ct = default) =>
            inner.ProtectEventAsync(id, offset, kind, scope, tenantId, priorTenantId, plaintext, ct);

        public ValueTask<byte[]> UnprotectEventAsync(CdcStreamEventRecord record, CancellationToken ct = default)
        {
            if (record.Offset == blockedOffset && !Released)
            {
                Interlocked.Increment(ref BlockedAttempts);
                throw new CdcStreamKeyUnavailableException();
            }

            return inner.UnprotectEventAsync(record, ct);
        }

        public ValueTask<CdcProtectedBlob> ProtectImageAsync(CdcStreamId id, byte[] fingerprint, ReadOnlyMemory<byte> plaintext, CancellationToken ct = default) => inner.ProtectImageAsync(id, fingerprint, plaintext, ct);

        public ValueTask<byte[]> UnprotectImageAsync(CdcStreamId id, CdcRowImageRecord image, CancellationToken ct = default) => inner.UnprotectImageAsync(id, image, ct);

        public ValueTask<byte[]> FingerprintAsync(CdcStreamId id, IReadOnlyList<object?> keyValues, CancellationToken ct = default) => inner.FingerprintAsync(id, keyValues, ct);
    }

    /// <summary>Fail closed: an undecryptable event is never skipped; delivery resumes in order once it is readable.</summary>
    [Fact]
    public async Task UndecryptableEvent_IsNeverSkipped_AndDeliveredOnceReadable()
    {
        var gated = new GatedCrypto(_crypto, blockedOffset: 2);
        _hub = new CdcStreamTailHub(_store, gated, _signal, _monitor, NullLogger<CdcStreamTailHub>.Instance, _time);
        await using var e = _hub.AttachAsync(Id, null, CancellationToken.None).GetAsyncEnumerator();
        var next = e.MoveNextAsync().AsTask();
        await AppendAsync(1, "ok");
        await AppendAsync(2, "two");
        await AppendAsync(3, "three");
        await _signal.NotifyAdvancedAsync(Id, 3);

        (await next.WaitAsync(Guard)).ShouldBeTrue();
        e.Current.Record.Offset.ShouldBe(1);
        var blocked = e.MoveNextAsync().AsTask();
        for (var i = 0; i < 200_000 && Volatile.Read(ref gated.BlockedAttempts) < 3; i++)
        {
            _time.Advance(TimeSpan.FromMilliseconds(1000));
            await Task.Yield();
        }

        gated.BlockedAttempts.ShouldBeGreaterThanOrEqualTo(3);
        blocked.IsCompleted.ShouldBeFalse(); // offset 3 was not delivered past the unreadable offset 2

        gated.Released = true;
        await _signal.NotifyAdvancedAsync(Id, 3);
        (await blocked.WaitAsync(Guard)).ShouldBeTrue();
        e.Current.Record.Offset.ShouldBe(2);
        (await NextAsync(e)).Record.Offset.ShouldBe(3);
    }

    [Fact]
    public async Task ControlEvents_AreDecodedWithoutRowData()
    {
        await using var e = Hub().AttachAsync(Id, null, CancellationToken.None).GetAsyncEnumerator();
        var next = e.MoveNextAsync().AsTask();
        var blob = await _crypto.ProtectEventAsync(Id, 1, CdcStreamEventKind.WatermarkExpired, CdcTenantScope.Shared, null, null,
            CdcPayloadCodec.EncodeControl(new CdcControlPayload("watermark_expired", 1, 5, 9)));
        _store.Add(new CdcStreamEventRecord(Id, 1, 9, CdcStreamEventKind.WatermarkExpired, null, CdcTenantScope.Shared, null, null, blob, _time.GetUtcNow()));
        await _signal.NotifyAdvancedAsync(Id, 1);
        (await next.WaitAsync(Guard)).ShouldBeTrue();
        e.Current.Change.ShouldBeNull();
        e.Current.Control!.CurrentVersion.ShouldBe(9);
    }
}
