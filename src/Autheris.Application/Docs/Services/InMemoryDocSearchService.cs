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
        _chunks[chunk.Id.ToString()] = chunk;
        return Task.CompletedTask;
    }

    public Task IndexChunksAsync(IEnumerable<DocChunk> chunks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        foreach (var chunk in chunks)
        {
            _chunks[chunk.Id.ToString()] = chunk;
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<DocSearchResult>> SearchAsync(
        string query,
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
                    SourceFile: chunk.SourceFile
                ));
            }
        }

        var sorted = results
            .OrderByDescending(r => r.RelevanceScore)
            .Take(limit > 0 ? limit : 10)
            .ToList();

        return Task.FromResult<IReadOnlyList<DocSearchResult>>(sorted);
    }

    public Task<DocChunk?> GetChunkAsync(DocChunkIdentifier id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        _chunks.TryGetValue(id.ToString(), out var chunk);
        return Task.FromResult(chunk);
    }

    public Task<ErrorCodeDefinition?> LookupErrorCodeAsync(string errorCode, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(errorCode))
        {
            return Task.FromResult<ErrorCodeDefinition?>(null);
        }

        var cleanCode = errorCode.Trim().ToUpperInvariant();
        var matchingChunk = _chunks.Values.FirstOrDefault(c =>
            c.ErrorCodes.Any(ec => ec.Equals(cleanCode, StringComparison.OrdinalIgnoreCase)) ||
            c.Title.Contains(cleanCode, StringComparison.OrdinalIgnoreCase));

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
            SourceFile: matchingChunk.SourceFile
        );

        return Task.FromResult<ErrorCodeDefinition?>(def);
    }

    public Task<IReadOnlyList<string>> ListTopicsAsync(string? appId = null, CancellationToken cancellationToken = default)
    {
        var query = _chunks.Values.AsEnumerable();
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
}
