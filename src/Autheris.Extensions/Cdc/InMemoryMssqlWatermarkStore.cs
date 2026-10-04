namespace Autheris.Extensions.Cdc;

using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Domain.Common;
using Autheris.Application.Interfaces;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;

/// <summary>
/// In-memory thread-safe implementation of IMssqlWatermarkStore.
/// </summary>
public sealed class InMemoryMssqlWatermarkStore : IMssqlWatermarkStore
{
    private readonly ConcurrentDictionary<TableIdentifier, long> _watermarks = new();

    public ValueTask<long> GetWatermarkAsync(TableIdentifier table, CancellationToken ct = default)
    {
        _watermarks.TryGetValue(table, out var version);
        return ValueTask.FromResult(version);
    }

    public ValueTask SetWatermarkAsync(TableIdentifier table, long watermark, CancellationToken ct = default)
    {
        _watermarks.AddOrUpdate(table, watermark, (_, existing) => System.Math.Max(existing, watermark));
        return ValueTask.CompletedTask;
    }
}
