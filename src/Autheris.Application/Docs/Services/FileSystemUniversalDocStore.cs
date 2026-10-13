namespace Autheris.Application.Docs.Services;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Docs.Interfaces;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;

/// <summary>
/// Filesystem-backed, air-gapped implementation of <see cref="IUniversalDocStore"/>.
/// Enforces strict Zip-Slip protection, path sanitization, and seamless in-memory indexing.
/// </summary>
public sealed class FileSystemUniversalDocStore : IUniversalDocStore
{
    private static readonly Regex SafeIdentifierRegex = new(@"^[a-zA-Z0-9_\-\.]+$", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly string _rootPath;
    private readonly IDocSearchService _searchService;
    private readonly IDocIngestor _ingestor;
    private readonly ILogger<FileSystemUniversalDocStore> _logger;
    private readonly ConcurrentDictionary<string, DocAppManifest> _manifestCache = new(StringComparer.OrdinalIgnoreCase);

    public FileSystemUniversalDocStore(
        string? rootPath,
        IDocSearchService searchService,
        IDocIngestor ingestor,
        ILogger<FileSystemUniversalDocStore> logger)
    {
        _rootPath = string.IsNullOrWhiteSpace(rootPath)
            ? Path.Combine(AppContext.BaseDirectory, "shared-docs")
            : Path.GetFullPath(rootPath);

        _searchService = searchService ?? throw new ArgumentNullException(nameof(searchService));
        _ingestor = ingestor ?? throw new ArgumentNullException(nameof(ingestor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (!Directory.Exists(_rootPath))
        {
            Directory.CreateDirectory(_rootPath);
        }
    }

    public async Task<IReadOnlyList<DocAppSummary>> ListApplicationsAsync(string? domain = null, string? tag = null, CancellationToken ct = default)
    {
        var summaries = new List<DocAppSummary>();
        if (!Directory.Exists(_rootPath))
        {
            return summaries;
        }

        var appDirs = Directory.GetDirectories(_rootPath);
        foreach (var dir in appDirs)
        {
            var appId = Path.GetFileName(dir);
            if (!SafeIdentifierRegex.IsMatch(appId))
            {
                continue;
            }

            var manifest = await GetManifestAsync(appId, ct).ConfigureAwait(false);
            var mdFiles = Directory.GetFiles(dir, "*.md", SearchOption.AllDirectories);

            var summary = new DocAppSummary(
                AppId: appId,
                Name: manifest?.Name ?? appId,
                Version: manifest?.Version ?? "1.0.0",
                Domain: manifest?.Domain ?? "default",
                Owner: manifest?.Owner ?? "unassigned",
                GovernanceTier: manifest?.GovernanceTier ?? "Tier-2",
                DocumentCount: mdFiles.Length,
                Tags: manifest?.Tags ?? Array.Empty<string>()
            );

            if (!string.IsNullOrWhiteSpace(domain) && !string.Equals(summary.Domain, domain, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(tag) && !summary.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            summaries.Add(summary);
        }

        return summaries;
    }

    public Task<DocAppManifest?> GetManifestAsync(string appId, CancellationToken ct = default)
    {
        ValidateSafeIdentifier(appId, nameof(appId));

        if (_manifestCache.TryGetValue(appId, out var cached))
        {
            return Task.FromResult<DocAppManifest?>(cached);
        }

        var safeAppId = Path.GetFileName(appId);
        var rootWithSep = _rootPath.EndsWith(Path.DirectorySeparatorChar)
            ? _rootPath
            : _rootPath + Path.DirectorySeparatorChar;

        var appDir = Path.GetFullPath(Path.Combine(_rootPath, safeAppId));
        if (!appDir.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(appDir))
        {
            return Task.FromResult<DocAppManifest?>(null);
        }

        var manifestJsonPath = Path.GetFullPath(Path.Combine(appDir, "manifest.json"));
        if (!manifestJsonPath.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult<DocAppManifest?>(null);
        }

        if (File.Exists(manifestJsonPath))
        {
            try
            {
                var json = File.ReadAllText(manifestJsonPath);
                var manifest = JsonSerializer.Deserialize<DocAppManifest>(json, JsonOptions);
                if (manifest != null)
                {
                    _manifestCache[appId] = manifest;
                    return Task.FromResult<DocAppManifest?>(manifest);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse manifest.json for app {AppId}", SanitizeForLog(appId));
            }
        }

        return Task.FromResult<DocAppManifest?>(null);
    }

    public async Task<DepositDocResult> DepositDocumentAsync(DepositDocRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateSafeIdentifier(request.AppId, nameof(request.AppId));

        var docId = !string.IsNullOrWhiteSpace(request.DocId)
            ? request.DocId.Trim()
            : Slugify(request.Title);

        ValidateSafeIdentifier(docId, nameof(docId));

        var safeAppId = Path.GetFileName(request.AppId);
        var safeDocId = Path.GetFileName(docId);
        var categoryName = Path.GetFileName(request.Category.ToString().ToLowerInvariant());

        var rootWithSep = _rootPath.EndsWith(Path.DirectorySeparatorChar)
            ? _rootPath
            : _rootPath + Path.DirectorySeparatorChar;

        var targetCategoryDir = Path.GetFullPath(Path.Combine(_rootPath, safeAppId, categoryName));
        if (!targetCategoryDir.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Invalid category directory path.", nameof(request));
        }

        Directory.CreateDirectory(targetCategoryDir);

        var filePath = Path.GetFullPath(Path.Combine(targetCategoryDir, $"{safeDocId}.md"));
        if (!filePath.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Invalid document file path.", nameof(request));
        }

        await File.WriteAllTextAsync(filePath, request.Content, ct).ConfigureAwait(false);

        // Index in-memory for instant discovery (< 100ms)
        var chunks = await _ingestor.IngestMarkdownContentAsync(
            request.Content,
            safeDocId,
            safeAppId,
            request.Category,
            filePath,
            ct).ConfigureAwait(false);

        await _searchService.IndexChunksAsync(chunks, ct).ConfigureAwait(false);

        var firstChunkId = chunks.Count > 0 ? chunks[0].Id : new DocChunkIdentifier(safeAppId, categoryName, safeDocId, "root");

        return new DepositDocResult(
            Success: true,
            ChunkId: firstChunkId,
            FilePath: filePath,
            Message: $"Successfully deposited and indexed document '{safeDocId}' for application '{safeAppId}'.");
    }

    public async Task<DepositDocResult> ExtractBundleAsync(string appId, Stream zipStream, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(zipStream);
        ValidateSafeIdentifier(appId, nameof(appId));

        var safeAppId = Path.GetFileName(appId);
        var rootWithSep = _rootPath.EndsWith(Path.DirectorySeparatorChar)
            ? _rootPath
            : _rootPath + Path.DirectorySeparatorChar;

        var targetDir = Path.GetFullPath(Path.Combine(_rootPath, safeAppId));
        if (!targetDir.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase) || targetDir == _rootPath)
        {
            throw new ArgumentException("Invalid target application directory.", nameof(appId));
        }

        var targetDirWithSep = targetDir.EndsWith(Path.DirectorySeparatorChar)
            ? targetDir
            : targetDir + Path.DirectorySeparatorChar;

        Directory.CreateDirectory(targetDir);

        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);
        var extractedFiles = new List<string>();

        foreach (var entry in archive.Entries)
        {
            // OWASP AppSec: Zip Slip Defense - reject traversal indicators immediately
            if (string.IsNullOrWhiteSpace(entry.FullName) ||
                entry.FullName.Contains("..", StringComparison.Ordinal) ||
                Path.IsPathRooted(entry.FullName))
            {
                _logger.LogError("Zip Slip traversal attempt detected for entry '{Entry}' in bundle '{AppId}'",
                    SanitizeForLog(entry.FullName), SanitizeForLog(appId));
                return new DepositDocResult(
                    Success: false,
                    ChunkId: null,
                    FilePath: null,
                    Message: $"Security violation: Archive entry '{entry.FullName}' attempts directory traversal outside destination directory.",
                    ErrorCode: "ZIP_SLIP_ATTEMPT");
            }

            var destinationPath = Path.GetFullPath(Path.Combine(targetDir, entry.FullName));

            // Verify canonical destination path resides strictly within target directory
            if (!destinationPath.StartsWith(targetDirWithSep, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogError("Zip Slip traversal attempt detected for entry '{Entry}' in bundle '{AppId}'",
                    SanitizeForLog(entry.FullName), SanitizeForLog(appId));
                return new DepositDocResult(
                    Success: false,
                    ChunkId: null,
                    FilePath: null,
                    Message: $"Security violation: Archive entry '{entry.FullName}' attempts directory traversal outside destination directory.",
                    ErrorCode: "ZIP_SLIP_ATTEMPT");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                // Directory entry
                Directory.CreateDirectory(destinationPath);
                continue;
            }

            var parentDir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(parentDir) &&
                parentDir.StartsWith(targetDirWithSep, StringComparison.OrdinalIgnoreCase) &&
                !Directory.Exists(parentDir))
            {
                Directory.CreateDirectory(parentDir);
            }

            entry.ExtractToFile(destinationPath, overwrite: true);
            extractedFiles.Add(destinationPath);
        }

        // Index extracted markdown files
        var allChunks = new List<DocChunk>();
        foreach (var file in extractedFiles)
        {
            if (file.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                var canonicalFile = Path.GetFullPath(file);
                if (!canonicalFile.StartsWith(targetDirWithSep, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var relativePath = Path.GetRelativePath(targetDir, canonicalFile);
                var category = ResolveCategoryFromPath(relativePath);
                try
                {
                    var chunks = await _ingestor.IngestMarkdownFileAsync(canonicalFile, safeAppId, category, ct).ConfigureAwait(false);
                    allChunks.AddRange(chunks);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to ingest markdown file {File}", SanitizeForLog(canonicalFile));
                }
            }
        }

        if (allChunks.Count > 0)
        {
            await _searchService.IndexChunksAsync(allChunks, ct).ConfigureAwait(false);
        }

        _manifestCache.TryRemove(appId, out _);

        return new DepositDocResult(
            Success: true,
            ChunkId: allChunks.Count > 0 ? allChunks[0].Id : null,
            FilePath: targetDir,
            Message: $"Bundle applied successfully: {extractedFiles.Count} files unpacked, {allChunks.Count} sections indexed.");
    }

    public async Task<int> SyncExistingDocumentsAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(_rootPath))
        {
            return 0;
        }

        int totalIndexed = 0;
        var appDirs = Directory.GetDirectories(_rootPath);

        foreach (var dir in appDirs)
        {
            ct.ThrowIfCancellationRequested();
            var appId = Path.GetFileName(dir);
            if (!SafeIdentifierRegex.IsMatch(appId))
            {
                continue;
            }

            var mdFiles = Directory.GetFiles(dir, "*.md", SearchOption.AllDirectories);
            var allChunks = new List<DocChunk>();

            foreach (var file in mdFiles)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var content = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
                    var docId = Path.GetFileNameWithoutExtension(file);
                    var relativePath = Path.GetRelativePath(dir, file);
                    var category = ResolveCategoryFromPath(relativePath);

                    var chunks = await _ingestor.IngestMarkdownContentAsync(
                        content,
                        docId,
                        appId,
                        category,
                        file,
                        ct).ConfigureAwait(false);

                    allChunks.AddRange(chunks);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to index existing document '{FilePath}' for app '{AppId}'", file, appId);
                }
            }

            if (allChunks.Count > 0)
            {
                await _searchService.IndexChunksAsync(allChunks, ct).ConfigureAwait(false);
                totalIndexed += allChunks.Count;
            }
        }

        _logger.LogInformation("Completed document store sync: {Count} total sections indexed across {AppCount} applications.",
            totalIndexed, appDirs.Length);

        return totalIndexed;
    }

    private static DocCategory ResolveCategoryFromPath(string relativePath)
    {
        var lower = relativePath.ToLowerInvariant();
        if (lower.Contains("runbook")) return DocCategory.Runbook;
        if (lower.Contains("architecture") || lower.Contains("arc42")) return DocCategory.Architecture;
        if (lower.Contains("adr")) return DocCategory.Adr;
        if (lower.Contains("error")) return DocCategory.ErrorCode;
        if (lower.Contains("api") || lower.Contains("openapi")) return DocCategory.ApiEndpoint;
        if (lower.Contains("config")) return DocCategory.Configuration;
        return DocCategory.Guide;
    }

    private static void ValidateSafeIdentifier(string identifier, string paramName)
    {
        if (string.IsNullOrWhiteSpace(identifier) ||
            !SafeIdentifierRegex.IsMatch(identifier) ||
            identifier.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Invalid identifier '{identifier}'. Only alphanumeric characters, dashes, underscores, and dots are permitted, and path traversal ('..') is prohibited.", paramName);
        }
    }

    private static string Slugify(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "untitled";
        var slug = Regex.Replace(text.ToLowerInvariant(), @"[^a-z0-9\-]+", "-").Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? "untitled" : slug;
    }

    private static string SanitizeForLog(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        return input.Replace("\r", "_").Replace("\n", "_");
    }
}
