using Autheris.Application.Hosting;
using Autheris.Application.State;
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

/// <summary>REQ-CDC-99 I1-C: retention bounds, chunking, fencing, DEK shredding, KEK re-wrap (D-A11, AC-9).</summary>
public sealed class CdcStreamRetentionJobTests
{
    private static readonly CdcStreamId Id = new("0123456789abcdef0123456789abcdef");
    private static readonly TableIdentifier Table = new("finance", "dbo", "invoices");

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeStreamStore _store = new(Id, Table);
    private readonly FakeKeyStore _keys = new();
    private readonly FakeKekProvider _keks = new FakeKekProvider().Add("k1");
    private GatewayOptions _opts = new();
    private readonly IOptionsMonitor<GatewayOptions> _monitor = Substitute.For<IOptionsMonitor<GatewayOptions>>();

    public CdcStreamRetentionJobTests() => _monitor.CurrentValue.Returns(_ => _opts);

    private CdcStreamRetentionJob Job() =>
        new(_store, _keys, new CdcStreamKeyring(_keys, _keks, _monitor, _time), _monitor, NullLogger<CdcStreamRetentionJob>.Instance, _time);

    private static ClusterJobContext Ctx(long token = 7) => new(token, Substitute.For<IDistributedLease>());

    private void Fill(int count, TimeSpan spacing)
    {
        for (var i = 1; i <= count; i++)
        {
            _store.Add(new CdcStreamEventRecord(Id, i, i, CdcStreamEventKind.Change, CdcOperation.Insert, CdcTenantScope.Row, "acme", null,
                new CdcProtectedBlob("d1", new byte[30]), _time.GetUtcNow() - (count - i) * spacing));
        }
    }

    [Fact]
    public async Task AgeBound_PurgesOnlyExpiredEvents()
    {
        _opts = new GatewayOptions { CdcStream = new CdcStreamOptions { RetentionHours = 72 } };
        // 10 events, one every 10 hours: ages 90,80,...,0 -> events at 90 and 80 hours are expired, 70 hours stays.
        Fill(10, TimeSpan.FromHours(10));
        await Job().ExecuteAsync(Ctx(), default);
        _store.Descriptor.TailOffset.ShouldBe(3);
        _store.Events.Select(e => e.Offset).ShouldBe(Enumerable.Range(3, 8).Select(i => (long)i));
    }

    [Fact]
    public async Task CountBound_KeepsOnlyTheNewestEvents()
    {
        _opts = new GatewayOptions { CdcStream = new CdcStreamOptions { RetentionHours = 1000, RetentionMaxEvents = 4 } };
        Fill(10, TimeSpan.FromMinutes(1));
        await Job().ExecuteAsync(Ctx(), default);
        _store.Events.Select(e => e.Offset).ShouldBe([7L, 8, 9, 10]);
        _store.Descriptor.TailOffset.ShouldBe(7);
    }

    [Fact]
    public async Task AllExpired_EmptiesTheStream_TailIsHeadPlusOne()
    {
        _opts = new GatewayOptions { CdcStream = new CdcStreamOptions { RetentionHours = 1 } };
        Fill(5, TimeSpan.FromHours(5));
        _time.Advance(TimeSpan.FromHours(100));
        await Job().ExecuteAsync(Ctx(), default);
        _store.Events.ShouldBeEmpty();
        _store.Descriptor.TailOffset.ShouldBe(6);
        _store.Descriptor.HeadOffset.ShouldBe(5);
    }

    [Fact]
    public async Task NothingExpired_PurgesNothing()
    {
        Fill(5, TimeSpan.FromMinutes(1));
        await Job().ExecuteAsync(Ctx(), default);
        _store.Events.Count.ShouldBe(5);
        _store.PurgeTokens.ShouldBeEmpty();
    }

    [Fact]
    public async Task Purge_RunsInBoundedChunks_WithTheJobFencingToken()
    {
        _opts = new GatewayOptions { CdcStream = new CdcStreamOptions { RetentionHours = 1000, RetentionMaxEvents = 1, RetentionDeleteBatch = 4 } };
        Fill(10, TimeSpan.FromMinutes(1));
        await Job().ExecuteAsync(Ctx(token: 99), default);
        _store.PurgeBatchSizeSeen.ShouldBe(4);
        _store.PurgeTokens.Count.ShouldBe(3); // 4 + 4 + 1
        _store.PurgeTokens.ShouldAllBe(t => t == 99);
        _store.Events.Select(e => e.Offset).ShouldBe([10L]);
    }

    [Fact]
    public async Task PerTableOverride_WinsOverTheStreamDefault()
    {
        _opts = new GatewayOptions
        {
            CdcStream = new CdcStreamOptions { RetentionHours = 1000, RetentionMaxEvents = 1000 },
            MssqlChangeTracking = new MssqlChangeTrackingOptions
            {
                Tables = [new CdcTrackedTableOptions { Table = "finance.dbo.invoices", RetentionMaxEvents = 2 }]
            }
        };
        Fill(6, TimeSpan.FromMinutes(1));
        await Job().ExecuteAsync(Ctx(), default);
        _store.Events.Select(e => e.Offset).ShouldBe([5L, 6]);
    }

