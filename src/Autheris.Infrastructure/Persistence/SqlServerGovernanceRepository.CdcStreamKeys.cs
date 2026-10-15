namespace Autheris.Infrastructure.Persistence;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>Governed CDC stream persistence (PLAN-CDC-99 CdcStreamKeys) on the SqlServer governance database; statements live in <see cref="CdcStreamSqlCore"/>.</summary>
public partial class SqlServerGovernanceRepository : ICdcStreamKeyStore
{
    public async Task<IReadOnlyList<CdcWrappedKey>> GetKeysAsync(CdcStreamId id, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await Core.GetKeysAsync(conn, id, ct).ConfigureAwait(false);
    }

    public async Task<bool> TryAddKeyAsync(CdcWrappedKey key, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await Core.TryAddKeyAsync(conn, key, ct).ConfigureAwait(false);
    }

    public async Task RetireKeyAsync(CdcStreamId id, string dekId, DateTimeOffset at, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await Core.RetireKeyAsync(conn, id, dekId, at, ct).ConfigureAwait(false);
    }

    public async Task ReplaceWrappingAsync(CdcStreamId id, string dekId, string kekId, byte[] wrappedKey, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await Core.ReplaceWrappingAsync(conn, id, dekId, kekId, wrappedKey, ct).ConfigureAwait(false);
    }

    public async Task<int> DeleteUnreferencedPayloadKeysAsync(CdcStreamId id, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await Core.DeleteUnreferencedPayloadKeysAsync(conn, id, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<string, int>> CountByKekAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await Core.CountByKekAsync(conn, ct).ConfigureAwait(false);
    }
}
