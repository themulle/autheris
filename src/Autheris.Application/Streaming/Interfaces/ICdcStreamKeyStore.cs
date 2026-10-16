namespace Autheris.Application.Streaming.Interfaces;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Model;

/// <summary>Persistence of wrapped per-stream data-encryption keys (PLAN-CDC-99 D-A8). Only wrapped keys are stored.</summary>
public interface ICdcStreamKeyStore
{
    Task<IReadOnlyList<CdcWrappedKey>> GetKeysAsync(CdcStreamId id, CancellationToken ct = default);

    /// <summary>False means a concurrent add won; the caller re-reads.</summary>
    Task<bool> TryAddKeyAsync(CdcWrappedKey key, CancellationToken ct = default);

    Task RetireKeyAsync(CdcStreamId id, string dekId, DateTimeOffset at, CancellationToken ct = default);

    Task ReplaceWrappingAsync(CdcStreamId id, string dekId, string kekId, byte[] wrappedKey, CancellationToken ct = default);

    Task<int> DeleteUnreferencedPayloadKeysAsync(CdcStreamId id, CancellationToken ct = default);

    Task<IReadOnlyDictionary<string, int>> CountByKekAsync(CancellationToken ct = default);
}
