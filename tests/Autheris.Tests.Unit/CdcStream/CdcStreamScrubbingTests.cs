using System.Diagnostics.Metrics;
using System.Text;
using Autheris.Application.Streaming;
using Autheris.Application.Streaming.Crypto;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.CdcStream;

/// <summary>REQ-CDC-99 I1-C: no row, key or key-material sentinel in logs, exception texts or metric labels (SEC-CDC99-14, -16, -19, -20; ST-12 crypto part).</summary>
[Trait("Category", "Security")]
public sealed class CdcStreamScrubbingTests
{
    private const string Sentinel = "SENTINEL-ROW-VALUE-7731";
    private static readonly CdcStreamId Id = new("0123456789abcdef0123456789abcdef");

    private sealed class Capture : ILogger<CdcStreamCrypto>
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception) + exception);
    }

    [Fact]
    public async Task RoundTripAndIntegrityFailure_LeakNothing()
    {
        var labels = new List<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (i, l) => { if (i.Name.StartsWith("autheris_cdc_stream_")) l.EnableMeasurementEvents(i); };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) => { foreach (var t in tags) labels.Add($"{t.Key}={t.Value}"); });
        listener.Start();

        var store = new FakeKeyStore();
        var keks = new FakeKekProvider().Add("k1");
        var monitor = Substitute.For<IOptionsMonitor<GatewayOptions>>();
        monitor.CurrentValue.Returns(new GatewayOptions());
        var log = new Capture();
        var crypto = new CdcStreamCrypto(new CdcStreamKeyring(store, keks, monitor, new FakeTimeProvider()), log);

        var payload = CdcPayloadCodec.EncodeChange(new CdcChangePayload(
            new Dictionary<string, object?> { ["Id"] = Sentinel }, null, new Dictionary<string, object?> { ["Name"] = Sentinel }, false, false));
        var blob = await crypto.ProtectEventAsync(Id, 1, CdcStreamEventKind.Change, CdcTenantScope.Row, "acme", null, payload);
        var rec = new CdcStreamEventRecord(Id, 2, 1, CdcStreamEventKind.Change, CdcOperation.Insert, CdcTenantScope.Row, "acme", null, blob, DateTimeOffset.UnixEpoch);
        var ex = await Should.ThrowAsync<CdcStreamKeyUnavailableException>(async () => await crypto.UnprotectEventAsync(rec)); // wrong offset -> integrity failure

        var haystack = string.Join("\n", log.Lines.Append(ex.ToString()).Append(string.Join(",", labels)));
        haystack.ShouldNotContain(Sentinel);
        haystack.ShouldNotContain(Convert.ToBase64String(store.Keys[0].WrappedKey));
        haystack.ShouldNotContain(Convert.ToHexStringLower(store.Keys[0].WrappedKey));
        labels.ShouldAllBe(l => l == $"stream={Id.Value}");
        log.Lines.ShouldNotBeEmpty();
        Encoding.UTF8.GetString(blob.Data).ShouldNotContain(Sentinel);
    }
}