    [Fact]
    public async Task CryptoShredding_DeletesRetiredPayloadDeksNoRetainedEventReferences_KeepsReferencedAndOtherPurposes()
    {
        Fill(3, TimeSpan.FromMinutes(1));
        var now = _time.GetUtcNow();
        CdcWrappedKey Key(string id, CdcKeyPurpose p, DateTimeOffset? retired) => new(Id, id, p, "k1", new byte[60], now, retired);
        _keys.Keys.AddRange([Key("old", CdcKeyPurpose.Payload, now), Key("used", CdcKeyPurpose.Payload, now), Key("fp", CdcKeyPurpose.Fingerprint, null)]);
        _keys.ReferencedDeks = _ => new HashSet<string> { "used" };

        await Job().ExecuteAsync(Ctx(), default);

        _keys.Keys.Select(k => k.DekId).OrderBy(x => x).ShouldBe(["fp", "used"]);
    }

    [Fact]
    public async Task KekRotation_RewrapsDeks()
    {
        Fill(1, TimeSpan.FromMinutes(1));
        var keyring = new CdcStreamKeyring(_keys, _keks, _monitor, _time);
        await new CdcStreamCrypto(keyring, NullLogger<CdcStreamCrypto>.Instance).FingerprintAsync(Id, [1]);
        _keks.Add("k2");

        await Job().ExecuteAsync(Ctx(), default);

        _keys.Keys.ShouldAllBe(k => k.KekId == "k2");
    }

    [Fact]
    public async Task DisabledStream_PurgesImages()
    {
        await _store.SetStatusAsync(Id, CdcStreamStatus.Disabled, null);
        Fill(2, TimeSpan.FromMinutes(1));
        await Job().ExecuteAsync(Ctx(), default);
        _store.PurgeImagesCalls.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task ActiveStream_KeepsItsImages()
    {
        Fill(2, TimeSpan.FromMinutes(1));
        await Job().ExecuteAsync(Ctx(), default);
        _store.PurgeImagesCalls.ShouldBe(0);
    }

    [Fact]
    public async Task OneFailingStream_DoesNotThrow_FencingRejectionPropagates()
    {
        _opts = new GatewayOptions { CdcStream = new CdcStreamOptions { RetentionHours = 1000, RetentionMaxEvents = 1 } };
        Fill(3, TimeSpan.FromMinutes(1));
        var store = Substitute.For<Autheris.Application.Streaming.Interfaces.ICdcStreamStore>();
        store.ListStreamsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<CdcStreamDescriptor>>([_store.Descriptor]));
        store.ReadAsync(Arg.Any<CdcStreamId>(), Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns<Task<CdcReadResult>>(_ => throw new InvalidOperationException("db"));
        var job = new CdcStreamRetentionJob(store, _keys, new CdcStreamKeyring(_keys, _keks, _monitor, _time), _monitor, NullLogger<CdcStreamRetentionJob>.Instance, _time);
        await job.ExecuteAsync(Ctx(), default);

        store.PurgeAsync(Arg.Any<CdcStreamId>(), Arg.Any<long>(), Arg.Any<int>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns<Task<long>>(_ => throw new FencingTokenRejectedException("cdc:stream:retention", 1));
        store.ReadAsync(Arg.Any<CdcStreamId>(), Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CdcReadResult(1, 3, [])));
        await Should.ThrowAsync<FencingTokenRejectedException>(async () => await job.ExecuteAsync(Ctx(), default));
    }

    [Fact]
    public void Schedule_FollowsTheSweepOption_AndUsesTheDedicatedLease()
    {
        _opts = new GatewayOptions { CdcStream = new CdcStreamOptions { RetentionSweepSeconds = 120 } };
        var svc = new RetentionProbe(_monitor, _time);
        svc.Key.ShouldBe("cdc:stream:retention");
        svc.Schedule.Interval.ShouldBe(TimeSpan.FromSeconds(120));
        svc.Schedule.Enabled.ShouldBeTrue();
        svc.Schedule.LeaseTtl.ShouldBeGreaterThan(svc.Schedule.Interval);
    }

    private sealed class RetentionProbe : CdcStreamRetentionService
    {
        public RetentionProbe(IOptionsMonitor<GatewayOptions> o, TimeProvider t)
            : base(Substitute.For<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(), o, Substitute.For<IDistributedClusterStateProvider>(), NullLogger<CdcStreamRetentionService>.Instance, t)
        {
        }

        public string Key => LeaseKey;

        public ClusterSchedule Schedule => GetSchedule();
    }
}
