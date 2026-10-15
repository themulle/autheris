namespace Autheris.Application.Streaming.Interfaces;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>
/// Durable, ordered, per-table CDC stream (PLAN-CDC-99 D-A1, D-A4). Implemented per governance engine and in memory.
/// Offsets are dense and immutable; safety rests on the fenced watermark upsert and the head-offset compare-and-swap, not on leases.
/// </summary>
public interface ICdcStreamStore
{
    bool IsDurable { get; }

    Task<CdcStreamDescriptor> EnsureStreamAsync(string source, TableIdentifier table, CancellationToken ct = default);

    Task<CdcStreamDescriptor?> GetStreamAsync(CdcStreamId id, CancellationToken ct = default);

    Task<IReadOnlyList<CdcStreamDescriptor>> ListStreamsAsync(CancellationToken ct = default);

    Task SetStatusAsync(CdcStreamId id, CdcStreamStatus status, string? activationCode, CancellationToken ct = default);

    Task TouchPolledAsync(CdcStreamId id, DateTimeOffset at, CancellationToken ct = default);

    /// <summary>
    /// One transaction: fenced watermark upsert, fenced head CAS, event inserts, image upserts/deletes.
    /// Throws <c>FencingTokenRejectedException</c> or <see cref="CdcStreamConflictException"/>; nothing is written then.
    /// </summary>
    Task AppendAsync(CdcAppendBatch batch, CancellationToken ct = default);

    Task<IReadOnlyList<CdcRowImageRecord>> GetRowImagesAsync(CdcStreamId id, IReadOnlyList<byte[]> fingerprints, CancellationToken ct = default);

    /// <summary>TailOffset/HeadOffset are read consistently with the rows.</summary>
    Task<CdcReadResult> ReadAsync(CdcStreamId id, long fromOffsetInclusive, int maxEvents, CancellationToken ct = default);

    Task<long> PurgeAsync(CdcStreamId id, long newTailOffset, int maxRows, long fencingToken, CancellationToken ct = default);

    Task<int> PurgeImagesAsync(CdcStreamId id, int maxRows, CancellationToken ct = default);

    Task<int> DiscardUnreadableAsync(CdcStreamId id, IReadOnlyCollection<string> unreadableDekIds, CancellationToken ct = default);
}
