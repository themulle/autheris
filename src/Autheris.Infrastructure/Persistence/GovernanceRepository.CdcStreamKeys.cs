namespace Autheris.Infrastructure.Persistence;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Domain.Model;

public partial class GovernanceRepository : ICdcStreamKeyStore
{
    public async Task<IReadOnlyList<CdcWrappedKey>> GetKeysAsync(CdcStreamId id, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await CdcCore.GetKeysAsync(conn, id, ct).ConfigureAwait(false);
    }

    public async Task<bool> TryAddKeyAsync(CdcWrappedKey key, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await CdcCore.TryAddKeyAsync(conn, key, ct).ConfigureAwait(false);
    }

    public async Task RetireKeyAsync(CdcStreamId id, string dekId, DateTimeOffset at, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await CdcCore.RetireKeyAsync(conn, id, dekId, at, ct).ConfigureAwait(false);
    }

    public async Task ReplaceWrappingAsync(CdcStreamId id, string dekId, string kekId, byte[] wrappedKey, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await CdcCore.ReplaceWrappingAsync(conn, id, dekId, kekId, wrappedKey, ct).ConfigureAwait(false);
    }

    public async Task<int> DeleteUnreferencedPayloadKeysAsync(CdcStreamId id, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await CdcCore.DeleteUnreferencedPayloadKeysAsync(conn, id, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<string, int>> CountByKekAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await CdcCore.CountByKekAsync(conn, ct).ConfigureAwait(false);
    }
}
