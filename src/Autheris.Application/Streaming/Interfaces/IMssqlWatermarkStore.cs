namespace Autheris.Application.Streaming.Interfaces;

using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;

/// <summary>
/// Checkpoint store for MSSQL Change Tracking watermarks (SYS_CHANGE_VERSION) per table. Writes are fenced: a writer that
/// lost its lease to a newer owner is rejected and can never move the watermark again.
/// </summary>
public interface IMssqlWatermarkStore
{
    /// <summary>True when the watermark survives a process restart. CDC refuses to start outside Development with a non-durable store.</summary>
    bool IsDurable { get; }

    /// <summary>The stored watermark, or <c>null</c> when none was ever stored (a fresh start, not "version 0").</summary>
    ValueTask<long?> GetWatermarkAsync(TableIdentifier table, CancellationToken ct = default);

    /// <summary>
    /// Advances the watermark to <c>max(stored, watermark)</c> under <paramref name="fencingToken"/>. Returns <c>false</c>
    /// when a newer fencing token already wrote the watermark (the caller lost its lease).
    /// </summary>
    ValueTask<bool> TryAdvanceWatermarkAsync(TableIdentifier table, long watermark, long fencingToken, CancellationToken ct = default);
}
