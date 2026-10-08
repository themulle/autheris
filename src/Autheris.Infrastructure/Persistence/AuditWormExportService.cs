namespace Autheris.Infrastructure.Persistence;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Exporter for the SHA-256 Audit Hash Chain into immutable WORM storage.
/// Verifies hash-chain integrity before export and seals archives with AWS S3 Object Lock
/// in COMPLIANCE mode or write-protected filesystem permissions.
/// </summary>
public sealed class AuditWormExportService : IAuditWormExportService
{
    private readonly IAuditLogRepository _auditRepo;
    private readonly IOptions<GatewayOptions> _options;
    private readonly HttpClient _httpClient;
    private readonly ILogger<AuditWormExportService> _logger;

    private static readonly JsonSerializerOptions IndentedJsonOptions = new() { WriteIndented = true };

    public AuditWormExportService(
        IAuditLogRepository auditRepo,
        IOptions<GatewayOptions> options,
        HttpClient httpClient,
        ILogger<AuditWormExportService> logger)
    {
        _auditRepo = auditRepo ?? throw new ArgumentNullException(nameof(auditRepo));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<WormExportResult> ExportAuditSnapshotAsync(
        DateTimeOffset windowFrom,
        DateTimeOffset windowTo,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Initiating WORM audit export snapshot for window [{From} -> {To}]", windowFrom, windowTo);

        // 1. Verify cryptographic hash chain integrity before export
        var isChainValid = await _auditRepo.VerifyAuditHashChainAsync(ct).ConfigureAwait(false);
        if (!isChainValid)
        {
            _logger.LogCritical("WORM Export aborted: SHA-256 Audit-Hash-Chain integrity verification failed (tampering detected).");
            return new WormExportResult(
                Success: false,
                RecordCount: 0,
                ManifestJson: "{}",
                ChecksumSha256: string.Empty,
                RootHash: string.Empty,
                FinalHash: string.Empty,
                RetentionUntil: DateTimeOffset.MinValue,
                DestinationLocation: string.Empty,
                ErrorMessage: "Audit hash-chain integrity verification failed: potential tampering detected.");
        }

        // 2. SEC M-31: fetch the complete, contiguous chain segment for the window (paged by rowid, no capping)
        var segment = _auditRepo is IAuditChainExportSource chainSource
            ? await LoadChainSegmentAsync(chainSource, windowFrom, windowTo, ct).ConfigureAwait(false)
            : await LoadLegacySegmentAsync(windowFrom, windowTo, ct).ConfigureAwait(false);

        if (segment.Error != null)
        {
            _logger.LogCritical("WORM Export aborted: {Error}", segment.Error);
            return new WormExportResult(
                Success: false,
                RecordCount: 0,
                ManifestJson: "{}",
                ChecksumSha256: string.Empty,
                RootHash: string.Empty,
                FinalHash: string.Empty,
                RetentionUntil: DateTimeOffset.MinValue,
                DestinationLocation: string.Empty,
                ErrorMessage: segment.Error);
        }

        var windowEntries = segment.Entries;
        if (windowEntries.Count == 0)
        {
            _logger.LogInformation("No audit log entries found for window [{From} -> {To}].", windowFrom, windowTo);
            return new WormExportResult(
                Success: true,
                RecordCount: 0,
                ManifestJson: "{}",
                ChecksumSha256: string.Empty,
                RootHash: string.Empty,
                FinalHash: string.Empty,
                RetentionUntil: DateTimeOffset.UtcNow,
                DestinationLocation: "NONE");
        }

        var rootHash = windowEntries[0].PrevHash;
        var finalHash = windowEntries[^1].EntryHash;

        // 3. Serialize and compute payload checksum
        var payloadJson = segment.Records != null
            ? JsonSerializer.Serialize(segment.Records.Select(r => new { rowId = r.RowId, sequence = r.Sequence, entry = r.Entry }), IndentedJsonOptions)
            : JsonSerializer.Serialize(windowEntries, IndentedJsonOptions);
        var payloadBytes = Encoding.UTF8.GetBytes(payloadJson);
        var checksumSha256 = Convert.ToHexStringLower(SHA256.HashData(payloadBytes));

        var wormOpts = _options.Value.Audit.Worm;
        var retentionDays = wormOpts.RetentionDays > 0 ? wormOpts.RetentionDays : 3650;
        var retentionUntil = DateTimeOffset.UtcNow.AddDays(retentionDays);

        var anchor = segment.Anchor;
        var manifestObj = new
        {
            exportId = Guid.NewGuid().ToString("D"),
            exportedAt = DateTimeOffset.UtcNow,
            windowFrom = windowFrom,
            windowTo = windowTo,
            recordCount = windowEntries.Count,
            expectedRecordCount = segment.ExpectedCount,
            firstRowId = segment.Records is { Count: > 0 } ? (long?)segment.Records[0].RowId : null,
            lastRowId = segment.Records is { Count: > 0 } ? (long?)segment.Records[^1].RowId : null,
            firstSequence = segment.Records is { Count: > 0 } ? segment.Records[0].Sequence : null,
            lastSequence = segment.Records is { Count: > 0 } ? segment.Records[^1].Sequence : null,
            continuityVerified = true,
            payloadSha256 = checksumSha256,
            rootHash,
            finalHash,
            chainAnchor = anchor == null ? null : new
            {
                sequence = anchor.Sequence,
                entryHash = anchor.EntryHash,
                updatedAt = anchor.UpdatedAt,
                signature = anchor.Signature
            },
            retentionUntil,
            objectLockMode = wormOpts.ObjectLockMode,
            complianceRegulation = "SEC-Rule-17a-4 / BaFin-MaRisk / GDPR-Art-30"
        };
        var manifestJson = JsonSerializer.Serialize(manifestObj, IndentedJsonOptions);

        // 4. Dispatch to configured storage destination (Local WORM vs S3 Object Lock)
        var fileName = $"audit_worm_{windowFrom:yyyyMMddTHHmmssZ}_{windowTo:yyyyMMddTHHmmssZ}_{checksumSha256[..8]}.json";
        var manifestFileName = $"audit_worm_{windowFrom:yyyyMMddTHHmmssZ}_{windowTo:yyyyMMddTHHmmssZ}_{checksumSha256[..8]}.manifest.json";
        string destinationLocation;

        if (string.Equals(wormOpts.StorageType, "S3", StringComparison.OrdinalIgnoreCase))
        {
            destinationLocation = await ExportToS3Async(fileName, payloadBytes, manifestFileName, manifestJson, retentionUntil, wormOpts, ct).ConfigureAwait(false);
        }
        else
        {
            destinationLocation = await ExportToLocalWormAsync(fileName, payloadBytes, manifestFileName, manifestJson, wormOpts, ct).ConfigureAwait(false);
        }

        _logger.LogInformation("Successfully completed WORM audit export of {Count} records to {Dest}. Retention until: {Retention}",
            windowEntries.Count, destinationLocation, retentionUntil);

        return new WormExportResult(
            Success: true,
            RecordCount: windowEntries.Count,
            ManifestJson: manifestJson,
            ChecksumSha256: checksumSha256,
            RootHash: rootHash,
            FinalHash: finalHash,
            RetentionUntil: retentionUntil,
            DestinationLocation: destinationLocation);
    }

    private const int ExportPageSize = 1000;
    private const int LegacyQueryCap = 5000;

    private sealed record ChainSegment(
        IReadOnlyList<AuditLogEntry> Entries,
        IReadOnlyList<AuditChainRecord>? Records,
        long ExpectedCount,
        AuditChainAnchor? Anchor,
        string? Error);

    private static async Task<ChainSegment> LoadChainSegmentAsync(
        IAuditChainExportSource source,
        DateTimeOffset windowFrom,
        DateTimeOffset windowTo,
        CancellationToken ct)
    {
        var anchor = source.GetVerifiedChainAnchor();
        var range = await source.GetAuditChainRangeAsync(windowFrom, windowTo, ct).ConfigureAwait(false);
        if (range == null)
        {
            return new ChainSegment(Array.Empty<AuditLogEntry>(), Array.Empty<AuditChainRecord>(), 0, anchor, null);
        }

        var records = new List<AuditChainRecord>();
        var afterRowId = range.FirstRowId - 1;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var page = await source.GetAuditChainPageAsync(afterRowId, range.LastRowId, ExportPageSize, ct).ConfigureAwait(false);
            if (page.Count == 0)
            {
                break;
            }

            records.AddRange(page);
            afterRowId = page[^1].RowId;
            if (afterRowId >= range.LastRowId)
            {
                break;
            }
        }

        if (records.Count != range.Count)
        {
            return new ChainSegment(Array.Empty<AuditLogEntry>(), null, range.Count, anchor,
                $"Audit export incomplete: expected {range.Count} entries in rowid range [{range.FirstRowId}..{range.LastRowId}], read {records.Count}.");
        }

        for (var i = 1; i < records.Count; i++)
        {
            if (!string.Equals(records[i].Entry.PrevHash, records[i - 1].Entry.EntryHash, StringComparison.Ordinal))
            {
                return new ChainSegment(Array.Empty<AuditLogEntry>(), null, range.Count, anchor,
                    $"Audit export continuity check failed at rowid {records[i].RowId}: PrevHash does not match the preceding entry.");
            }

            var prevSeq = records[i - 1].Sequence;
            var curSeq = records[i].Sequence;
            if ((prevSeq.HasValue && curSeq.HasValue && curSeq.Value != prevSeq.Value + 1) ||
                (prevSeq.HasValue && !curSeq.HasValue))
            {
                return new ChainSegment(Array.Empty<AuditLogEntry>(), null, range.Count, anchor,
                    $"Audit export sequence gap detected at rowid {records[i].RowId}.");
            }
        }

        return new ChainSegment(records.Select(r => r.Entry).ToList(), records, range.Count, anchor, null);
    }

