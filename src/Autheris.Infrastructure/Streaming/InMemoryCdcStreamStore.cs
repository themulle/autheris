namespace Autheris.Infrastructure.Streaming;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.State;
using Autheris.Application.Streaming;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>
/// Non-durable <see cref="ICdcStreamStore"/> and <see cref="ICdcStreamKeyStore"/> with exactly the fencing and compare-and-set semantics
/// of the governance-database stores (PLAN-CDC-99 D-A4). One lock serialises every operation, so an append is atomic. For tests and
/// Development hosts; the stream does not survive a restart (<see cref="IsDurable"/> is false).
/// </summary>
public sealed class InMemoryCdcStreamStore : ICdcStreamStore, ICdcStreamKeyStore
{
    private const int MaxReadBatch = 10_000;

    private readonly object _gate = new();
    private readonly Dictionary<string, StreamState> _streams = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Source, TableIdentifier Table), WatermarkState> _watermarks = [];
    public bool IsDurable => false;

    public Task<CdcStreamDescriptor> EnsureStreamAsync(string source, TableIdentifier table, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        lock (_gate)
        {
            var existing = _streams.Values.FirstOrDefault(s => s.Source == source && s.Table.Equals(table));
            if (existing is null)
            {
                existing = new StreamState(CdcStreamId.New(), source, table);
                _streams[existing.Id.Value] = existing;
            }

            return Task.FromResult(Describe(existing));
        }
    }

    public Task<CdcStreamDescriptor?> GetStreamAsync(CdcStreamId id, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_streams.TryGetValue(id.Value ?? string.Empty, out var s) ? Describe(s) : null);
        }
    }

    public Task<IReadOnlyList<CdcStreamDescriptor>> ListStreamsAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<CdcStreamDescriptor>>(_streams.Values.Select(Describe).ToList());
        }
    }

    public Task SetStatusAsync(CdcStreamId id, CdcStreamStatus status, string? activationCode, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_streams.TryGetValue(id.Value ?? string.Empty, out var s))
            {
                s.Status = status;
                s.ActivationCode = activationCode;
            }
        }

        return Task.CompletedTask;
    }

    public Task TouchPolledAsync(CdcStreamId id, DateTimeOffset at, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_streams.TryGetValue(id.Value ?? string.Empty, out var s))
            {
                s.LastPolledMs = at.ToUnixTimeMilliseconds();
            }
        }

        return Task.CompletedTask;
    }

    public Task AppendAsync(CdcAppendBatch batch, CancellationToken ct = default)
    {
        CdcAppendBatchValidation.Validate(batch);
        lock (_gate)
        {
            if (!_streams.TryGetValue(batch.StreamId.Value, out var s))
            {
                throw new CdcStreamConflictException();
            }

            // (a) fenced watermark upsert: a writer with a lower token than the stored one is rejected before anything changes.
            var key = (batch.Source, batch.Table);
            _watermarks.TryGetValue(key, out var wm);
            if (wm is not null && batch.FencingToken < wm.FencingToken)
            {
                throw new FencingTokenRejectedException($"cdc-stream:{batch.StreamId.Value}", batch.FencingToken);
            }

            // (b) head compare-and-set, fenced by the stream's producer token.
            if (batch.FencingToken < s.ProducerFencingToken)
            {
                throw new FencingTokenRejectedException($"cdc-stream:{batch.StreamId.Value}", batch.FencingToken);
            }

            if (s.HeadOffset != batch.ExpectedHeadOffset)
            {
                throw new CdcStreamConflictException();
            }

            // Everything validated: apply (nothing below can fail).
            _watermarks[key] = new WatermarkState(Math.Max(wm?.Watermark ?? long.MinValue, batch.NewWatermark), batch.FencingToken);
            s.HeadOffset = batch.ExpectedHeadOffset + batch.Events.Count;
            s.ProducerFencingToken = batch.FencingToken;
            foreach (var e in batch.Events)
            {
                s.Events[e.Offset] = e;
            }

            foreach (var w in batch.ImageWrites)
            {
                var fp = Convert.ToHexString(w.KeyFingerprint);
                if (w.Image is null)
                {
                    s.Images.Remove(fp);
                }
                else
                {
                    s.Images[fp] = new CdcRowImageRecord((byte[])w.KeyFingerprint.Clone(), w.TenantScope, w.TenantId, w.Image, w.Offset);
                }
            }
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<CdcRowImageRecord>> GetRowImagesAsync(CdcStreamId id, IReadOnlyList<byte[]> fingerprints, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fingerprints);
        lock (_gate)
        {
            var result = new List<CdcRowImageRecord>();
            if (_streams.TryGetValue(id.Value ?? string.Empty, out var s))
            {
                foreach (var fp in fingerprints.Select(Convert.ToHexString).Distinct())
                {
                    if (s.Images.TryGetValue(fp, out var image))
                    {
                        result.Add(image);
                    }
                }
            }

            return Task.FromResult<IReadOnlyList<CdcRowImageRecord>>(result);
        }
    }

    public Task<CdcReadResult> ReadAsync(CdcStreamId id, long fromOffsetInclusive, int maxEvents, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_streams.TryGetValue(id.Value ?? string.Empty, out var s))
            {
                return Task.FromResult(new CdcReadResult(1, 0, []));
            }

            var take = Math.Clamp(maxEvents, 1, MaxReadBatch);
            var events = s.Events.Where(kv => kv.Key >= fromOffsetInclusive).Take(take).Select(kv => kv.Value).ToList();
            return Task.FromResult(new CdcReadResult(s.TailOffset, s.HeadOffset, events));
        }
    }

    public Task<long> PurgeAsync(CdcStreamId id, long newTailOffset, int maxRows, long fencingToken, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_streams.TryGetValue(id.Value ?? string.Empty, out var s))
            {
                return Task.FromResult(0L);
            }

            if (fencingToken < s.RetentionFencingToken)
            {
                throw new FencingTokenRejectedException($"cdc-stream-retention:{id.Value}", fencingToken);
            }

            s.RetentionFencingToken = fencingToken;
            var target = Math.Min(newTailOffset, s.HeadOffset + 1);
            var effective = Math.Min(target, s.TailOffset + Math.Max(1, maxRows));
            if (effective > s.TailOffset)
            {
                foreach (var offset in s.Events.Keys.Where(o => o < effective).ToList())
                {
                    s.Events.Remove(offset);
                }

                s.TailOffset = effective;
            }

            return Task.FromResult(s.TailOffset);
        }
    }

    public Task<int> PurgeImagesAsync(CdcStreamId id, int maxRows, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_streams.TryGetValue(id.Value ?? string.Empty, out var s))
            {
                return Task.FromResult(0);
            }

            var doomed = s.Images.Keys.Take(Math.Max(1, maxRows)).ToList();
            foreach (var key in doomed)
            {
                s.Images.Remove(key);
            }

            return Task.FromResult(doomed.Count);
        }
    }

    public Task<int> DiscardUnreadableAsync(CdcStreamId id, IReadOnlyCollection<string> unreadableDekIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(unreadableDekIds);
        lock (_gate)
        {
            if (unreadableDekIds.Count == 0 || !_streams.TryGetValue(id.Value ?? string.Empty, out var s))
            {
                return Task.FromResult(0);
            }

            var deks = unreadableDekIds.ToHashSet(StringComparer.Ordinal);
            var events = s.Events.Where(kv => deks.Contains(kv.Value.Payload.DekId)).Select(kv => kv.Key).ToList();
            events.ForEach(o => s.Events.Remove(o));
            var images = s.Images.Where(kv => deks.Contains(kv.Value.Image.DekId)).Select(kv => kv.Key).ToList();
            images.ForEach(k => s.Images.Remove(k));
            s.Keys.RemoveAll(k => deks.Contains(k.DekId));
            return Task.FromResult(events.Count + images.Count);
        }
    }

    public Task<IReadOnlyList<CdcWrappedKey>> GetKeysAsync(CdcStreamId id, CancellationToken ct = default)
    {
        lock (_gate)
        {
            IReadOnlyList<CdcWrappedKey> keys = _streams.TryGetValue(id.Value ?? string.Empty, out var s)
                ? s.Keys.OrderBy(k => k.CreatedAt).ToList()
                : [];
            return Task.FromResult(keys);
        }
    }

    public Task<bool> TryAddKeyAsync(CdcWrappedKey key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_gate)
        {
            if (!_streams.TryGetValue(key.StreamId.Value ?? string.Empty, out var s) || s.Keys.Any(k => k.DekId == key.DekId))
            {
                return Task.FromResult(false);
            }

            s.Keys.Add(key with { WrappedKey = (byte[])key.WrappedKey.Clone() });
            return Task.FromResult(true);
        }
    }

    public Task RetireKeyAsync(CdcStreamId id, string dekId, DateTimeOffset at, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_streams.TryGetValue(id.Value ?? string.Empty, out var s))
            {
                var i = s.Keys.FindIndex(k => k.DekId == dekId && k.RetiredAt is null);
                if (i >= 0)
                {
                    s.Keys[i] = s.Keys[i] with { RetiredAt = at };
                }
            }
        }

        return Task.CompletedTask;
    }

    public Task ReplaceWrappingAsync(CdcStreamId id, string dekId, string kekId, byte[] wrappedKey, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_streams.TryGetValue(id.Value ?? string.Empty, out var s))
            {
                var i = s.Keys.FindIndex(k => k.DekId == dekId);
                if (i >= 0)
                {
                    s.Keys[i] = s.Keys[i] with { KekId = kekId, WrappedKey = (byte[])wrappedKey.Clone() };
                }
            }
        }

        return Task.CompletedTask;
    }

    public Task<int> DeleteUnreferencedPayloadKeysAsync(CdcStreamId id, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_streams.TryGetValue(id.Value ?? string.Empty, out var s))
            {
                return Task.FromResult(0);
            }

            var referenced = s.Events.Values.Select(e => e.Payload.DekId).ToHashSet(StringComparer.Ordinal);
            return Task.FromResult(s.Keys.RemoveAll(k => k.Purpose == CdcKeyPurpose.Payload && k.RetiredAt is not null && !referenced.Contains(k.DekId)));
        }
    }

    public Task<IReadOnlyDictionary<string, int>> CountByKekAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            IReadOnlyDictionary<string, int> counts = _streams.Values.SelectMany(s => s.Keys).GroupBy(k => k.KekId).ToDictionary(g => g.Key, g => g.Count());
            return Task.FromResult(counts);
        }
    }

    private CdcStreamDescriptor Describe(StreamState s) =>
        new(s.Id, s.Source, s.Table, s.Status, s.ActivationCode, s.HeadOffset, s.TailOffset,
            _watermarks.TryGetValue((s.Source, s.Table), out var w) ? w.Watermark : null,
            s.LastPolledMs is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null);

    private sealed record WatermarkState(long Watermark, long FencingToken);

    private sealed class StreamState(CdcStreamId id, string source, TableIdentifier table)
    {
        public CdcStreamId Id { get; } = id;

        public string Source { get; } = source;

        public TableIdentifier Table { get; } = table;

        public CdcStreamStatus Status { get; set; } = CdcStreamStatus.Active;

        public string? ActivationCode { get; set; }

        public long HeadOffset { get; set; }

        public long TailOffset { get; set; } = 1;

        public long ProducerFencingToken { get; set; }

        public long RetentionFencingToken { get; set; }

        public long? LastPolledMs { get; set; }

        public SortedDictionary<long, CdcStreamEventRecord> Events { get; } = [];

        public Dictionary<string, CdcRowImageRecord> Images { get; } = new(StringComparer.Ordinal);

        public List<CdcWrappedKey> Keys { get; } = [];
    }
}

