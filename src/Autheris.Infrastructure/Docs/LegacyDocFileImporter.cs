namespace Autheris.Infrastructure.Docs;

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Docs.Interfaces;
using Autheris.Application.Docs.Services;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>Counts of one legacy import run.</summary>
public sealed record LegacyDocImportResult(int Imported, int Skipped, int Rejected, int AlreadyPresent);

/// <summary>
/// One-shot import of the former file layout <c>{source}/{appId}/{category}/{docId}.md</c> into the document store.
/// Runs in the background after startup, only when <c>Docs:LegacyImport:SourcePath</c> is set, and never writes to the source.
/// Every file passes through <see cref="IUniversalDocStore.CreateAsync"/> and so through the same rules, sanitiser and quota
/// as a normal write. Documents are never overwritten; only counts and relative paths are logged or audited.
/// </summary>
public sealed partial class LegacyDocFileImporter : BackgroundService
{
    private const string TenantKey = "autheris-tenant";
    private const string AuthorKey = "autheris-author";
    private const string UnknownTenant = "__unknown__";
    private const string Fence = "---";
    private const int ProvenanceAllowanceBytes = 1024;

    private readonly IUniversalDocStore _store;
    private readonly IDocumentRepository _repository;
    private readonly IAuditWriter _audit;
    private readonly DocsOptions _options;
    private readonly ILogger<LegacyDocFileImporter> _logger;

