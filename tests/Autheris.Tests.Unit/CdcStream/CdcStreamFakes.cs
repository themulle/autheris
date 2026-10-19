using System.Collections.Concurrent;
using System.Security.Cryptography;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Domain.Model;

namespace Autheris.Tests.Unit.CdcStream;

/// <summary>In-memory key store used by I1-C tests (the production stores belong to I1-A).</summary>
internal sealed class FakeKeyStore : ICdcStreamKeyStore
{
    public readonly List<CdcWrappedKey> Keys = [];
    public bool FailReads;
    public bool RejectNextAdd;
    public Func<Task>? OnRejectedAdd;

    public Task<IReadOnlyList<CdcWrappedKey>> GetKeysAsync(CdcStreamId id, CancellationToken ct = default)
    {
        if (FailReads) throw new InvalidOperationException("store down");
        lock (Keys) return Task.FromResult<IReadOnlyList<CdcWrappedKey>>(Keys.Where(k => k.StreamId == id).ToList());
    }

    public async Task<bool> TryAddKeyAsync(CdcWrappedKey key, CancellationToken ct = default)
    {
        if (RejectNextAdd)
        {
            RejectNextAdd = false;
            if (OnRejectedAdd is not null) await OnRejectedAdd();
            return false;
        }

        lock (Keys) Keys.Add(key);
        return true;
    }

    public Task RetireKeyAsync(CdcStreamId id, string dekId, DateTimeOffset at, CancellationToken ct = default)
    {
        lock (Keys)
        {
            var i = Keys.FindIndex(k => k.StreamId == id && k.DekId == dekId);
            if (i >= 0) Keys[i] = Keys[i] with { RetiredAt = at };
        }
        return Task.CompletedTask;
    }

    public Task ReplaceWrappingAsync(CdcStreamId id, string dekId, string kekId, byte[] wrappedKey, CancellationToken ct = default)
    {
        lock (Keys)
        {
            var i = Keys.FindIndex(k => k.StreamId == id && k.DekId == dekId);
            if (i >= 0) Keys[i] = Keys[i] with { KekId = kekId, WrappedKey = wrappedKey };
        }
        return Task.CompletedTask;
    }

    public Func<CdcStreamId, IReadOnlySet<string>>? ReferencedDeks { get; set; }

    public Task<int> DeleteUnreferencedPayloadKeysAsync(CdcStreamId id, CancellationToken ct = default)
    {
        lock (Keys)
        {
            var referenced = ReferencedDeks?.Invoke(id) ?? new HashSet<string>();
            var n = Keys.RemoveAll(k => k.StreamId == id && k.Purpose == CdcKeyPurpose.Payload && k.RetiredAt is not null && !referenced.Contains(k.DekId));
            return Task.FromResult(n);
        }
    }

    public Task<IReadOnlyDictionary<string, int>> CountByKekAsync(CancellationToken ct = default)
    {
        lock (Keys) return Task.FromResult<IReadOnlyDictionary<string, int>>(Keys.GroupBy(k => k.KekId).ToDictionary(g => g.Key, g => g.Count()));
    }
}

internal sealed class FakeKekProvider : ICdcKeyEncryptionKeyProvider
{
    private readonly ConcurrentDictionary<string, byte[]> _keys = new();

    public string? ActiveKekId { get; set; }

    public FakeKekProvider Add(string id, bool active = true)
    {
        _keys[id] = RandomNumberGenerator.GetBytes(32);
        if (active) ActiveKekId = id;
        return this;
    }

    public void Remove(string id) => _keys.TryRemove(id, out _);

    public bool TryGetKek(string kekId, out ReadOnlyMemory<byte> key)
    {
        if (_keys.TryGetValue(kekId, out var k)) { key = k; return true; }
        key = default;
        return false;
    }
}

/// <summary>Minimal in-memory stream for hub and retention tests (the production stores belong to I1-A).</summary>
internal sealed class FakeStreamStore : ICdcStreamStore
{
    private readonly object _gate = new();
    public readonly List<CdcStreamEventRecord> Events = [];
    public CdcStreamDescriptor Descriptor;
    public List<long> PurgeTokens { get; } = [];
    public int PurgeImagesCalls;
    public int PurgeBatchSizeSeen;

