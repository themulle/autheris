namespace Autheris.Application.Interfaces;

using System;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Result of an immutable WORM (Write Once, Read Many) export containing cryptographic hash chain manifest
/// and compliance retention locking metadata.
/// </summary>
public sealed record WormExportResult(
    bool Success,
    int RecordCount,
    string ManifestJson,
    string ChecksumSha256,
    string RootHash,
    string FinalHash,
    DateTimeOffset RetentionUntil,
    string DestinationLocation,
    string? ErrorMessage = null);

/// <summary>
/// Exporter for the SHA-256 tamper-evident Audit Hash Chain into immutable WORM storage
/// (e.g. AWS S3 Object Lock in COMPLIANCE mode or write-protected filesystem archives).
/// Ensures BaFin / SEC Rule 17a-4 / GDPR Article 30 audit compliance.
/// </summary>
public interface IAuditWormExportService
{
    Task<WormExportResult> ExportAuditSnapshotAsync(
        DateTimeOffset windowFrom,
        DateTimeOffset windowTo,
        CancellationToken ct = default);
}
