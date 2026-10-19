namespace Autheris.Infrastructure.Persistence;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>Governed CDC stream persistence (PLAN-CDC-99 CdcStreams) on the PostgreSql governance database; statements live in <see cref="CdcStreamSqlCore"/>.</summary>
public partial class PostgreSqlGovernanceRepository : ICdcStreamStore
{
    public bool IsDurable => true;

    private static readonly CdcStreamSqlCore Core = new(CdcSqlDialect.PostgreSql);

    public async Task<CdcStreamDescriptor> EnsureStreamAsync(string source, TableIdentifier table, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await Core.EnsureStreamAsync(conn, source, table, ct).ConfigureAwait(false);
    }

    public async Task<CdcStreamDescriptor?> GetStreamAsync(CdcStreamId id, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await Core.GetStreamAsync(conn, id, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CdcStreamDescriptor>> ListStreamsAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await Core.ListStreamsAsync(conn, ct).ConfigureAwait(false);
    }

    public async Task SetStatusAsync(CdcStreamId id, CdcStreamStatus status, string? activationCode, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await Core.SetStatusAsync(conn, id, status, activationCode, ct).ConfigureAwait(false);
    }

    public async Task TouchPolledAsync(CdcStreamId id, DateTimeOffset at, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await Core.TouchPolledAsync(conn, id, at, ct).ConfigureAwait(false);
    }

    public async Task AppendAsync(CdcAppendBatch batch, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await Core.AppendAsync(conn, batch, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CdcRowImageRecord>> GetRowImagesAsync(CdcStreamId id, IReadOnlyList<byte[]> fingerprints, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await Core.GetRowImagesAsync(conn, id, fingerprints, ct).ConfigureAwait(false);
    }

    public async Task<CdcReadResult> ReadAsync(CdcStreamId id, long fromOffsetInclusive, int maxEvents, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await Core.ReadAsync(conn, id, fromOffsetInclusive, maxEvents, ct).ConfigureAwait(false);
    }

    public async Task<long> PurgeAsync(CdcStreamId id, long newTailOffset, int maxRows, long fencingToken, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await Core.PurgeAsync(conn, id, newTailOffset, maxRows, fencingToken, ct).ConfigureAwait(false);
    }

    public async Task<int> PurgeImagesAsync(CdcStreamId id, int maxRows, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await Core.PurgeImagesAsync(conn, id, maxRows, ct).ConfigureAwait(false);
    }

    public async Task<int> DiscardUnreadableAsync(CdcStreamId id, IReadOnlyCollection<string> unreadableDekIds, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await Core.DiscardUnreadableAsync(conn, id, unreadableDekIds, ct).ConfigureAwait(false);
    }
}
