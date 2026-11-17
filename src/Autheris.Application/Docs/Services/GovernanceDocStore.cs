namespace Autheris.Application.Docs.Services;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Docs.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Persisted, multi-tenant documentation store backed by <see cref="IDocumentRepository"/>
/// with synchronous search index updates via <see cref="IDocSearchService"/>.
/// </summary>
public sealed class GovernanceDocStore : IUniversalDocStore
{
    private readonly IDocumentRepository _repository;
    private readonly IDocSearchService _searchService;
    private readonly IDocIngestor _ingestor;
    private readonly ILogger<GovernanceDocStore> _logger;
    private readonly DocsOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<ManifestKey, DocAppManifest> _manifests = new(ManifestKeyComparer.Instance);

    public GovernanceDocStore(
        IDocumentRepository repository,
        IDocSearchService searchService,
        IDocIngestor ingestor,
        ILogger<GovernanceDocStore> logger,
        IOptions<DocsOptions>? docsOptions = null,
        TimeProvider? timeProvider = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _searchService = searchService ?? throw new ArgumentNullException(nameof(searchService));
        _ingestor = ingestor ?? throw new ArgumentNullException(nameof(ingestor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = docsOptions?.Value ?? new DocsOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    private DocQuota Quota => new(_options.Quota.MaxDocumentsPerTenant, _options.Quota.MaxBytesPerTenant);

    public async Task<IReadOnlyList<DocAppSummary>> ListApplicationsAsync(
        DocAccessScope scope,
        string? domain = null,
        string? tag = null,
        CancellationToken ct = default)
    {
        var visibleChunks = await _searchService.SnapshotVisibleChunksAsync(scope, ct).ConfigureAwait(false);
        var appDocs = visibleChunks
            .GroupBy(c => c.AppId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.Select(c => (Tenant: c.TenantId ?? string.Empty, Category: c.Category.ToString(), c.Id.DocId))
                    .Distinct()
                    .Count(),
                StringComparer.OrdinalIgnoreCase);

        var candidateApps = new HashSet<string>(appDocs.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var manifestKey in _manifests.Keys)
        {
            if (IsManifestVisible(manifestKey.TenantKey, scope))
            {
                candidateApps.Add(manifestKey.AppId);
            }
        }

        var summaries = new List<DocAppSummary>();
        foreach (var appId in candidateApps.OrderBy(a => a, StringComparer.OrdinalIgnoreCase))
        {
            var manifest = await GetManifestAsync(scope, appId, ct).ConfigureAwait(false);
            var docCount = appDocs.TryGetValue(appId, out var count) ? count : 0;

            var summary = new DocAppSummary(
                AppId: appId,
                Name: manifest?.Name ?? appId,
                Version: manifest?.Version ?? "1.0.0",
                Domain: manifest?.Domain ?? "default",
                Owner: manifest?.Owner ?? "unassigned",
                GovernanceTier: manifest?.GovernanceTier ?? "Tier-2",
                DocumentCount: docCount,
                Tags: manifest?.Tags ?? Array.Empty<string>());

            if (!string.IsNullOrWhiteSpace(domain) &&
                !string.Equals(summary.Domain, domain, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(tag) &&
                !summary.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            summaries.Add(summary);
        }

        return summaries;
    }

    public Task<DocAppManifest?> GetManifestAsync(
        DocAccessScope scope,
        string appId,
        CancellationToken ct = default)
    {
        ValidateIdentifier(appId, nameof(appId), DocumentRules.MaxAppIdLength);

        if (_manifests.TryGetValue(new ManifestKey(string.Empty, appId), out var official))
        {
            return Task.FromResult<DocAppManifest?>(official);
        }

        if (scope.TenantId is not null &&
            _manifests.TryGetValue(new ManifestKey(scope.TenantId, appId), out var tenantManifest))
        {
            return Task.FromResult<DocAppManifest?>(tenantManifest);
        }

        return Task.FromResult<DocAppManifest?>(null);
    }

    public async Task<DepositDocResult> CreateAsync(
        DocWriter writer,
        DepositDocRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(request);

        ValidateRequestIdentifiers(request);

        if (!writer.IsOfficial && DocumentRules.IsReservedAppId(request.AppId))
        {
            return new DepositDocResult(
                Success: false,
                ChunkId: null,
                Message: "Forbidden: Depositing documentation for global application requires ClusterAdmin role.",
                ErrorCode: "FORBIDDEN");
        }

        if (!TryPrepareRecord(writer, request, expectedVersion: null, out var prepared, out var failure))
        {
            return failure!;
        }

        var outcome = await _repository.CreateAsync(prepared!.Record, Quota, ct).ConfigureAwait(false);
        if (!outcome.Succeeded || outcome.Record is null)
        {
            return MapWriteFailure(outcome.Status, prepared.WasSanitized);
        }

        var firstChunkId = await IndexSavedRecordAsync(outcome.Record, ct).ConfigureAwait(false);
        return new DepositDocResult(
            Success: true,
            ChunkId: firstChunkId,
            Message: "Document deposited and indexed successfully.",
            Version: outcome.Record.Version,
            Sanitized: prepared.WasSanitized);
    }

    public async Task<DepositDocResult> ReplaceAsync(
        DocWriter writer,
        DepositDocRequest request,
        long expectedVersion,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(request);

        ValidateRequestIdentifiers(request);

        if (!writer.IsOfficial && DocumentRules.IsReservedAppId(request.AppId))
        {
            return new DepositDocResult(
                Success: false,
                ChunkId: null,
                Message: "Forbidden: Depositing documentation for global application requires ClusterAdmin role.",
                ErrorCode: "FORBIDDEN");
        }

        if (!TryPrepareRecord(writer, request, expectedVersion, out var prepared, out var failure))
        {
            return failure!;
        }

        var outcome = await _repository.ReplaceAsync(prepared!.Record, expectedVersion, Quota, ct).ConfigureAwait(false);
        if (!outcome.Succeeded || outcome.Record is null)
        {
            return MapWriteFailure(outcome.Status, prepared.WasSanitized);
        }

        var saved = outcome.Record;
        await _searchService.RemoveDocumentAsync(new DocKey(saved.TenantId, saved.AppId, saved.Category, saved.DocId), ct).ConfigureAwait(false);
        var firstChunkId = await IndexSavedRecordAsync(saved, ct).ConfigureAwait(false);

        return new DepositDocResult(
            Success: true,
            ChunkId: firstChunkId,
            Message: "Document replaced and indexed successfully.",
            Version: saved.Version,
            Sanitized: prepared.WasSanitized);
    }

    public async Task<bool> DeleteAsync(
        DocWriter writer,
        DocKey key,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(key);

        ValidateIdentifier(key.AppId, nameof(key.AppId), DocumentRules.MaxAppIdLength);
        ValidateIdentifier(key.DocId, nameof(key.DocId), DocumentRules.MaxDocIdLength);

        if (!string.Equals(writer.TenantId, key.TenantId, StringComparison.Ordinal))
        {
            return false;
        }

        if (!writer.IsOfficial && DocumentRules.IsReservedAppId(key.AppId))
        {
            return false;
        }

        var outcome = await _repository.DeleteAsync(key, expectedVersion: null, _timeProvider.GetUtcNow(), ct).ConfigureAwait(false);
        if (!outcome.Succeeded)
        {
            return false;
        }

        await _searchService.RemoveDocumentAsync(key, ct).ConfigureAwait(false);
        return true;
    }

    public async Task<DepositDocResult> ImportBundleAsync(
        DocWriter writer,
        string appId,
        Stream zipStream,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(zipStream);
        ValidateIdentifier(appId, nameof(appId), DocumentRules.MaxAppIdLength);

        if (!writer.IsOfficial && DocumentRules.IsReservedAppId(appId))
        {
            return new DepositDocResult(
                Success: false,
                ChunkId: null,
                Message: "Forbidden: Depositing documentation for global application requires ClusterAdmin role.",
                ErrorCode: "FORBIDDEN");
        }

        var parsed = DocBundleReader.Read(writer, appId, zipStream, _options, _logger);
        if (parsed.IsFailure)
        {
            return parsed.Failure!;
        }

        var now = _timeProvider.GetUtcNow();
        var anySanitized = parsed.Entries.Any(e => e.WasSanitized);

        if (!writer.IsOfficial)
        {
            var records = new List<DocRecord>(parsed.Entries.Count);
            foreach (var entry in parsed.Entries)
            {
                records.Add(new DocRecord(
                    TenantId: writer.TenantId,
                    AppId: appId,
                    Category: entry.Category,
                    DocId: entry.DocId,
                    Title: entry.Title,
                    Content: entry.SanitizedContent,
                    ContentSha256: entry.ContentSha256,
                    ByteCount: entry.ByteCount,
                    Tags: Array.Empty<string>(),
                    ErrorCode: null,
                    AuthorSid: writer.AuthorSid,
                    Version: 1,
                    UpdatedAt: now));
            }

            DocRecord? lastSaved = null;
            if (records.Count > 0)
            {
                var outcome = await _repository.CreateManyAsync(records, Quota, ct).ConfigureAwait(false);
                if (!outcome.Succeeded)
                {
                    return MapWriteFailure(outcome.Status, anySanitized);
                }
                lastSaved = outcome.Record;
            }

            var allChunks = new List<DocChunk>();
            foreach (var record in records)
            {
                var chunks = await BuildChunksAsync(record, ct).ConfigureAwait(false);
                allChunks.AddRange(chunks);
            }

            if (allChunks.Count > 0)
            {
                await _searchService.IndexChunksAsync(allChunks, ct).ConfigureAwait(false);
            }

            return new DepositDocResult(
                Success: true,
                ChunkId: allChunks.Count > 0 ? allChunks[0].Id : null,
                Message: $"Bundle applied successfully: {records.Count} files unpacked, {allChunks.Count} sections indexed.",
                Version: lastSaved?.Version,
                Sanitized: anySanitized);
        }

        var officialRecords = new List<DocRecord>(parsed.Entries.Count);
        foreach (var entry in parsed.Entries)
        {
            officialRecords.Add(new DocRecord(
                TenantId: null,
                AppId: appId,
                Category: entry.Category,
                DocId: entry.DocId,
                Title: entry.Title,
                Content: entry.SanitizedContent,
                ContentSha256: entry.ContentSha256,
                ByteCount: entry.ByteCount,
                Tags: Array.Empty<string>(),
                ErrorCode: null,
                AuthorSid: writer.AuthorSid,
                Version: 1,
                UpdatedAt: now));
        }

        long? lastOfficialVersion = null;
        if (officialRecords.Count > 0)
        {
            var outcome = await _repository.UpsertOfficialManyAsync(officialRecords, ct).ConfigureAwait(false);
            if (!outcome.Succeeded)
            {
                return MapWriteFailure(outcome.Status, anySanitized);
            }

            lastOfficialVersion = outcome.Record?.Version;
        }

        if (parsed.Manifest is not null)
        {
            _manifests[new ManifestKey(string.Empty, appId)] = parsed.Manifest;
        }

        var officialChunks = new List<DocChunk>();
        foreach (var record in officialRecords)
        {
            await _searchService.RemoveDocumentAsync(new DocKey(null, appId, record.Category, record.DocId), ct).ConfigureAwait(false);
            officialChunks.AddRange(await BuildChunksAsync(record, ct).ConfigureAwait(false));
        }

        if (officialChunks.Count > 0)
        {
            await _searchService.IndexChunksAsync(officialChunks, ct).ConfigureAwait(false);
        }

        return new DepositDocResult(
            Success: true,
            ChunkId: officialChunks.Count > 0 ? officialChunks[0].Id : null,
            Message: $"Bundle applied successfully: {parsed.Entries.Count} files unpacked, {officialChunks.Count} sections indexed.",
            Version: lastOfficialVersion,
            Sanitized: anySanitized);
    }

    public async Task<int> PurgeTenantAsync(string tenantId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            throw new ArgumentException("Tenant identifier is required.", nameof(tenantId));
        }

        var normalized = tenantId.Trim();
        var purged = await _repository.PurgeTenantAsync(normalized, ct).ConfigureAwait(false);
        await _searchService.RemoveTenantAsync(normalized, ct).ConfigureAwait(false);

        foreach (var key in _manifests.Keys)
        {
            if (string.Equals(key.TenantKey, normalized, StringComparison.Ordinal))
            {
                _manifests.TryRemove(key, out _);
            }
        }

        return purged;
    }

    public async Task<int> RefreshIndexAsync(CancellationToken ct = default)
    {
        var activeRecords = await _repository.ReadChangedSinceAsync(since: null, ct).ConfigureAwait(false);
        var chunks = new List<DocChunk>();
        foreach (var record in activeRecords)
        {
            if (record.IsDeleted)
            {
                continue;
            }

            chunks.AddRange(await BuildChunksAsync(record, ct).ConfigureAwait(false));
        }

        await _searchService.ReplaceAllAsync(chunks, ct).ConfigureAwait(false);
        return chunks.Count;
    }

    private bool TryPrepareRecord(
        DocWriter writer,
        DepositDocRequest request,
        long? expectedVersion,
        out PreparedRecord? prepared,
        out DepositDocResult? failure)
    {
        SanitizedMarkdown sanitized;
        try
        {
            sanitized = UntrustedMarkdownSanitizer.Sanitize(request.Content, _options.MaxDocumentBytes);
        }
        catch (DocValidationException ex)
        {
            prepared = null;
            failure = new DepositDocResult(false, null, ex.Message, ex.Code);
            return false;
        }

        var title = UntrustedMarkdownSanitizer.SanitizeTitle(request.Title, _options.MaxTitleLength);
        var tags = UntrustedMarkdownSanitizer.SanitizeTags(request.Tags, _options.MaxTags, _options.MaxTagLength);
        var docId = string.IsNullOrWhiteSpace(request.DocId)
            ? DocumentRules.GenerateDocId(title, sanitized.Text)
            : request.DocId.Trim();
        ValidateIdentifier(docId, nameof(request.DocId), DocumentRules.MaxDocIdLength);

        var effectiveTitle = string.IsNullOrWhiteSpace(title) ? docId : title;
        var utf8Bytes = Encoding.UTF8.GetBytes(sanitized.Text);
        var sha256 = Convert.ToHexString(SHA256.HashData(utf8Bytes)).ToLowerInvariant();
        var record = new DocRecord(
            TenantId: writer.TenantId,
            AppId: request.AppId.Trim(),
            Category: request.Category,
            DocId: docId,
            Title: effectiveTitle,
            Content: sanitized.Text,
            ContentSha256: sha256,
            ByteCount: utf8Bytes.Length,
            Tags: tags,
            ErrorCode: request.ErrorCode?.Trim(),
            AuthorSid: writer.AuthorSid,
            Version: expectedVersion ?? 1,
            UpdatedAt: _timeProvider.GetUtcNow());

        prepared = new PreparedRecord(record, sanitized.Changed);
        failure = null;
        return true;
    }

    private async Task<DocChunkIdentifier?> IndexSavedRecordAsync(DocRecord record, CancellationToken ct)
    {
        var chunks = await BuildChunksAsync(record, ct).ConfigureAwait(false);
        if (chunks.Count > 0)
        {
            await _searchService.IndexChunksAsync(chunks, ct).ConfigureAwait(false);
            return chunks[0].Id;
        }
        return null;
    }

    private Task<IReadOnlyList<DocChunk>> BuildChunksAsync(DocRecord record, CancellationToken ct) =>
        BuildChunksAsync(_ingestor, record, ct);

    internal static async Task<IReadOnlyList<DocChunk>> BuildChunksAsync(
        IDocIngestor ingestor,
        DocRecord record,
        CancellationToken ct)
    {
        var sourcePath = $"{record.AppId}/{record.Category.ToString().ToLowerInvariant()}/{record.DocId}.md";
        var chunks = await ingestor.IngestMarkdownContentAsync(
            record.Content,
            record.DocId,
            record.AppId,
            record.Category,
            sourcePath,
            ct).ConfigureAwait(false);

        if (record.TenantId is null && record.AuthorSid is null && string.IsNullOrWhiteSpace(record.ErrorCode))
        {
            return chunks;
        }

        var enriched = new List<DocChunk>(chunks.Count);
        foreach (var chunk in chunks)
        {
            var errorCodes = !string.IsNullOrWhiteSpace(record.ErrorCode) &&
                             !chunk.ErrorCodes.Contains(record.ErrorCode, StringComparer.OrdinalIgnoreCase)
                ? chunk.ErrorCodes.Concat([record.ErrorCode]).ToArray()
                : chunk.ErrorCodes;

            enriched.Add(chunk with
            {
                TenantId = record.TenantId,
                AuthorSid = record.AuthorSid,
                ErrorCodes = errorCodes
            });
        }

        return enriched;
    }

    private static DepositDocResult MapWriteFailure(DocWriteStatus status, bool wasSanitized) => status switch
    {
        DocWriteStatus.ReservedKey => new DepositDocResult(
            false, null, "The document identifier conflicts with official documentation.", "DOC_ID_RESERVED", Sanitized: wasSanitized),
        DocWriteStatus.AlreadyExists => new DepositDocResult(
            false, null, "A document with this key already exists.", "DOC_EXISTS", Sanitized: wasSanitized),
        DocWriteStatus.QuotaExceeded => new DepositDocResult(
            false, null, "Tenant documentation quota has been exceeded.", "DOC_QUOTA_EXCEEDED", Sanitized: wasSanitized),
        DocWriteStatus.VersionMismatch => new DepositDocResult(
            false, null, "The document version does not match the expected If-Match version.", "VERSION_MISMATCH", Sanitized: wasSanitized),
        DocWriteStatus.NotFound => new DepositDocResult(
            false, null, "The requested document was not found.", "DOC_NOT_FOUND", Sanitized: wasSanitized),
        _ => new DepositDocResult(
            false, null, "Documentation write failed.", "BAD_REQUEST", Sanitized: wasSanitized)
    };

    private static void ValidateRequestIdentifiers(DepositDocRequest request)
    {
        ValidateIdentifier(request.AppId, nameof(request.AppId), DocumentRules.MaxAppIdLength);
        if (!string.IsNullOrWhiteSpace(request.DocId))
        {
            ValidateIdentifier(request.DocId.Trim(), nameof(request.DocId), DocumentRules.MaxDocIdLength);
        }
        if (!string.IsNullOrWhiteSpace(request.ErrorCode) && !DocumentRules.IsValidErrorCode(request.ErrorCode))
        {
            throw new ArgumentException($"Invalid error code '{request.ErrorCode}'.", nameof(request));
        }
    }

    private static void ValidateIdentifier(string identifier, string paramName, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(identifier) || !DocumentRules.IsValidIdentifier(identifier, maxLength))
        {
            throw new ArgumentException(
                $"Invalid identifier '{identifier}'. Only alphanumeric characters, dashes, underscores, and dots are permitted, and path traversal ('..') is prohibited.",
                paramName);
        }
    }

    private static bool IsManifestVisible(string tenantKey, DocAccessScope scope) =>
        string.IsNullOrEmpty(tenantKey) ||
        (scope.TenantId is not null && string.Equals(tenantKey, scope.TenantId, StringComparison.Ordinal));

    private sealed record PreparedRecord(DocRecord Record, bool WasSanitized);

    private readonly record struct ManifestKey(string TenantKey, string AppId);

    private sealed class ManifestKeyComparer : IEqualityComparer<ManifestKey>
    {
        public static readonly ManifestKeyComparer Instance = new();

        public bool Equals(ManifestKey x, ManifestKey y) =>
            string.Equals(x.TenantKey, y.TenantKey, StringComparison.Ordinal) &&
            string.Equals(x.AppId, y.AppId, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(ManifestKey obj) =>
            HashCode.Combine(
                StringComparer.Ordinal.GetHashCode(obj.TenantKey),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.AppId));
    }
}
