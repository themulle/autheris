namespace Autheris.Infrastructure.Persistence;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>Governed CDC stream persistence (PLAN-CDC-99 CdcStreamKeys) on the Sqlite governance database; statements live in <see cref="CdcStreamSqlCore"/>.</summary>
public partial class SqliteGovernanceRepository : ICdcStreamKeyStore
{
    public Task<IReadOnlyList<CdcWrappedKey>> GetKeysAsync(CdcStreamId id, CancellationToken ct = default) =>
        ReadAsync(async c => await Core.GetKeysAsync(c, id, ct).ConfigureAwait(false), ct);

    public Task<bool> TryAddKeyAsync(CdcWrappedKey key, CancellationToken ct = default) =>
        WriteAsync(async c => await Core.TryAddKeyAsync(c, key, ct).ConfigureAwait(false), ct);

    public Task RetireKeyAsync(CdcStreamId id, string dekId, DateTimeOffset at, CancellationToken ct = default) =>
        WriteAsync<int>(async c => { await Core.RetireKeyAsync(c, id, dekId, at, ct).ConfigureAwait(false); return 0; }, ct);

    public Task ReplaceWrappingAsync(CdcStreamId id, string dekId, string kekId, byte[] wrappedKey, CancellationToken ct = default) =>
        WriteAsync<int>(async c => { await Core.ReplaceWrappingAsync(c, id, dekId, kekId, wrappedKey, ct).ConfigureAwait(false); return 0; }, ct);

    public Task<int> DeleteUnreferencedPayloadKeysAsync(CdcStreamId id, CancellationToken ct = default) =>
        WriteAsync(async c => await Core.DeleteUnreferencedPayloadKeysAsync(c, id, ct).ConfigureAwait(false), ct);

    public Task<IReadOnlyDictionary<string, int>> CountByKekAsync(CancellationToken ct = default) =>
        ReadAsync(async c => await Core.CountByKekAsync(c, ct).ConfigureAwait(false), ct);
}
