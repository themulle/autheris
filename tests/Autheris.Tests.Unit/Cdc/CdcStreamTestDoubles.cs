namespace Autheris.Tests.Unit.Cdc;

using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.State;
using Autheris.Application.Streaming;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Extensions.Cdc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

/// <summary>An in-memory <see cref="ICdcStreamStore"/> with the fencing and compare-and-swap semantics of the durable stores.</summary>
internal sealed class FakeCdcStreamStore : ICdcStreamStore
{
    private readonly object _gate = new();

    public sealed class StreamState
    {
        public required CdcStreamId Id { get; init; }
        public required string Source { get; init; }
        public required TableIdentifier Table { get; init; }
        public CdcStreamStatus Status { get; set; } = CdcStreamStatus.Active;
        public string? ActivationCode { get; set; }
        public long Head { get; set; }
        public long Tail { get; set; } = 1;
        public long? Watermark { get; set; }
        public string? WatermarkState { get; set; }
        public long WatermarkToken { get; set; }
        public long ProducerToken { get; set; }
        public DateTimeOffset? LastPolled { get; set; }
        public List<CdcStreamEventRecord> Events { get; } = [];
        public Dictionary<string, CdcRowImageRecord> Images { get; } = new(StringComparer.Ordinal);
    }

    public Dictionary<CdcStreamId, StreamState> Streams { get; } = [];

    public int AppendCalls { get; private set; }

    public int TouchCalls { get; private set; }

    public bool IsDurable => true;

    public StreamState Only => Streams.Values.Single();