    public FakeStreamStore(CdcStreamId id, Autheris.Domain.Common.TableIdentifier table, CdcStreamStatus status = CdcStreamStatus.Active) =>
        Descriptor = new CdcStreamDescriptor(id, "mssql_ct", table, status, null, 0, 1, null, null);

    public bool IsDurable => false;

    public void Add(CdcStreamEventRecord record)
    {
        lock (_gate)
        {
            Events.Add(record);
            Descriptor = Descriptor with { HeadOffset = Math.Max(Descriptor.HeadOffset, record.Offset) };
        }
    }

    public Task<CdcStreamDescriptor> EnsureStreamAsync(string source, Autheris.Domain.Common.TableIdentifier table, CancellationToken ct = default) => Task.FromResult(Descriptor);

    public Task<CdcStreamDescriptor?> GetStreamAsync(CdcStreamId id, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult<CdcStreamDescriptor?>(id == Descriptor.Id ? Descriptor : null);
    }

    public Task<IReadOnlyList<CdcStreamDescriptor>> ListStreamsAsync(CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<CdcStreamDescriptor>>([Descriptor]);
    }

    public Task SetStatusAsync(CdcStreamId id, CdcStreamStatus status, string? activationCode, CancellationToken ct = default)
    {
        lock (_gate) Descriptor = Descriptor with { Status = status, ActivationCode = activationCode };
        return Task.CompletedTask;
    }

    public Task TouchPolledAsync(CdcStreamId id, DateTimeOffset at, CancellationToken ct = default) => Task.CompletedTask;

    public Task AppendAsync(CdcAppendBatch batch, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<CdcRowImageRecord>> GetRowImagesAsync(CdcStreamId id, IReadOnlyList<byte[]> fingerprints, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CdcRowImageRecord>>([]);

    public Task<string?> GetWatermarkStateAsync(CdcStreamId id, CancellationToken ct = default) => Task.FromResult<string?>(null);

    public Task<MultiWatermarkVectorRecord?> GetWatermarkVectorAsync(CdcStreamId id, CancellationToken ct = default) => Task.FromResult<MultiWatermarkVectorRecord?>(null);

    public Task ResetWatermarkAsync(CdcStreamId id, string? watermarkState, CancellationToken ct = default) => Task.CompletedTask;

    public Task<CdcReadResult> ReadAsync(CdcStreamId id, long fromOffsetInclusive, int maxEvents, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var rows = Events.Where(e => e.Offset >= fromOffsetInclusive).OrderBy(e => e.Offset).Take(maxEvents).ToList();
            return Task.FromResult(new CdcReadResult(Descriptor.TailOffset, Descriptor.HeadOffset, rows));
        }
    }

    public Task<long> PurgeAsync(CdcStreamId id, long newTailOffset, int maxRows, long fencingToken, CancellationToken ct = default)
    {
        lock (_gate)
        {
            PurgeTokens.Add(fencingToken);
            PurgeBatchSizeSeen = maxRows;
            var victims = Events.Where(e => e.Offset < newTailOffset).OrderBy(e => e.Offset).Take(maxRows).ToList();
            foreach (var v in victims) Events.Remove(v);
            var tail = Events.Count == 0 ? Descriptor.HeadOffset + 1 : Events.Min(e => e.Offset);
            Descriptor = Descriptor with { TailOffset = Math.Max(Descriptor.TailOffset, Math.Min(tail, newTailOffset)) };
            return Task.FromResult((long)victims.Count);
        }
    }

    public Task<int> PurgeImagesAsync(CdcStreamId id, int maxRows, CancellationToken ct = default)
    {
        PurgeImagesCalls++;
        return Task.FromResult(0);
    }

    public Task<int> DiscardUnreadableAsync(CdcStreamId id, IReadOnlyCollection<string> unreadableDekIds, CancellationToken ct = default) => Task.FromResult(0);
}
