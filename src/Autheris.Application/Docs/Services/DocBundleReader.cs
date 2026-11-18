namespace Autheris.Application.Docs.Services;

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;

internal sealed record DocBundleEntry(
    DocCategory Category,
    string DocId,
    string Title,
    string SanitizedContent,
    string ContentSha256,
    int ByteCount,
    bool WasSanitized);

internal sealed record ParsedDocBundle(
    DepositDocResult? Failure,
    DocAppManifest? Manifest,
    IReadOnlyList<DocBundleEntry> Entries)
{
    public bool IsFailure => Failure is not null;
}

/// <summary>
/// Validates and reads documentation ZIP archives completely in memory with Zip-Bomb, Zip-Slip,
/// path-shape, and untrusted-markdown sanitization guards.
/// </summary>
internal static class DocBundleReader
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static ParsedDocBundle Read(
        DocWriter writer,
        string appId,
        Stream zipStream,
        DocsOptions options,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(zipStream);
        ArgumentNullException.ThrowIfNull(options);

        try
        {
            return ReadCore(writer, appId, zipStream, options, logger);
        }
        catch (InvalidDataException ex)
        {
            // Corrupt or tampered archive (e.g. entry CRC32 mismatch): reject the whole bundle, never partially index it.
            logger.LogWarning(ex, "Rejected corrupt documentation bundle '{AppId}'", SanitizeForLog(appId));
            return Fail(new DepositDocResult(
                Success: false,
                ChunkId: null,
                Message: "Archive is corrupt or failed integrity validation.",
                ErrorCode: "INVALID_PARAMS"));
        }
    }

    private static ParsedDocBundle ReadCore(
        DocWriter writer,
        string appId,
        Stream zipStream,
        DocsOptions options,
        ILogger logger)
    {

        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);

        if (archive.Entries.Count > options.MaxBundleEntries)
        {
            logger.LogError(
                "Zip Bomb detected: entry count {Count} exceeds limit {MaxEntries} for bundle '{AppId}'",
                archive.Entries.Count, options.MaxBundleEntries, SanitizeForLog(appId));
            return Fail(new DepositDocResult(
                Success: false,
                ChunkId: null,
                Message: $"Security violation: Archive entry count ({archive.Entries.Count}) exceeds maximum allowed threshold of {options.MaxBundleEntries} (Zip bomb protection).",
                ErrorCode: "ZIP_BOMB_DETECTED"));
        }

        var entries = new List<DocBundleEntry>();
        var seenKeys = new HashSet<(DocCategory Category, string DocId)>();
        DocAppManifest? manifest = null;
        long cumulativeHeaderBytes = 0;
        long cumulativeActualBytes = 0;

        foreach (var entry in archive.Entries)
        {
            if (entry.Length < 0 || cumulativeHeaderBytes + entry.Length > options.MaxBundleUncompressedBytes)
            {
                return ZipBombFailure(logger, appId, cumulativeHeaderBytes + Math.Max(0, entry.Length), options.MaxBundleUncompressedBytes);
            }
            cumulativeHeaderBytes += entry.Length;

            if (IsTraversalEntry(entry.FullName))
            {
                logger.LogError(
                    "Zip Slip traversal attempt detected for entry '{Entry}' in bundle '{AppId}'",
                    SanitizeForLog(entry.FullName), SanitizeForLog(appId));
                return Fail(new DepositDocResult(
                    Success: false,
                    ChunkId: null,
                    Message: $"Security violation: Archive entry '{entry.FullName}' attempts directory traversal outside destination directory.",
                    ErrorCode: "ZIP_SLIP_ATTEMPT"));
            }

            // Directory entry
            if (string.IsNullOrEmpty(entry.Name) || entry.FullName.EndsWith('/', StringComparison.Ordinal))
            {
                continue;
            }

            var normalized = entry.FullName.TrimStart('/');
            var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);

            if (segments.Length == 1 && segments[0].Equals("manifest.json", StringComparison.OrdinalIgnoreCase))
            {
                if (!writer.IsOfficial)
                {
                    // Tenant bundles carry documentation only: manifests are ignored.
                    continue;
                }

                var manifestBytes = ReadBoundedBytes(entry, options.MaxBundleUncompressedBytes - cumulativeActualBytes, options.MaxDocumentBytes, out var tooBigForArchive, out _);
                if (tooBigForArchive)
                {
                    return ZipBombFailure(logger, appId, options.MaxBundleUncompressedBytes + 1, options.MaxBundleUncompressedBytes);
                }

                if (manifestBytes is not null)
                {
                    cumulativeActualBytes += manifestBytes.Length;
                    manifest = TryParseManifest(manifestBytes, appId, logger);
                }
                continue;
            }

            if (!entry.Name.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (segments.Length != 2)
            {
                return Fail(new DepositDocResult(
                    Success: false,
                    ChunkId: null,
                    Message: $"Archive entry '{entry.FullName}' must use the '{{category}}/{{docId}}.md' layout.",
                    ErrorCode: "INVALID_PARAMS"));
            }

            var docId = Path.GetFileNameWithoutExtension(segments[1]);
            if (!DocumentRules.IsValidDocId(docId))
            {
                return Fail(new DepositDocResult(
                    Success: false,
                    ChunkId: null,
                    Message: $"Archive entry '{entry.FullName}' has an invalid document identifier.",
                    ErrorCode: "INVALID_PARAMS"));
            }

            // Allow raw markdown slightly larger than MaxDocumentBytes before HTML/frontmatter stripping if within archive cap,
            // then let UntrustedMarkdownSanitizer enforce MaxDocumentBytes on sanitized UTF-8 output.
            var remainingArchive = options.MaxBundleUncompressedBytes - cumulativeActualBytes;
            var singleEntryLimit = Math.Max((long)options.MaxDocumentBytes * 6L, options.MaxDocumentBytes);
            var rawBytes = ReadBoundedBytes(entry, remainingArchive, singleEntryLimit, out var archiveOverflow, out var docOverflow);
            if (archiveOverflow)
            {
                return ZipBombFailure(logger, appId, options.MaxBundleUncompressedBytes + 1, options.MaxBundleUncompressedBytes);
            }

            if (docOverflow || rawBytes is null)
            {
                return Fail(new DepositDocResult(
                    Success: false,
                    ChunkId: null,
                    Message: $"Archive entry '{entry.FullName}' exceeds the maximum document size of {options.MaxDocumentBytes} bytes.",
                    ErrorCode: "DOC_TOO_LARGE"));
            }

            cumulativeActualBytes += rawBytes.Length;

            var rawMarkdown = Encoding.UTF8.GetString(rawBytes);
            SanitizedMarkdown sanitized;
            try
            {
                sanitized = UntrustedMarkdownSanitizer.Sanitize(rawMarkdown, options.MaxDocumentBytes);
            }
            catch (DocValidationException ex)
            {
                return Fail(new DepositDocResult(
                    Success: false,
                    ChunkId: null,
                    Message: ex.Message,
                    ErrorCode: ex.Code));
            }

            var category = ResolveCategoryFromSegment(segments[0]);
            if (!seenKeys.Add((category, docId)))
            {
                return Fail(new DepositDocResult(
                    Success: false,
                    ChunkId: null,
                    Message: $"Archive entry '{entry.FullName}' resolves to a document that is already part of the bundle.",
                    ErrorCode: "INVALID_PARAMS"));
            }

            var title = UntrustedMarkdownSanitizer.SanitizeTitle(DocumentRules.ExtractTitle(sanitized.Text, docId), options.MaxTitleLength);
            if (string.IsNullOrWhiteSpace(title))
            {
                title = docId;
            }

            var utf8Bytes = Encoding.UTF8.GetBytes(sanitized.Text);
            var sha256 = Convert.ToHexString(SHA256.HashData(utf8Bytes)).ToLowerInvariant();
            entries.Add(new DocBundleEntry(category, docId, title, sanitized.Text, sha256, utf8Bytes.Length, sanitized.Changed));
        }

        return new ParsedDocBundle(null, manifest, entries);
    }

    internal static DocCategory ResolveCategoryFromSegment(string segment)
    {
        if (Enum.TryParse<DocCategory>(segment, true, out var exact))
        {
            return exact;
        }

        var lower = segment.ToLowerInvariant();
        if (lower.Contains("runbook", StringComparison.Ordinal)) return DocCategory.Runbook;
        if (lower.Contains("architecture", StringComparison.Ordinal) || lower.Contains("arc42", StringComparison.Ordinal)) return DocCategory.Architecture;
        if (lower.Contains("adr", StringComparison.Ordinal)) return DocCategory.Adr;
        if (lower.Contains("error", StringComparison.Ordinal)) return DocCategory.ErrorCode;
        if (lower.Contains("api", StringComparison.Ordinal) || lower.Contains("openapi", StringComparison.Ordinal)) return DocCategory.ApiEndpoint;
        if (lower.Contains("config", StringComparison.Ordinal)) return DocCategory.Configuration;
        return DocCategory.Guide;
    }

    private static bool IsTraversalEntry(string? fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName) ||
            fullName.Contains('\0', StringComparison.Ordinal) ||
            fullName.Contains("..", StringComparison.Ordinal) ||
            fullName.Contains('\\', StringComparison.Ordinal) ||
            Path.IsPathRooted(fullName) ||
            fullName.StartsWith('/', StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    private static byte[]? ReadBoundedBytes(
        ZipArchiveEntry entry,
        long remainingArchiveBytes,
        long maxEntryBytes,
        out bool archiveLimitExceeded,
        out bool docLimitExceeded)
    {
        archiveLimitExceeded = false;
        docLimitExceeded = false;

        using var entryStream = entry.Open();
        using var ms = new MemoryStream();
        var buffer = new byte[16384];
        long total = 0;

        int read;
        while ((read = entryStream.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > remainingArchiveBytes)
            {
                archiveLimitExceeded = true;
                return null;
            }

            if (total > maxEntryBytes)
            {
                docLimitExceeded = true;
                return null;
            }

            ms.Write(buffer, 0, read);
        }

        return ms.ToArray();
    }

    private static DocAppManifest? TryParseManifest(byte[] bytes, string appId, ILogger logger)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<DocAppManifest>(bytes, JsonOptions);
            return manifest is not null ? manifest with { AppId = appId } : null;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse manifest.json for app {AppId}", SanitizeForLog(appId));
            return null;
        }
    }

    private static ParsedDocBundle Fail(DepositDocResult failure) =>
        new(failure, null, Array.Empty<DocBundleEntry>());

    private static ParsedDocBundle ZipBombFailure(ILogger logger, string appId, long size, long maxBytes)
    {
        logger.LogError(
            "Zip Bomb detected: cumulative size {Bytes} exceeds limit {MaxBytes} for bundle '{AppId}'",
            size, maxBytes, SanitizeForLog(appId));
        return Fail(new DepositDocResult(
            Success: false,
            ChunkId: null,
            Message: $"Security violation: Archive uncompressed size exceeds maximum allowed threshold of {maxBytes / (1024 * 1024)}MB (Zip bomb protection).",
            ErrorCode: "ZIP_BOMB_DETECTED"));
    }

    private static string SanitizeForLog(string? input) =>
        string.IsNullOrEmpty(input)
            ? string.Empty
            : input.Replace("\r", "_", StringComparison.Ordinal).Replace("\n", "_", StringComparison.Ordinal);
}