    private async Task<ChainSegment> LoadLegacySegmentAsync(DateTimeOffset windowFrom, DateTimeOffset windowTo, CancellationToken ct)
    {
        var allEntries = await _auditRepo.QueryAuditLogsAsync(since: windowFrom, limit: LegacyQueryCap, ct: ct).ConfigureAwait(false);
        if (allEntries.Count >= LegacyQueryCap)
        {
            // SEC M-31: never report success for a possibly capped result.
            return new ChainSegment(Array.Empty<AuditLogEntry>(), null, allEntries.Count, null,
                $"Audit export window contains {LegacyQueryCap} or more entries; the repository does not support gap-free paged export.");
        }

        var windowEntries = allEntries
            .Where(e => e.OccurredAt >= windowFrom && e.OccurredAt <= windowTo)
            .OrderBy(e => e.OccurredAt)
            .ToList();

        for (var i = 1; i < windowEntries.Count; i++)
        {
            if (!string.Equals(windowEntries[i].PrevHash, windowEntries[i - 1].EntryHash, StringComparison.Ordinal))
            {
                return new ChainSegment(Array.Empty<AuditLogEntry>(), null, windowEntries.Count, null,
                    $"Audit export continuity check failed at entry {windowEntries[i].Id}: PrevHash does not match the preceding entry.");
            }
        }

        return new ChainSegment(windowEntries, null, windowEntries.Count, null, null);
    }

