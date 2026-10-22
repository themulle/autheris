namespace Autheris.Application.Docs.Services;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Docs.Interfaces;
using Autheris.Domain.Model;

public sealed class InMemoryDocSearchService : IDocSearchService
{
    private readonly ConcurrentDictionary<string, DocChunk> _chunks = new(StringComparer.OrdinalIgnoreCase);

    public Task IndexChunkAsync(DocChunk chunk, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        Index(chunk);
        return Task.CompletedTask;
    }

    public Task IndexChunksAsync(IEnumerable<DocChunk> chunks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        foreach (var chunk in chunks)
        {
            Index(chunk);
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<DocSearchResult>> SearchAsync(
        string query,
        DocCategory? category = null,
        int limit = 10,
        CancellationToken cancellationToken = default) =>
        SearchAsync(query, DocAccessScope.OfficialOnly, category, limit, cancellationToken);

    public Task<IReadOnlyList<DocSearchResult>> SearchAsync(
        string query,
        DocAccessScope scope,
        DocCategory? category = null,
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Task.FromResult<IReadOnlyList<DocSearchResult>>(Array.Empty<DocSearchResult>());
        }

        var terms = query.ToLowerInvariant()
            .Split([' ', '\t', '\r', '\n', '-', '_'], StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (terms.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<DocSearchResult>>(Array.Empty<DocSearchResult>());
        }

        var results = new List<DocSearchResult>();

        foreach (var chunk in _chunks.Values)
        {
            if (!scope.CanSee(chunk))
            {
                continue;
            }

            if (category.HasValue && chunk.Category != category.Value)
            {
                continue;
            }

            var matchedTerms = new List<string>();
            double score = 0.0;

            var titleLower = chunk.Title.ToLowerInvariant();
            var contentLower = chunk.Content.ToLowerInvariant();

            foreach (var term in terms)
            {
                var termMatched = false;

                // 1. Error code match (high boost: 3.0)
                if (chunk.ErrorCodes.Any(ec => ec.Equals(term, StringComparison.OrdinalIgnoreCase)))
                {
                    score += 3.0;
                    termMatched = true;
                }

                // 2. Title match (boost: 2.0)
                if (titleLower.Contains(term, StringComparison.Ordinal))
                {
                    score += 2.0;
                    termMatched = true;
                }

                // 3. Keyword / Content match (base: 1.0)
                if (chunk.Keywords.Any(k => k.Equals(term, StringComparison.OrdinalIgnoreCase)) ||
                    contentLower.Contains(term, StringComparison.Ordinal))
                {
                    score += 1.0;
                    termMatched = true;
                }

                if (termMatched)
                {
                    matchedTerms.Add(term);
                }
            }

            if (score > 0)
            {
                results.Add(new DocSearchResult(
                    Id: chunk.Id,
                    AppId: chunk.AppId,
                    Category: chunk.Category,
                    Title: chunk.Title,
                    Summary: chunk.Summary,
                    RelevanceScore: score,
                    MatchedTerms: matchedTerms,
                    SourceFile: DocProvenance.ToSafeSourcePath(chunk.SourceFile),
                    TenantId: chunk.TenantId
                ));
            }
        }

        // Stable ordering: score, then official documentation before tenant documents, then the chunk identifier.
        var sorted = results
            .OrderByDescending(r => r.RelevanceScore)
            .ThenBy(r => r.TenantId is null ? 0 : 1)
            .ThenBy(r => r.Id.ToString(), StringComparer.Ordinal)
            .Take(limit > 0 ? limit : 10)
            .ToList();

        return Task.FromResult<IReadOnlyList<DocSearchResult>>(sorted);
    }

    public Task<DocChunk?> GetChunkAsync(DocChunkIdentifier id, CancellationToken cancellationToken = default) =>
        GetChunkAsync(id, DocAccessScope.OfficialOnly, cancellationToken);

    public Task<DocChunk?> GetChunkAsync(DocChunkIdentifier id, DocAccessScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (!_chunks.TryGetValue(id.ToString(), out var chunk) || !scope.CanSee(chunk))
        {
            // A chunk of another tenant looks exactly like a missing one.
            return Task.FromResult<DocChunk?>(null);
        }

        return Task.FromResult<DocChunk?>(chunk with { SourceFile = DocProvenance.ToSafeSourcePath(chunk.SourceFile) });
    }

    public Task<ErrorCodeDefinition?> LookupErrorCodeAsync(string errorCode, CancellationToken cancellationToken = default) =>
        LookupErrorCodeAsync(errorCode, DocAccessScope.OfficialOnly, cancellationToken);

    public Task<ErrorCodeDefinition?> LookupErrorCodeAsync(string errorCode, DocAccessScope scope, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(errorCode))
        {
            return Task.FromResult<ErrorCodeDefinition?>(null);
        }

        var cleanCode = errorCode.Trim().ToUpperInvariant();

        // Deterministic precedence (the dictionary enumeration order is unspecified): exact error-code metadata match before
        // a title match, official documentation before tenant documents, then the chunk identifier.
        var matchingChunk = _chunks.Values
            .Where(c => scope.CanSee(c))
            .Select(c => (Chunk: c, MatchRank: ErrorCodeMatchRank(c, cleanCode)))
            .Where(x => x.MatchRank >= 0)
            .OrderBy(x => x.MatchRank)
            .ThenBy(x => x.Chunk.TenantId is null ? 0 : 1)
            .ThenBy(x => x.Chunk.Id.ToString(), StringComparer.Ordinal)
            .Select(x => x.Chunk)
            .FirstOrDefault();

        if (matchingChunk == null)
        {
            return Task.FromResult<ErrorCodeDefinition?>(null);
        }

        var def = new ErrorCodeDefinition(
            ErrorCode: cleanCode,
            Symptom: matchingChunk.Summary,
            RootCause: $"Referenced in documentation section '{matchingChunk.Title}'.",
            Resolution: matchingChunk.Content,
            RunbookRef: matchingChunk.Category == DocCategory.Runbook ? matchingChunk.Id.ToString() : null,
            SourceFile: DocProvenance.ToSafeSourcePath(matchingChunk.SourceFile),
            TenantId: matchingChunk.TenantId
        );

        return Task.FromResult<ErrorCodeDefinition?>(def);
    }

    public Task<IReadOnlyList<string>> ListTopicsAsync(string? appId = null, CancellationToken cancellationToken = default) =>
        ListTopicsAsync(DocAccessScope.OfficialOnly, appId, cancellationToken);

    public Task<IReadOnlyList<string>> ListTopicsAsync(DocAccessScope scope, string? appId = null, CancellationToken cancellationToken = default)
    {
        var query = _chunks.Values.Where(c => scope.CanSee(c));
        if (!string.IsNullOrWhiteSpace(appId))
        {
            query = query.Where(c => c.AppId.Equals(appId, StringComparison.OrdinalIgnoreCase));
        }

        var topics = query
            .Select(c => $"{c.AppId} / {c.Category}: {c.Title}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t)
            .ToList();

        return Task.FromResult<IReadOnlyList<string>>(topics);
    }

    /// <summary>0 = exact match on the error-code metadata, 1 = the title contains the code, -1 = no match.</summary>
    private static int ErrorCodeMatchRank(DocChunk chunk, string cleanCode)
    {
        if (chunk.ErrorCodes.Any(ec => ec.Equals(cleanCode, StringComparison.OrdinalIgnoreCase)))
        {
            return 0;
        }

        return chunk.Title.Contains(cleanCode, StringComparison.OrdinalIgnoreCase) ? 1 : -1;
    }

    /// <summary>
    /// Chunk identifiers are not tenant-scoped (AppId::Category::DocId#Section), so different files can map to the same key.
    /// An official chunk replaces anything; a tenant chunk replaces only a chunk of the same tenant. A tenant can therefore
    /// never displace official documentation or another tenant's document from the index.
    /// </summary>
    private void Index(DocChunk chunk)
    {
        _chunks.AddOrUpdate(
            chunk.Id.ToString(),
            chunk,
            (_, existing) => chunk.TenantId is null || string.Equals(existing.TenantId, chunk.TenantId, StringComparison.Ordinal)
                ? chunk
                : existing);
    }
}
