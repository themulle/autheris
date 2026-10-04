namespace Autheris.Application.Streaming.Interfaces;

using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;

/// <summary>
/// Checkpoint store for MSSQL Change Tracking watermarks (SYS_CHANGE_VERSION) per table.
/// </summary>
public interface IMssqlWatermarkStore
{
    ValueTask<long> GetWatermarkAsync(TableIdentifier table, CancellationToken ct = default);
    ValueTask SetWatermarkAsync(TableIdentifier table, long watermark, CancellationToken ct = default);
}
