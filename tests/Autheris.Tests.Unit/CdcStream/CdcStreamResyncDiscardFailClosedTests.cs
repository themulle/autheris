using Autheris.Application.Streaming;
using Autheris.Application.Streaming.Crypto;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Application.Streaming.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.CdcStream;

/// <summary>INF-13 (A3): resync with discardUnreadable discards only provably permanent key failures and fails closed otherwise (A3-S1..S6).</summary>
[Trait("Category", "Security")]
public sealed class CdcStreamResyncDiscardFailClosedTests : IDisposable
{
    private static readonly CdcStreamId Id = new("0123456789abcdef0123456789abcdef");
    private static readonly TableIdentifier Table = new("finance", "dbo", "invoices");

    private readonly FakeStreamStore _descriptorStore = new(Id, Table);
    private readonly FakeKeyStore _keys = new();
    private readonly FakeKekProvider _keks = new FakeKekProvider().Add("k1");
    private readonly ICdcStreamStore _store = Substitute.For<ICdcStreamStore>();
    private readonly ICdcStreamRestarter _restarter = Substitute.For<ICdcStreamRestarter>();
    private readonly CdcStreamKeyring _keyring;

    public CdcStreamResyncDiscardFailClosedTests()
    {
        var monitor = Substitute.For<IOptionsMonitor<GatewayOptions>>();
        monitor.CurrentValue.Returns(new GatewayOptions());
        _keyring = new CdcStreamKeyring(_keys, _keks, monitor, new FakeTimeProvider());
        _store.GetStreamAsync(Id, Arg.Any<CancellationToken>()).Returns(_descriptorStore.Descriptor);
        _store.DiscardUnreadableAsync(Id, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>()).Returns(3);
        _restarter.RestartAsync(Id, Arg.Any<CancellationToken>()).Returns(true);
    }

    public void Dispose() => _keyring.Dispose();

    private CdcStreamAdminService Service() => new(_store, _keys, _keyring, _restarter);

    private void AddKey(string dekId, string kekId) =>
        _keys.Keys.Add(new CdcWrappedKey(Id, dekId, CdcKeyPurpose.Payload, kekId, new byte[60], DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch));

    private async Task AddReadableKeyAsync()
    {
        var crypto = new CdcStreamCrypto(_keyring, Microsoft.Extensions.Logging.Abstractions.NullLogger<CdcStreamCrypto>.Instance);
        await crypto.ProtectEventAsync(Id, 1, CdcStreamEventKind.Change, CdcTenantScope.Row, "acme", null, new byte[] { 1 });
    }

    private async Task AssertNothingDiscardedAsync()
    {
        await _store.DidNotReceiveWithAnyArgs().DiscardUnreadableAsync(default, default!, default);
        await _restarter.DidNotReceiveWithAnyArgs().RestartAsync(default, default);
    }

    [Fact]
    public async Task Resync_Discard_SecretProviderThrows_AbortsKeyUnavailable_NoDiscard_NoRestart()
    {
        _keks.Add("k2", active: false).MakeUnavailable("k2");
        AddKey("transient", "k2");

        var outcome = await Service().ResyncAsync(Id, true);

        outcome.Status.ShouldBe(CdcResyncStatus.KeyUnavailable);
        outcome.DiscardedEvents.ShouldBe(0);
        await AssertNothingDiscardedAsync();
    }

    [Fact]
    public async Task Resync_Discard_KeyStoreReadFails_AbortsKeyUnavailable_NoDiscard()
    {
        _keys.FailReads = true;

        var outcome = await Service().ResyncAsync(Id, true);

        outcome.Status.ShouldBe(CdcResyncStatus.KeyUnavailable);
        await AssertNothingDiscardedAsync();
    }

    [Fact]
    public async Task Resync_Discard_ActiveKekUnresolvable_AbortsKeyUnavailable_NoDiscard()
    {
        AddKey("lost", "k-gone");
        _keks.MakeUnavailable("k1");

        var outcome = await Service().ResyncAsync(Id, true);

        outcome.Status.ShouldBe(CdcResyncStatus.KeyUnavailable);
        await AssertNothingDiscardedAsync();
    }

    [Fact]
    public async Task Resync_Discard_MixedPermanentAndTransient_AbortsWholeResync()
    {
        _keks.Add("k2", active: false).MakeUnavailable("k2");
        AddKey("permanent", "k-gone");
        AddKey("transient", "k2");

        var outcome = await Service().ResyncAsync(Id, true);

        outcome.Status.ShouldBe(CdcResyncStatus.KeyUnavailable);
        await AssertNothingDiscardedAsync();
    }

    [Fact]
    public async Task Resync_Discard_KekIdRemovedFromConfig_DiscardsOnlyThatDek()
    {
        await AddReadableKeyAsync();
        AddKey("lost", "k-gone");

        var outcome = await Service().ResyncAsync(Id, true);

        outcome.Status.ShouldBe(CdcResyncStatus.Done);
        outcome.DiscardedEvents.ShouldBe(3);
        await _store.Received(1).DiscardUnreadableAsync(Id, Arg.Is<IReadOnlyCollection<string>>(ids => ids.Count == 1 && ids.Contains("lost")), Arg.Any<CancellationToken>());
        await _restarter.Received(1).RestartAsync(Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Resync_Discard_AesGcmAuthFailure_DiscardsDek()
    {
        AddKey("tampered", "k1");

        var outcome = await Service().ResyncAsync(Id, true);

        outcome.Status.ShouldBe(CdcResyncStatus.Done);
        await _store.Received(1).DiscardUnreadableAsync(Id, Arg.Is<IReadOnlyCollection<string>>(ids => ids.Single() == "tampered"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Resync_WithoutDiscard_Unchanged()
    {
        _keks.MakeUnavailable("k1");
        AddKey("transient", "k1");

        var outcome = await Service().ResyncAsync(Id, false);

        outcome.Status.ShouldBe(CdcResyncStatus.Done);
        await _store.DidNotReceiveWithAnyArgs().DiscardUnreadableAsync(default, default!, default);
        await _restarter.Received(1).RestartAsync(Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Resync_Discard_ClassificationThrowsUnexpectedException_Indeterminate_NoDiscard()
    {
        _keks.Add("k2", active: false).ThrowFor("k2", "boom");
        AddKey("odd", "k2");

        var outcome = await Service().ResyncAsync(Id, true);

        outcome.Status.ShouldBe(CdcResyncStatus.KeyUnavailable);
        await AssertNothingDiscardedAsync();
    }

    [Fact]
    public async Task Resync_Discard_Cancelled_NoDiscard_PropagatesCancellation()
    {
        AddKey("lost", "k-gone");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => Service().ResyncAsync(Id, true, cts.Token));

        await AssertNothingDiscardedAsync();
    }

    [Fact]
    public async Task Resync_Discard_KeyringStillFailsClosed_OnUnavailableOrUnknownKek()
    {
        // A3-S3: the other TryGetKek call sites never treat Unknown or Unavailable as success.
        AddKey("lost", "k-gone");
        await Should.ThrowAsync<CdcStreamKeyUnavailableException>(async () => await _keyring.GetDekAsync(Id, "lost"));

        _keks.MakeUnavailable("k1");
        await Should.ThrowAsync<CdcStreamKeyUnavailableException>(async () =>
            await _keyring.AcquireForEncryptionAsync(Id, CdcKeyPurpose.Payload));
    }
}