    public Task<CdcStreamDescriptor> EnsureStreamAsync(string source, TableIdentifier table, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var state = Streams.Values.FirstOrDefault(s => s.Source == source && s.Table.Equals(table));
            if (state is null)
            {
                state = new StreamState { Id = CdcStreamId.New(), Source = source, Table = table };
                Streams[state.Id] = state;
            }

            return Task.FromResult(Describe(state));
        }
    }

    public Task<CdcStreamDescriptor?> GetStreamAsync(CdcStreamId id, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(Streams.TryGetValue(id, out var s) ? Describe(s) : null);
        }
    }

    public Task<IReadOnlyList<CdcStreamDescriptor>> ListStreamsAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<CdcStreamDescriptor>>(Streams.Values.Select(Describe).ToList());
        }
    }

    public Task SetStatusAsync(CdcStreamId id, CdcStreamStatus status, string? activationCode, CancellationToken ct = default)
    {
        lock (_gate)
        {
            Streams[id].Status = status;
            Streams[id].ActivationCode = activationCode;
        }

        return Task.CompletedTask;
    }

    public Task TouchPolledAsync(CdcStreamId id, DateTimeOffset at, CancellationToken ct = default)
    {
        lock (_gate)
        {
            TouchCalls++;
            Streams[id].LastPolled = at;
        }

        return Task.CompletedTask;
    }

    public Task AppendAsync(CdcAppendBatch batch, CancellationToken ct = default)
    {
        lock (_gate)
        {
            AppendCalls++;
            var s = Streams[batch.StreamId];
            if (batch.FencingToken < s.WatermarkToken || batch.FencingToken < s.ProducerToken)
            {
                throw new FencingTokenRejectedException(s.Table.ToString(), batch.FencingToken);
            }

            if (batch.ExpectedHeadOffset != s.Head)
            {
                throw new CdcStreamConflictException();
            }

            for (var i = 0; i < batch.Events.Count; i++)
            {
                if (batch.Events[i].Offset != s.Head + 1 + i)
                {
                    throw new InvalidOperationException("offsets must be dense");
                }
            }

            s.Watermark = Math.Max(s.Watermark ?? long.MinValue, batch.NewWatermark);
            if (batch.WatermarkState is not null)
            {
                s.WatermarkState = batch.WatermarkState;
            }
            s.WatermarkToken = batch.FencingToken;
            s.ProducerToken = batch.FencingToken;
            s.Head += batch.Events.Count;
            s.Events.AddRange(batch.Events);
            foreach (var w in batch.ImageWrites)
            {
                var key = Convert.ToHexString(w.KeyFingerprint);
                if (w.Image is null)
                {
                    s.Images.Remove(key);
                }
                else
                {
                    s.Images[key] = new CdcRowImageRecord(w.KeyFingerprint, w.TenantScope, w.TenantId, w.Image, w.Offset);
                }
            }
        }

        return Task.CompletedTask;
    }

    public Task<string?> GetWatermarkStateAsync(CdcStreamId id, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(Streams.TryGetValue(id, out var s) ? s.WatermarkState : null);
        }
    }

    public Task<MultiWatermarkVectorRecord?> GetWatermarkVectorAsync(CdcStreamId id, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (Streams.TryGetValue(id, out var s))
            {
                var vector = MultiWatermarkVector.FromJson(s.WatermarkState);
                return Task.FromResult<MultiWatermarkVectorRecord?>(new MultiWatermarkVectorRecord(id, vector, s.WatermarkToken));
            }
            return Task.FromResult<MultiWatermarkVectorRecord?>(null);
        }
    }

    public Task ResetWatermarkAsync(CdcStreamId id, string? newWatermarkStateJson, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (Streams.TryGetValue(id, out var s))
            {
                s.WatermarkState = newWatermarkStateJson;
                s.Watermark = null;
            }
            return Task.CompletedTask;
        }
    }

    public Task<IReadOnlyList<CdcRowImageRecord>> GetRowImagesAsync(CdcStreamId id, IReadOnlyList<byte[]> fingerprints, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var s = Streams[id];
            return Task.FromResult<IReadOnlyList<CdcRowImageRecord>>(
                fingerprints.Select(f => s.Images.GetValueOrDefault(Convert.ToHexString(f))).Where(r => r is not null).Select(r => r!).ToList());
        }
    }

    public Task<CdcReadResult> ReadAsync(CdcStreamId id, long fromOffsetInclusive, int maxEvents, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var s = Streams[id];
            return Task.FromResult(new CdcReadResult(s.Tail, s.Head, s.Events.Where(e => e.Offset >= fromOffsetInclusive).Take(maxEvents).ToList()));
        }
    }

    public Task<long> PurgeAsync(CdcStreamId id, long newTailOffset, int maxRows, long fencingToken, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<int> PurgeImagesAsync(CdcStreamId id, int maxRows, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<int> DiscardUnreadableAsync(CdcStreamId id, IReadOnlyCollection<string> unreadableDekIds, CancellationToken ct = default) => throw new NotSupportedException();

    private static CdcStreamDescriptor Describe(StreamState s) =>
        new(s.Id, s.Source, s.Table, s.Status, s.ActivationCode, s.Head, s.Tail, s.Watermark, s.LastPolled);
}

/// <summary>Fault injection around a store: simulates a producer crash before or after the append transaction commits.</summary>
internal sealed class FaultInjectingStreamStore(ICdcStreamStore inner) : ICdcStreamStore
{
    public bool ThrowBeforeAppend { get; set; }

    public bool ThrowAfterAppend { get; set; }

    /// <summary>Runs just before the append reaches the inner store (another producer commits in between).</summary>
    public Func<Task>? BeforeAppend { get; set; }

    public bool IsDurable => inner.IsDurable;

    public Task<CdcStreamDescriptor> EnsureStreamAsync(string source, TableIdentifier table, CancellationToken ct = default) => inner.EnsureStreamAsync(source, table, ct);

    public Task<CdcStreamDescriptor?> GetStreamAsync(CdcStreamId id, CancellationToken ct = default) => inner.GetStreamAsync(id, ct);

    public Task<IReadOnlyList<CdcStreamDescriptor>> ListStreamsAsync(CancellationToken ct = default) => inner.ListStreamsAsync(ct);

    public Task SetStatusAsync(CdcStreamId id, CdcStreamStatus status, string? activationCode, CancellationToken ct = default) => inner.SetStatusAsync(id, status, activationCode, ct);

    public Task TouchPolledAsync(CdcStreamId id, DateTimeOffset at, CancellationToken ct = default) => inner.TouchPolledAsync(id, at, ct);

    public async Task AppendAsync(CdcAppendBatch batch, CancellationToken ct = default)
    {
        if (ThrowBeforeAppend)
        {
            throw new InvalidOperationException("injected crash before the append commits");
        }

        if (BeforeAppend is { } hook)
        {
            await hook();
        }

        await inner.AppendAsync(batch, ct);
        if (ThrowAfterAppend)
        {
            throw new InvalidOperationException("injected crash after the append committed");
        }
    }

    public Task<IReadOnlyList<CdcRowImageRecord>> GetRowImagesAsync(CdcStreamId id, IReadOnlyList<byte[]> fingerprints, CancellationToken ct = default) => inner.GetRowImagesAsync(id, fingerprints, ct);

    public Task<string?> GetWatermarkStateAsync(CdcStreamId id, CancellationToken ct = default) => inner.GetWatermarkStateAsync(id, ct);

    public Task<MultiWatermarkVectorRecord?> GetWatermarkVectorAsync(CdcStreamId id, CancellationToken ct = default) => inner.GetWatermarkVectorAsync(id, ct);

    public Task ResetWatermarkAsync(CdcStreamId id, string? newWatermarkStateJson, CancellationToken ct = default) => inner.ResetWatermarkAsync(id, newWatermarkStateJson, ct);

    public Task<CdcReadResult> ReadAsync(CdcStreamId id, long fromOffsetInclusive, int maxEvents, CancellationToken ct = default) => inner.ReadAsync(id, fromOffsetInclusive, maxEvents, ct);

    public Task<long> PurgeAsync(CdcStreamId id, long newTailOffset, int maxRows, long fencingToken, CancellationToken ct = default) => inner.PurgeAsync(id, newTailOffset, maxRows, fencingToken, ct);

    public Task<int> PurgeImagesAsync(CdcStreamId id, int maxRows, CancellationToken ct = default) => inner.PurgeImagesAsync(id, maxRows, ct);

    public Task<int> DiscardUnreadableAsync(CdcStreamId id, IReadOnlyCollection<string> unreadableDekIds, CancellationToken ct = default) => inner.DiscardUnreadableAsync(id, unreadableDekIds, ct);
}

/// <summary>Test crypto: AES-256-GCM with a fixed key and the offset/tenant bound into the AAD; HMAC fingerprints per stream.</summary>
internal sealed class TestCdcCrypto : ICdcStreamCrypto
{
    private static readonly byte[] Key = SHA256.HashData(Encoding.UTF8.GetBytes("test-cdc-kek"));

    public bool KeysUnavailable { get; set; }

    public ValueTask<CdcProtectedBlob> ProtectEventAsync(CdcStreamId id, long offset, CdcStreamEventKind kind, CdcTenantScope scope, string? tenantId, string? priorTenantId, ReadOnlyMemory<byte> plaintext, CancellationToken ct = default) =>
        ValueTask.FromResult(Seal($"evt|{id.Value}|{offset}|{kind}|{scope}|{tenantId}|{priorTenantId}", plaintext.Span));

    public ValueTask<byte[]> UnprotectEventAsync(CdcStreamEventRecord record, CancellationToken ct = default) =>
        ValueTask.FromResult(Open($"evt|{record.StreamId.Value}|{record.Offset}|{record.Kind}|{record.TenantScope}|{record.TenantId}|{record.PriorTenantId}", record.Payload));

    public ValueTask<CdcProtectedBlob> ProtectImageAsync(CdcStreamId id, byte[] fingerprint, ReadOnlyMemory<byte> plaintext, CancellationToken ct = default) =>
        ValueTask.FromResult(Seal($"img|{id.Value}|{Convert.ToHexString(fingerprint)}", plaintext.Span));

    public ValueTask<byte[]> UnprotectImageAsync(CdcStreamId id, CdcRowImageRecord image, CancellationToken ct = default) =>
        ValueTask.FromResult(Open($"img|{id.Value}|{Convert.ToHexString(image.KeyFingerprint)}", image.Image));

    public ValueTask<byte[]> FingerprintAsync(CdcStreamId id, IReadOnlyList<object?> keyValues, CancellationToken ct = default)
    {
        if (KeysUnavailable)
        {
            throw new CdcStreamKeyUnavailableException();
        }

        var canonical = id.Value + "|" + string.Join("|", keyValues.Select(v => $"{v?.GetType().Name}:{v}".Length + ":" + $"{v?.GetType().Name}:{v}"));
        return ValueTask.FromResult(HMACSHA256.HashData(Key, Encoding.UTF8.GetBytes(canonical)));
    }

    private CdcProtectedBlob Seal(string aad, ReadOnlySpan<byte> plaintext)
    {
        if (KeysUnavailable)
        {
            throw new CdcStreamKeyUnavailableException();
        }

        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[plaintext.Length];
        using var gcm = new AesGcm(Key, 16);
        gcm.Encrypt(nonce, plaintext, cipher, tag, Encoding.UTF8.GetBytes(aad));
        return new CdcProtectedBlob("dek-test", [.. nonce, .. tag, .. cipher]);
    }

    private static byte[] Open(string aad, CdcProtectedBlob blob)
    {
        var data = blob.Data;
        var plain = new byte[data.Length - 28];
        using var gcm = new AesGcm(Key, 16);
        gcm.Decrypt(data.AsSpan(0, 12), data.AsSpan(28), data.AsSpan(12, 16), plain, Encoding.UTF8.GetBytes(aad));
        return plain;
    }
}

/// <summary>A tagged-JSON test codec; a value of an unknown CLR type is <see cref="NotSupportedException"/> like the closed-tag production codec.</summary>
internal sealed class TestPayloadCodec : ICdcPayloadCodec
{
    public byte[] SerializeChange(CdcChangePayload payload) => JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
    {
        ["key"] = Encode(payload.Key),
        ["before"] = payload.Before is null ? null : Encode(payload.Before),
        ["after"] = payload.After is null ? null : Encode(payload.After),
        ["beforeUnknown"] = payload.BeforeUnknown,
        ["oversize"] = payload.PayloadOversize
    });

    public byte[] SerializeControl(CdcControlPayload payload) => JsonSerializer.SerializeToUtf8Bytes(payload);

    public byte[] SerializeImage(IReadOnlyDictionary<string, object?> row) => JsonSerializer.SerializeToUtf8Bytes(Encode(row));

    public IReadOnlyDictionary<string, object?> DeserializeImage(ReadOnlySpan<byte> plaintext) =>
        Decode(JsonSerializer.Deserialize<Dictionary<string, string?>>(plaintext)!);

    public static CdcChangePayload ReadChange(byte[] plaintext)
    {
        using var doc = JsonDocument.Parse(plaintext);
        var root = doc.RootElement;
        IReadOnlyDictionary<string, object?>? Obj(string name) =>
            root.GetProperty(name).ValueKind == JsonValueKind.Null
                ? null
                : Decode(root.GetProperty(name).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind == JsonValueKind.Null ? null : p.Value.GetString()));
        return new CdcChangePayload(Obj("key")!, Obj("before"), Obj("after"), root.GetProperty("beforeUnknown").GetBoolean(), root.GetProperty("oversize").GetBoolean());
    }

    private static Dictionary<string, string?> Encode(IReadOnlyDictionary<string, object?> row) =>
        row.ToDictionary(kv => kv.Key, kv => kv.Value switch
        {
            null => null,
            int i => "i32:" + i,
            long l => "i64:" + l,
            string s => "s:" + s,
            bool b => "b:" + b,
            decimal d => "dec:" + d.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => throw new NotSupportedException(kv.Value.GetType().Name)
        });

    private static Dictionary<string, object?> Decode(Dictionary<string, string?> encoded) =>
        encoded.ToDictionary(kv => kv.Key, kv => kv.Value is null ? null : DecodeValue(kv.Value));

    private static object DecodeValue(string v)
    {
        var i = v.IndexOf(':');
        var tag = v[..i];
        var text = v[(i + 1)..];
        return tag switch
        {
            "i32" => int.Parse(text, System.Globalization.CultureInfo.InvariantCulture),
            "i64" => long.Parse(text, System.Globalization.CultureInfo.InvariantCulture),
            "s" => text,
            "b" => bool.Parse(text),
            "dec" => decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture),
            _ => throw new NotSupportedException(tag)
        };
    }
}

/// <summary>A signal that records the heads it was told about.</summary>
internal sealed class RecordingStreamSignal : ICdcStreamSignal
{
    public List<(CdcStreamId Id, long Head)> Notified { get; } = [];

    public bool Throws { get; set; }

    public ValueTask NotifyAdvancedAsync(CdcStreamId id, long headOffset, CancellationToken ct = default)
    {
        if (Throws)
        {
            throw new InvalidOperationException("signal down");
        }

        Notified.Add((id, headOffset));
        return ValueTask.CompletedTask;
    }

    public IAsyncDisposable Subscribe(Func<CdcStreamId, long, ValueTask> onAdvanced) => throw new NotSupportedException();
}

