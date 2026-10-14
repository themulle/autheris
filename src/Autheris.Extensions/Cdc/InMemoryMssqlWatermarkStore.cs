namespace Autheris.Extensions.Cdc;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Domain.Common;

/// <summary>
/// In-memory thread-safe implementation of <see cref="IMssqlWatermarkStore"/> with the same fencing semantics as the durable
/// store. Not durable and therefore not registered by the host; kept for tests.
/// </summary>
public sealed class InMemoryMssqlWatermarkStore : IMssqlWatermarkStore
{
    private readonly object _gate = new();
    private readonly Dictionary<TableIdentifier, (long Watermark, long Token)> _state = [];

    public bool IsDurable => false;

    public ValueTask<long?> GetWatermarkAsync(TableIdentifier table, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return ValueTask.FromResult<long?>(_state.TryGetValue(table, out var entry) ? entry.Watermark : null);
        }
    }

    public ValueTask<bool> TryAdvanceWatermarkAsync(TableIdentifier table, long watermark, long fencingToken, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_state.TryGetValue(table, out var existing))
            {
                if (fencingToken < existing.Token)
                {
                    return ValueTask.FromResult(false);
                }

                _state[table] = (Math.Max(existing.Watermark, watermark), fencingToken);
            }
            else
            {
                _state[table] = (watermark, fencingToken);
            }

            return ValueTask.FromResult(true);
        }
    }
}
