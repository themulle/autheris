namespace Autheris.Infrastructure.Persistence;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>Governed CDC stream persistence (PLAN-CDC-99 CdcStreams) on the Sqlite governance database; statements live in <see cref="CdcStreamSqlCore"/>.</summary>
public partial class SqliteGovernanceRepository : ICdcStreamStore
{
    public bool IsDurable => true;

    private static readonly CdcStreamSqlCore Core = new(CdcSqlDialect.Sqlite);

    public Task<CdcStreamDescriptor> EnsureStreamAsync(string source, TableIdentifier table, CancellationToken ct = default) =>
        WriteAsync(async c => await Core.EnsureStreamAsync(c, source, table, ct).ConfigureAwait(false), ct);

    public Task<CdcStreamDescriptor?> GetStreamAsync(CdcStreamId id, CancellationToken ct = default) =>
        ReadAsync(async c => await Core.GetStreamAsync(c, id, ct).ConfigureAwait(false), ct);

    public Task<IReadOnlyList<CdcStreamDescriptor>> ListStreamsAsync(CancellationToken ct = default) =>
        ReadAsync(async c => await Core.ListStreamsAsync(c, ct).ConfigureAwait(false), ct);

    public Task SetStatusAsync(CdcStreamId id, CdcStreamStatus status, string? activationCode, CancellationToken ct = default) =>
        WriteAsync<int>(async c => { await Core.SetStatusAsync(c, id, status, activationCode, ct).ConfigureAwait(false); return 0; }, ct);

    public Task TouchPolledAsync(CdcStreamId id, DateTimeOffset at, CancellationToken ct = default) =>
        WriteAsync<int>(async c => { await Core.TouchPolledAsync(c, id, at, ct).ConfigureAwait(false); return 0; }, ct);

    public Task AppendAsync(CdcAppendBatch batch, CancellationToken ct = default) =>
        WriteAsync<int>(async c => { await Core.AppendAsync(c, batch, ct).ConfigureAwait(false); return 0; }, ct);

    public Task<IReadOnlyList<CdcRowImageRecord>> GetRowImagesAsync(CdcStreamId id, IReadOnlyList<byte[]> fingerprints, CancellationToken ct = default) =>
        ReadAsync(async c => await Core.GetRowImagesAsync(c, id, fingerprints, ct).ConfigureAwait(false), ct);

    public Task<CdcReadResult> ReadAsync(CdcStreamId id, long fromOffsetInclusive, int maxEvents, CancellationToken ct = default) =>
        ReadAsync(async c => await Core.ReadAsync(c, id, fromOffsetInclusive, maxEvents, ct).ConfigureAwait(false), ct);

    public Task<long> PurgeAsync(CdcStreamId id, long newTailOffset, int maxRows, long fencingToken, CancellationToken ct = default) =>
        WriteAsync(async c => await Core.PurgeAsync(c, id, newTailOffset, maxRows, fencingToken, ct).ConfigureAwait(false), ct);

    public Task<int> PurgeImagesAsync(CdcStreamId id, int maxRows, CancellationToken ct = default) =>
        WriteAsync(async c => await Core.PurgeImagesAsync(c, id, maxRows, ct).ConfigureAwait(false), ct);

    public Task<int> DiscardUnreadableAsync(CdcStreamId id, IReadOnlyCollection<string> unreadableDekIds, CancellationToken ct = default) =>
        WriteAsync(async c => await Core.DiscardUnreadableAsync(c, id, unreadableDekIds, ct).ConfigureAwait(false), ct);

    public Task<string?> GetWatermarkStateAsync(CdcStreamId id, CancellationToken ct = default) =>
        ReadAsync(async c => await Core.GetWatermarkStateAsync(c, id, ct).ConfigureAwait(false), ct);

    public async Task<MultiWatermarkVectorRecord?> GetWatermarkVectorAsync(CdcStreamId id, CancellationToken ct = default)
    {
        var json = await GetWatermarkStateAsync(id, ct).ConfigureAwait(false);
        return new MultiWatermarkVectorRecord(id, MultiWatermarkVector.FromJson(json), 0);
    }

    public Task ResetWatermarkAsync(CdcStreamId id, string? watermarkState, CancellationToken ct = default) =>
        WriteAsync<int>(async c => { await Core.ResetWatermarkAsync(c, id, watermarkState, ct).ConfigureAwait(false); return 0; }, ct);
}