    public LegacyDocFileImporter(
        IUniversalDocStore store,
        IDocumentRepository repository,
        IAuditWriter audit,
        IOptions<DocsOptions> options,
        ILogger<LegacyDocFileImporter> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Yield();
            await RunAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            LogImportFailed(_logger, ex.GetType().Name);
        }
    }

    /// <summary>Runs the import once; returns <c>null</c> when no source path is configured.</summary>
    public async Task<LegacyDocImportResult?> RunAsync(CancellationToken ct)
    {
        var source = _options.LegacyImport.SourcePath;
        if (string.IsNullOrWhiteSpace(source))
        {
            return null;
        }

        var root = Path.GetFullPath(source);
        int imported = 0, skipped = 0, rejected = 0, alreadyPresent = 0;

        foreach (var file in EnumerateCandidateFiles(root, () => skipped++))
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(root, file.Path).Replace('\\', '/');
            switch (await ImportFileAsync(file, ct).ConfigureAwait(false))
            {
                case Outcome.Imported: imported++; break;
                case Outcome.AlreadyPresent: alreadyPresent++; break;
                case Outcome.Skipped: skipped++; LogFile(_logger, "skipped", relative); break;
                default: rejected++; LogFile(_logger, "rejected", relative); break;
            }
        }

        var result = new LegacyDocImportResult(imported, skipped, rejected, alreadyPresent);
        await _audit.RecordAsync(new AuditLogEntry
        {
            TenantId = TenantId.Platform,
            EventType = AuditEventTypes.DocLegacyImportCompleted,
            ActorSid = new Sid("S-1-0-0"),
            Decision = "ALLOW",
            TargetTable = "doc_documents",
            TraceId = Guid.NewGuid().ToString("N"),
            DetailsJson = JsonSerializer.Serialize(new { imported, skipped, rejected, alreadyPresent })
        }, ct).ConfigureAwait(false);
        LogCompleted(_logger, imported, skipped, rejected, alreadyPresent);
        return result;
    }

    private async Task<Outcome> ImportFileAsync(Candidate file, CancellationToken ct)
    {
        if (!Enum.TryParse<DocCategory>(file.Category, ignoreCase: true, out var category) ||
            !Enum.IsDefined(category) || char.IsDigit(file.Category[0]))
        {
            return Outcome.Skipped;
        }

        string raw;
        try
        {
            if (new FileInfo(file.Path).Length > _options.MaxDocumentBytes + ProvenanceAllowanceBytes)
            {
                return Outcome.Rejected;
            }

            raw = await File.ReadAllTextAsync(file.Path, Encoding.UTF8, ct).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return Outcome.Rejected;
        }
        catch (UnauthorizedAccessException)
        {
            return Outcome.Rejected;
        }

        var (body, tenant, author) = ParseProvenance(raw);
        if (tenant is not null && (tenant == UnknownTenant || !DocumentRules.IsValidTenantId(tenant)))
        {
            return Outcome.Skipped;
        }

        try
        {
            var request = new DepositDocRequest(file.AppId, category, string.Empty, body, file.DocId, AuthorSid: author);
            var result = await _store.CreateAsync(new DocWriter(tenant, author), request, ct).ConfigureAwait(false);
            if (result.Success)
            {
                return Outcome.Imported;
            }

            if (result.ErrorCode != "DOC_EXISTS")
            {
                return Outcome.Rejected;
            }

            var existing = await _repository.GetAsync(new DocKey(tenant, file.AppId, category, file.DocId), ct).ConfigureAwait(false);
            var sanitized = UntrustedMarkdownSanitizer.Sanitize(body, _options.MaxDocumentBytes).Text;
            var sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sanitized))).ToLowerInvariant();
            return existing is { IsDeleted: false } && string.Equals(existing.ContentSha256, sha, StringComparison.Ordinal)
                ? Outcome.AlreadyPresent
                : Outcome.Rejected;
        }
        catch (Exception ex) when (ex is ArgumentException or DocValidationException)
        {
            return Outcome.Rejected;
        }
    }

    /// <summary>
    /// Yields regular <c>.md</c> files exactly two directory levels below <paramref name="root"/>. Symlinks and reparse points
    /// (directories and files) are never followed and count as skipped; other file types are ignored.
    /// </summary>
    private static IEnumerable<Candidate> EnumerateCandidateFiles(string root, Action onSkipped)
    {
        if (!Directory.Exists(root))
        {
            yield break;
        }

        var rootPrefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        foreach (var appDir in Directory.EnumerateDirectories(root))
        {
            if (IsLink(appDir)) { onSkipped(); continue; }
            foreach (var categoryDir in Directory.EnumerateDirectories(appDir))
            {
                if (IsLink(categoryDir)) { onSkipped(); continue; }
                foreach (var path in Directory.EnumerateFiles(categoryDir, "*.md"))
                {
                    var full = Path.GetFullPath(path);
                    if (!full.StartsWith(rootPrefix, StringComparison.Ordinal) || IsLink(full))
                    {
                        onSkipped();
                        continue;
                    }

                    yield return new Candidate(
                        full,
                        Path.GetFileName(appDir),
                        Path.GetFileName(categoryDir),
                        Path.GetFileNameWithoutExtension(full));
                }
            }
        }
    }

    private static bool IsLink(string path)
    {
        var info = new FileInfo(path);
        return info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint);
    }

    /// <summary>
    /// Splits file content into body and provenance. Only a leading front-matter block naming the tenant key is a provenance
    /// block; an unusable or unterminated block yields the unknown tenant so the document is skipped, never made official.
    /// </summary>
    private static (string Content, string? TenantId, string? AuthorSid) ParseProvenance(string fileContent)
    {
        if (string.IsNullOrEmpty(fileContent))
        {
            return (string.Empty, null, null);
        }

        var text = fileContent[0] == '﻿' ? fileContent[1..] : fileContent;
        var firstLineEnd = text.IndexOf('\n', StringComparison.Ordinal);
        if (firstLineEnd < 0 || !string.Equals(text[..firstLineEnd].Trim(), Fence, StringComparison.Ordinal))
        {
            return (fileContent, null, null);
        }

        string? tenant = null;
        string? author = null;
        var hasTenantKey = false;
        var pos = firstLineEnd + 1;
        while (pos < text.Length)
        {
            var end = text.IndexOf('\n', pos, StringComparison.Ordinal);
            var line = (end < 0 ? text[pos..] : text[pos..end]).TrimEnd('\r');
            var next = end < 0 ? text.Length : end + 1;

            if (string.Equals(line.Trim(), Fence, StringComparison.Ordinal))
            {
                return hasTenantKey
                    ? (text[next..], string.IsNullOrWhiteSpace(tenant) ? UnknownTenant : tenant, string.IsNullOrWhiteSpace(author) ? null : author)
                    : (fileContent, null, null);
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0)
            {
                var key = line[..colon].Trim();
                var value = line[(colon + 1)..].Trim();
                if (string.Equals(key, TenantKey, StringComparison.OrdinalIgnoreCase))
                {
                    hasTenantKey = true;
                    tenant ??= value;
                }
                else if (string.Equals(key, AuthorKey, StringComparison.OrdinalIgnoreCase))
                {
                    author ??= value;
                }
            }

            pos = next;
        }

        return (fileContent, hasTenantKey ? UnknownTenant : null, null);
    }

    private enum Outcome { Imported, AlreadyPresent, Skipped, Rejected }

    private readonly record struct Candidate(string Path, string AppId, string Category, string DocId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Legacy doc import completed: imported={Imported}, skipped={Skipped}, rejected={Rejected}, alreadyPresent={AlreadyPresent}.")]
    private static partial void LogCompleted(ILogger logger, int imported, int skipped, int rejected, int alreadyPresent);

    [LoggerMessage(Level = LogLevel.Information, Message = "Legacy doc import {Disposition} file {RelativePath}.")]
    private static partial void LogFile(ILogger logger, string disposition, string relativePath);

    [LoggerMessage(Level = LogLevel.Error, Message = "Legacy doc import failed ({ExceptionType}).")]
    private static partial void LogImportFailed(ILogger logger, string exceptionType);
}