    private async Task<string> ExportToLocalWormAsync(
        string fileName,
        byte[] payloadBytes,
        string manifestFileName,
        string manifestJson,
        WormAuditOptions wormOpts,
        CancellationToken ct)
    {
        var basePath = !string.IsNullOrWhiteSpace(wormOpts.ExportPath)
            ? wormOpts.ExportPath
            : Path.Combine(AppContext.BaseDirectory, "worm_archives");

        Directory.CreateDirectory(basePath);

        var dataFilePath = Path.Combine(basePath, fileName);
        var manifestFilePath = Path.Combine(basePath, manifestFileName);

        await File.WriteAllBytesAsync(dataFilePath, payloadBytes, ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(manifestFilePath, manifestJson, ct).ConfigureAwait(false);

        // Enforce write-once / read-only filesystem protection
        try
        {
            File.SetAttributes(dataFilePath, FileAttributes.ReadOnly);
            File.SetAttributes(manifestFilePath, FileAttributes.ReadOnly);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to apply FileAttributes.ReadOnly to local WORM files.");
        }

        return dataFilePath;
    }

    private async Task<string> ExportToS3Async(
        string fileName,
        byte[] payloadBytes,
        string manifestFileName,
        string manifestJson,
        DateTimeOffset retentionUntil,
        WormAuditOptions wormOpts,
        CancellationToken ct)
    {
        var endpoint = !string.IsNullOrWhiteSpace(wormOpts.S3Endpoint) ? wormOpts.S3Endpoint.TrimEnd('/') : "https://s3.amazonaws.com";
        var bucket = !string.IsNullOrWhiteSpace(wormOpts.S3Bucket) ? wormOpts.S3Bucket : "audit-worm-bucket";
        var prefix = !string.IsNullOrWhiteSpace(wormOpts.S3Prefix) ? wormOpts.S3Prefix.Trim('/') + "/" : "audit-worm/";

        bool hasCredentials = !string.IsNullOrWhiteSpace(wormOpts.S3AccessKey) && !string.IsNullOrWhiteSpace(wormOpts.S3SecretKey);
        if (!hasCredentials && !_options.Value.AreUnsignedS3RequestsAllowed)
        {
            throw new System.Security.SecurityException("S3 WORM export requires signed requests (S3AccessKey/S3SecretKey). Unsigned requests are only allowed with warn_allow_unsigned_s3_requests.");
        }

        var dataUri = new Uri($"{endpoint}/{bucket}/{prefix}{fileName}");
        using var putRequest = new HttpRequestMessage(HttpMethod.Put, dataUri);
        putRequest.Content = new ByteArrayContent(payloadBytes);
        putRequest.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        if (wormOpts.EnforceObjectLock)
        {
            putRequest.Headers.TryAddWithoutValidation("x-amz-object-lock-mode", wormOpts.ObjectLockMode);
            putRequest.Headers.TryAddWithoutValidation("x-amz-object-lock-retain-until-date", retentionUntil.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
        }

        if (hasCredentials)
        {
            SignS3Request(putRequest, payloadBytes, wormOpts.S3AccessKey, wormOpts.S3SecretKey);
        }

        var response = await _httpClient.SendAsync(putRequest, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        // Also upload manifest
        var manifestUri = new Uri($"{endpoint}/{bucket}/{prefix}{manifestFileName}");
        using var putManifestRequest = new HttpRequestMessage(HttpMethod.Put, manifestUri);
        var manifestBytes = Encoding.UTF8.GetBytes(manifestJson);
        putManifestRequest.Content = new ByteArrayContent(manifestBytes);
        putManifestRequest.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        if (wormOpts.EnforceObjectLock)
        {
            putManifestRequest.Headers.TryAddWithoutValidation("x-amz-object-lock-mode", wormOpts.ObjectLockMode);
            putManifestRequest.Headers.TryAddWithoutValidation("x-amz-object-lock-retain-until-date", retentionUntil.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
        }

        if (hasCredentials)
        {
            SignS3Request(putManifestRequest, manifestBytes, wormOpts.S3AccessKey, wormOpts.S3SecretKey);
        }

        var manifestResponse = await _httpClient.SendAsync(putManifestRequest, ct).ConfigureAwait(false);
        manifestResponse.EnsureSuccessStatusCode();

        return dataUri.ToString();
    }

    private static void SignS3Request(
        HttpRequestMessage request,
        byte[] contentBytes,
        string accessKey,
        string secretKey,
        string region = "us-east-1")
    {
        var now = DateTimeOffset.UtcNow;
        var amzDate = now.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        var dateStamp = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

        var contentSha256 = Convert.ToHexStringLower(SHA256.HashData(contentBytes));

        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", contentSha256);

        var host = request.RequestUri!.Authority;
        request.Headers.Host = host;

        var headersToSign = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["host"] = host,
            ["x-amz-content-sha256"] = contentSha256,
            ["x-amz-date"] = amzDate
        };

        foreach (var header in request.Headers)
        {
            var lowerKey = header.Key.ToLowerInvariant();
            if (lowerKey.StartsWith("x-amz-", StringComparison.Ordinal))
            {
                headersToSign[lowerKey] = string.Join(",", header.Value);
            }
        }

        var canonicalHeaders = string.Join("\n", headersToSign.Select(kv => $"{kv.Key}:{kv.Value.Trim()}")) + "\n";
        var signedHeaders = string.Join(";", headersToSign.Keys);

        var canonicalUri = string.IsNullOrEmpty(request.RequestUri.AbsolutePath) ? "/" : request.RequestUri.AbsolutePath;
        var canonicalQuery = string.Empty;

        var canonicalRequest = $"{request.Method.Method}\n{canonicalUri}\n{canonicalQuery}\n{canonicalHeaders}\n{signedHeaders}\n{contentSha256}";
        var canonicalRequestHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest)));

        var credentialScope = $"{dateStamp}/{region}/s3/aws4_request";
        var stringToSign = $"AWS4-HMAC-SHA256\n{amzDate}\n{credentialScope}\n{canonicalRequestHash}";

        var kSecret = Encoding.UTF8.GetBytes("AWS4" + secretKey);
        var kDate = HMACSHA256.HashData(kSecret, Encoding.UTF8.GetBytes(dateStamp));
        var kRegion = HMACSHA256.HashData(kDate, Encoding.UTF8.GetBytes(region));
        var kService = HMACSHA256.HashData(kRegion, Encoding.UTF8.GetBytes("s3"));
        var kSigning = HMACSHA256.HashData(kService, Encoding.UTF8.GetBytes("aws4_request"));

        var signature = Convert.ToHexStringLower(HMACSHA256.HashData(kSigning, Encoding.UTF8.GetBytes(stringToSign)));

        var authHeader = $"AWS4-HMAC-SHA256 Credential={accessKey}/{credentialScope}, SignedHeaders={signedHeaders}, Signature={signature}";
        request.Headers.TryAddWithoutValidation("Authorization", authHeader);
    }
}