/// <summary>The key-store face of an <see cref="InMemoryCdcStreamStore"/>: key shredding needs to know which events still reference a DEK.</summary>
public sealed class InMemoryCdcStreamKeyStore(InMemoryCdcStreamStore store) : ICdcStreamKeyStore
{
    public Task<IReadOnlyList<CdcWrappedKey>> GetKeysAsync(CdcStreamId id, CancellationToken ct = default) => store.GetKeysAsync(id, ct);

    public Task<bool> TryAddKeyAsync(CdcWrappedKey key, CancellationToken ct = default) => store.TryAddKeyAsync(key, ct);

    public Task RetireKeyAsync(CdcStreamId id, string dekId, DateTimeOffset at, CancellationToken ct = default) => store.RetireKeyAsync(id, dekId, at, ct);

    public Task ReplaceWrappingAsync(CdcStreamId id, string dekId, string kekId, byte[] wrappedKey, CancellationToken ct = default) =>
        store.ReplaceWrappingAsync(id, dekId, kekId, wrappedKey, ct);

    public Task<int> DeleteUnreferencedPayloadKeysAsync(CdcStreamId id, CancellationToken ct = default) => store.DeleteUnreferencedPayloadKeysAsync(id, ct);

    public Task<IReadOnlyDictionary<string, int>> CountByKekAsync(CancellationToken ct = default) => store.CountByKekAsync(ct);
}
