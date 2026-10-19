using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using Autheris.Application.Streaming;
using Autheris.Application.Streaming.Crypto;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.CdcStream;

/// <summary>REQ-CDC-99 I1-C: envelope crypto, AAD binding, nonce budget, rotation, fail-closed (SEC-CDC99-01, -14, -15, -16).</summary>
public sealed class CdcStreamCryptoTests
{
    private static readonly CdcStreamId Stream = new("0123456789abcdef0123456789abcdef");
    private static readonly CdcStreamId OtherStream = new("fedcba9876543210fedcba9876543210");

    private readonly FakeKeyStore _store = new();
    private readonly FakeKekProvider _keks = new FakeKekProvider().Add("k1");
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero));
    private GatewayOptions _opts = new();

    private CdcStreamKeyring Keyring()
    {
        var monitor = Substitute.For<IOptionsMonitor<GatewayOptions>>();
        monitor.CurrentValue.Returns(_ => _opts);
        return new CdcStreamKeyring(_store, _keks, monitor, _time);
    }

    private CdcStreamCrypto Crypto(CdcStreamKeyring? keyring = null) => new(keyring ?? Keyring(), NullLogger<CdcStreamCrypto>.Instance);

    private static CdcStreamEventRecord Record(CdcProtectedBlob blob, long offset = 1, CdcTenantScope scope = CdcTenantScope.Row, string? tenant = "acme", string? prior = null, CdcStreamId? stream = null) =>
        new(stream ?? Stream, offset, 7, CdcStreamEventKind.Change, CdcOperation.Update, scope, tenant, prior, blob, _timeStatic);

    private static readonly DateTimeOffset _timeStatic = DateTimeOffset.UnixEpoch;

    private static readonly byte[] Plain = Encoding.UTF8.GetBytes("{\"secret\":\"row value\"}");

    [Fact]
    public async Task Event_RoundTrips_AndCiphertextHidesPlaintext()
    {
        var crypto = Crypto();
        var blob = await crypto.ProtectEventAsync(Stream, 1, CdcStreamEventKind.Change, CdcTenantScope.Row, "acme", null, Plain);
        Encoding.UTF8.GetString(blob.Data).ShouldNotContain("row value");
        (await crypto.UnprotectEventAsync(Record(blob))).ShouldBe(Plain);
    }

    [Fact]
    public async Task EachEncryption_UsesAFreshNonce()
    {
        var crypto = Crypto();
        var a = await crypto.ProtectEventAsync(Stream, 1, CdcStreamEventKind.Change, CdcTenantScope.Row, "acme", null, Plain);
        var b = await crypto.ProtectEventAsync(Stream, 1, CdcStreamEventKind.Change, CdcTenantScope.Row, "acme", null, Plain);
        a.Data.AsSpan(0, 12).SequenceEqual(b.Data.AsSpan(0, 12)).ShouldBeFalse();
    }

    /// <summary>SEC-CDC99-01 / SEC-CDC99-16: any change of an AAD component fails authentication.</summary>
    [Theory]
    [InlineData("offset")]
    [InlineData("stream")]
    [InlineData("tenant")]
    [InlineData("prior")]
    [InlineData("scope")]
    public async Task AadTamper_FailsClosed_AndCountsIntegrityFailure(string field)
    {
        var failures = 0L;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (i, l) => { if (i.Name == "autheris_cdc_stream_integrity_failures_total") l.EnableMeasurementEvents(i); };
        listener.SetMeasurementEventCallback<long>((_, v, _, _) => Interlocked.Add(ref failures, v));
        listener.Start();

        var crypto = Crypto();
        var blob = await crypto.ProtectEventAsync(Stream, 5, CdcStreamEventKind.Change, CdcTenantScope.Row, "acme", null, Plain);
        // The wrong-stream case needs the DEK under the other stream id: copy the key rows so only the AAD differs.
        lock (_store.Keys) foreach (var k in _store.Keys.ToList()) _store.Keys.Add(k with { StreamId = OtherStream });

        var tampered = field switch
        {
            "offset" => Record(blob, offset: 6),
            "stream" => Record(blob, offset: 5, stream: OtherStream),
            "tenant" => Record(blob, offset: 5, tenant: "globex"),
            "prior" => Record(blob, offset: 5, prior: "globex"),
            _ => Record(blob, offset: 5, scope: CdcTenantScope.Shared)
        };

        await Should.ThrowAsync<CdcStreamKeyUnavailableException>(async () => await crypto.UnprotectEventAsync(tampered));
        if (field != "stream") failures.ShouldBeGreaterThan(0); // a foreign-stream DEK wrap fails as key-unavailable, not as payload integrity
        (await crypto.UnprotectEventAsync(Record(blob, offset: 5))).ShouldBe(Plain);
    }

    [Fact]
    public async Task FlippedCiphertextBit_FailsClosed()
    {
        var crypto = Crypto();
        var blob = await crypto.ProtectEventAsync(Stream, 1, CdcStreamEventKind.Change, CdcTenantScope.Row, "acme", null, Plain);
        blob.Data[^1] ^= 0x01;
        await Should.ThrowAsync<CdcStreamKeyUnavailableException>(async () => await crypto.UnprotectEventAsync(Record(blob)));
        await Should.ThrowAsync<CdcStreamKeyUnavailableException>(async () => await crypto.UnprotectEventAsync(Record(new CdcProtectedBlob(blob.DekId, new byte[5]))));
    }

    [Fact]
    public async Task UnknownDek_FailsClosed()
    {
        var crypto = Crypto();
        var blob = await crypto.ProtectEventAsync(Stream, 1, CdcStreamEventKind.Change, CdcTenantScope.Row, "acme", null, Plain);
        await Should.ThrowAsync<CdcStreamKeyUnavailableException>(async () =>
            await crypto.UnprotectEventAsync(Record(new CdcProtectedBlob(Guid.NewGuid().ToString("N"), blob.Data))));
    }

    [Fact]
    public async Task Image_RoundTrips_AndIsBoundToStreamAndFingerprint()
    {
        var crypto = Crypto();
        var fp = await crypto.FingerprintAsync(Stream, [1]);
        var blob = await crypto.ProtectImageAsync(Stream, fp, Plain);
        var rec = new CdcRowImageRecord(fp, CdcTenantScope.Row, "acme", blob, 1);
        (await crypto.UnprotectImageAsync(Stream, rec)).ShouldBe(Plain);

        var otherFp = await crypto.FingerprintAsync(Stream, [2]);
        await Should.ThrowAsync<CdcStreamKeyUnavailableException>(async () =>
            await crypto.UnprotectImageAsync(Stream, rec with { KeyFingerprint = otherFp }));
    }

    [Fact]
    public async Task Fingerprint_IsStablePerStream_DistinctPerStreamAndValue_AndUnambiguous()
    {
        var crypto = Crypto();
        var a1 = await crypto.FingerprintAsync(Stream, [1, "x"]);
        var a2 = await crypto.FingerprintAsync(Stream, [1, "x"]);
        a1.ShouldBe(a2);
        a1.Length.ShouldBe(32);
        (await crypto.FingerprintAsync(Stream, [2, "x"])).ShouldNotBe(a1);
        (await crypto.FingerprintAsync(OtherStream, [1, "x"])).ShouldNotBe(a1);
        (await crypto.FingerprintAsync(Stream, ["a|b"])).ShouldNotBe(await crypto.FingerprintAsync(Stream, ["a", "b"]));
        (await crypto.FingerprintAsync(Stream, [1L])).ShouldNotBe(await crypto.FingerprintAsync(Stream, ["1"]));
        _store.Keys.Count(k => k.Purpose == CdcKeyPurpose.Fingerprint && k.StreamId == Stream).ShouldBe(1);
    }

    /// <summary>SEC-CDC99-15: the DEK is retired after MaxEventsPerDek encryptions; old events stay readable.</summary>
    [Fact]
    public async Task NonceBudget_RetiresDekAfterMaxEvents()
    {
        _opts = new GatewayOptions { CdcStream = new CdcStreamOptions { Encryption = new CdcStreamEncryptionOptions { MaxEventsPerDek = 3 } } };
        var crypto = Crypto();
        var blobs = new List<CdcProtectedBlob>();
        for (var i = 1; i <= 7; i++)
        {
            blobs.Add(await crypto.ProtectEventAsync(Stream, i, CdcStreamEventKind.Change, CdcTenantScope.Row, "acme", null, Plain));
        }

        blobs.Select(b => b.DekId).Distinct().Count().ShouldBe(3);
        blobs.Take(3).Select(b => b.DekId).Distinct().Count().ShouldBe(1);
        for (var i = 0; i < blobs.Count; i++)
        {
            (await crypto.UnprotectEventAsync(Record(blobs[i], offset: i + 1))).ShouldBe(Plain);
        }

        _store.Keys.Count(k => k.Purpose == CdcKeyPurpose.Payload && k.RetiredAt is null).ShouldBe(1);
    }

    [Fact]
    public async Task Rotation_ByAge_RetiresDek_AndKeepsOldEventsReadable()
    {
        _opts = new GatewayOptions { CdcStream = new CdcStreamOptions { Encryption = new CdcStreamEncryptionOptions { DekRotationHours = 24 } } };
        var crypto = Crypto();
        var first = await crypto.ProtectEventAsync(Stream, 1, CdcStreamEventKind.Change, CdcTenantScope.Row, "acme", null, Plain);
        var same = await crypto.ProtectEventAsync(Stream, 2, CdcStreamEventKind.Change, CdcTenantScope.Row, "acme", null, Plain);
        same.DekId.ShouldBe(first.DekId);

        _time.Advance(TimeSpan.FromHours(24));
        var rotated = await crypto.ProtectEventAsync(Stream, 3, CdcStreamEventKind.Change, CdcTenantScope.Row, "acme", null, Plain);
        rotated.DekId.ShouldNotBe(first.DekId);
        (await crypto.UnprotectEventAsync(Record(first, offset: 1))).ShouldBe(Plain);
    }

    [Fact]
    public async Task PersistedKeys_AreWrappedOnly_AndRestartedNodeCanRead()
    {
        var crypto = Crypto();
        var blob = await crypto.ProtectEventAsync(Stream, 1, CdcStreamEventKind.Change, CdcTenantScope.Row, "acme", null, Plain);
        _store.Keys.ShouldAllBe(k => k.KekId == "k1" && k.WrappedKey.Length == 12 + 16 + 32);

        var other = Crypto(Keyring()); // fresh process, empty cache, same store and KEK
        (await other.UnprotectEventAsync(Record(blob))).ShouldBe(Plain);
    }

    [Fact]
    public async Task KeyLoss_FailsClosed_ForReadAndWrite()
    {
        var crypto = Crypto();
        var blob = await crypto.ProtectEventAsync(Stream, 1, CdcStreamEventKind.Change, CdcTenantScope.Row, "acme", null, Plain);

        _keks.Remove("k1");
        var fresh = Crypto(Keyring());
        await Should.ThrowAsync<CdcStreamKeyUnavailableException>(async () => await fresh.UnprotectEventAsync(Record(blob)));
        await Should.ThrowAsync<CdcStreamKeyUnavailableException>(async () =>
            await fresh.ProtectEventAsync(Stream, 2, CdcStreamEventKind.Change, CdcTenantScope.Row, "acme", null, Plain));
    }

    [Fact]
    public async Task NoActiveKek_FailsClosed()
    {
        var keks = new FakeKekProvider();
        var monitor = Substitute.For<IOptionsMonitor<GatewayOptions>>();
        monitor.CurrentValue.Returns(_ => _opts);
        var crypto = new CdcStreamCrypto(new CdcStreamKeyring(_store, keks, monitor, _time), NullLogger<CdcStreamCrypto>.Instance);
        var ex = await Should.ThrowAsync<CdcStreamKeyUnavailableException>(async () =>
            await crypto.ProtectEventAsync(Stream, 1, CdcStreamEventKind.Change, CdcTenantScope.Row, "acme", null, Plain));
        ex.Message.ShouldBe(CdcActivationCodes.KeyUnavailable);
    }

    [Fact]
    public async Task StoreFailure_FailsClosed()
    {
        _store.FailReads = true;
        await Should.ThrowAsync<CdcStreamKeyUnavailableException>(async () =>
            await Crypto().ProtectEventAsync(Stream, 1, CdcStreamEventKind.Change, CdcTenantScope.Row, "acme", null, Plain));
    }

    [Fact]
    public async Task WrappedDek_MovedToAnotherStream_IsRejected()
    {
        await Crypto().ProtectEventAsync(Stream, 1, CdcStreamEventKind.Change, CdcTenantScope.Row, "acme", null, Plain);
        var stolen = _store.Keys.First(k => k.Purpose == CdcKeyPurpose.Payload) with { StreamId = OtherStream };
        _store.Keys.Add(stolen);
        await Should.ThrowAsync<CdcStreamKeyUnavailableException>(async () => await Keyring().GetDekAsync(OtherStream, stolen.DekId));
    }

    [Fact]
    public async Task KekRotation_RewrapsDeks_AndOldKekCanBeRemoved()
    {
        var keyring = Keyring();
        var crypto = Crypto(keyring);
        var blob = await crypto.ProtectEventAsync(Stream, 1, CdcStreamEventKind.Change, CdcTenantScope.Row, "acme", null, Plain);
        await crypto.FingerprintAsync(Stream, [1]);

        _keks.Add("k2");
        (await keyring.RewrapAsync(Stream)).ShouldBe(2);
        _store.Keys.ShouldAllBe(k => k.KekId == "k2");
        (await keyring.RewrapAsync(Stream)).ShouldBe(0);

        _keks.Remove("k1");
        (await Crypto(Keyring()).UnprotectEventAsync(Record(blob))).ShouldBe(Plain);

        // New keys are wrapped under the new active KEK.
        var next = await Crypto(Keyring()).ProtectEventAsync(OtherStream, 1, CdcStreamEventKind.Change, CdcTenantScope.Row, "acme", null, Plain);
        _store.Keys.First(k => k.DekId == next.DekId).KekId.ShouldBe("k2");
    }

    [Fact]
    public async Task Rewrap_LeavesKeysOfARemovedKekUntouched()
    {
        var keyring = Keyring();
        await Crypto(keyring).ProtectEventAsync(Stream, 1, CdcStreamEventKind.Change, CdcTenantScope.Row, "acme", null, Plain);
        _keks.Add("k2");
        _keks.Remove("k1");
        (await keyring.RewrapAsync(Stream)).ShouldBe(0);
        _store.Keys.ShouldAllBe(k => k.KekId == "k1");
    }

    [Fact]
    public async Task ConcurrentKeyCreation_UsesTheWinnersKey()
    {
        var winnerBlob = default(CdcProtectedBlob);
        var other = Crypto(Keyring());
        _store.RejectNextAdd = true;
        _store.OnRejectedAdd = async () =>
            winnerBlob = await other.ProtectEventAsync(Stream, 1, CdcStreamEventKind.Change, CdcTenantScope.Row, "acme", null, Plain);

        var blob = await Crypto(Keyring()).ProtectEventAsync(Stream, 2, CdcStreamEventKind.Change, CdcTenantScope.Row, "acme", null, Plain);

        blob.DekId.ShouldBe(winnerBlob!.DekId);
        _store.Keys.Count(k => k.Purpose == CdcKeyPurpose.Payload).ShouldBe(1);
        (await Crypto(Keyring()).UnprotectEventAsync(Record(blob, offset: 2))).ShouldBe(Plain);
    }

    [Fact]
    public async Task Cache_IsBounded_AndEvictionDoesNotBreakReads()
    {
        _opts = new GatewayOptions { CdcStream = new CdcStreamOptions { Encryption = new CdcStreamEncryptionOptions { MaxCachedDeks = 2 } } };
        var keyring = Keyring();
        var crypto = Crypto(keyring);
        var blobs = new List<(CdcStreamId, CdcProtectedBlob)>();
        for (var i = 0; i < 5; i++)
        {
            var sid = new CdcStreamId(Guid.NewGuid().ToString("N"));
            blobs.Add((sid, await crypto.ProtectEventAsync(sid, 1, CdcStreamEventKind.Change, CdcTenantScope.Row, "acme", null, Plain)));
        }

        foreach (var (sid, blob) in blobs)
        {
            (await crypto.UnprotectEventAsync(Record(blob, stream: sid))).ShouldBe(Plain);
        }
    }

    [Fact]
    public async Task ImageKeyRotation_CreatesNewKey_OldImagesStayReadable()
    {
        var keyring = Keyring();
        var crypto = Crypto(keyring);
        var fp = await crypto.FingerprintAsync(Stream, [1]);
        var old = await crypto.ProtectImageAsync(Stream, fp, Plain);
        var newId = await keyring.RotateImageKeyAsync(Stream);
        newId.ShouldNotBe(old.DekId);
        var fresh = await crypto.ProtectImageAsync(Stream, fp, Plain);
        fresh.DekId.ShouldBe(newId);
        (await crypto.UnprotectImageAsync(Stream, new CdcRowImageRecord(fp, CdcTenantScope.Row, null, old, 1))).ShouldBe(Plain);
    }

    [Fact]
    public void Aad_ContainsEveryBoundComponent()
    {
        var aad = Encoding.UTF8.GetString(CdcStreamCrypto.EventAad(Stream, 9, CdcStreamEventKind.Change, CdcTenantScope.Row, "acme", "globex"));
        aad.ShouldBe($"cdc-evt|v1|{Stream.Value}|9|1|1|acme|globex");
        Encoding.UTF8.GetString(CdcStreamCrypto.ImageAad(Stream, [0xAB, 0x01])).ShouldBe($"cdc-img|v1|{Stream.Value}|ab01");
        Encoding.UTF8.GetString(CdcStreamKeyring.DekAad(Stream, CdcKeyPurpose.Payload, "d1")).ShouldBe($"cdc-dek|{Stream.Value}|payload|d1");
    }
}
