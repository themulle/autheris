namespace Autheris.Infrastructure.Persistence;

/// <summary>
/// Durable, fenced storage of CDC watermarks in the governance database (WP-ARC-A-11). One row per source table; the row
/// remembers the highest fencing token that wrote it, so a writer that lost its lease can never move the watermark again.
/// </summary>
public interface ICdcWatermarkRepository
{
    /// <summary>The stored watermark, or <c>null</c> when none was ever stored for the table.</summary>
    Task<long?> GetAsync(string source, string domain, string schema, string table, CancellationToken ct = default);

    /// <summary>
    /// Atomically advances the watermark to <c>max(stored, watermark)</c> and records <paramref name="fencingToken"/>.
    /// Returns <c>false</c> (and changes nothing) when a newer fencing token already wrote the row.
    /// </summary>
    Task<bool> TryAdvanceAsync(string source, string domain, string schema, string table, long watermark, long fencingToken, CancellationToken ct = default);
}
