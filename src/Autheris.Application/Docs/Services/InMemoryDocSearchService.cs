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
    private readonly object _writeGate = new();
    private volatile ConcurrentDictionary<DocIndexKey, DocChunk> _chunks = new(DocIndexKeyComparer.Instance);

    public Task IndexChunkAsync(DocChunk chunk, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        lock (_writeGate)
        {
            _chunks[KeyOf(chunk)] = chunk;
        }
        return Task.CompletedTask;
    }

    public Task IndexChunksAsync(IEnumerable<DocChunk> chunks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        lock (_writeGate)
        {
            var index = _chunks;
            foreach (var chunk in chunks)
            {
                if (chunk is not null)
                {
                    index[KeyOf(chunk)] = chunk;
                }
            }
        }
        return Task.CompletedTask;
    }

    public Task RemoveDocumentAsync(DocKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        var tenantKey = key.TenantId?.Trim() ?? string.Empty;
        var categoryName = key.Category.ToString();

        lock (_writeGate)
        {
            var index = _chunks;
            foreach (var existingKey in index.Keys)
            {
                if (string.Equals(existingKey.TenantKey, tenantKey, StringComparison.Ordinal) &&
                    string.Equals(existingKey.ChunkId.AppId, key.AppId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(existingKey.ChunkId.Category, categoryName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(existingKey.ChunkId.DocId, key.DocId, StringComparison.OrdinalIgnoreCase))
                {
                    index.TryRemove(existingKey, out _);
                }
            }
        }

        return Task.CompletedTask;
    }

    public Task RemoveTenantAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return Task.CompletedTask;
        }

        var normalized = tenantId.Trim();
        lock (_writeGate)
        {
            var index = _chunks;
            foreach (var existingKey in index.Keys)
            {
                if (string.Equals(existingKey.TenantKey, normalized, StringComparison.Ordinal))
                {
                    index.TryRemove(existingKey, out _);
                }
            }
        }

        return Task.CompletedTask;
    }

    public Task ReplaceAllAsync(IEnumerable<DocChunk> chunks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        var next = new ConcurrentDictionary<DocIndexKey, DocChunk>(DocIndexKeyComparer.Instance);
        foreach (var chunk in chunks)
        {
            if (chunk is not null)
            {
                next[KeyOf(chunk)] = chunk;
            }
        }

        lock (_writeGate)
        {
            _chunks = next;
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<DocChunk>> SnapshotVisibleChunksAsync(DocAccessScope scope, CancellationToken cancellationToken = default)
    {
        var snapshot = _chunks.Values
            .Where(scope.CanSee)
            .Select(c => c with { SourceFile = DocSourcePath.ToSafe(c.SourceFile) })
            .ToList();
        return Task.FromResult<IReadOnlyList<DocChunk>>(snapshot);
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

        var rawQuery = query.Trim().ToLowerInvariant();
        if (rawQuery.Length >= 2 && !terms.Contains(rawQuery, StringComparer.OrdinalIgnoreCase))
        {
            terms.Add(rawQuery);
        }

        var results = new List<DocSearchResult>();
        var snapshot = _chunks;

        foreach (var chunk in snapshot.Values)
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

                if (chunk.ErrorCodes.Any(ec => ec.Equals(term, StringComparison.OrdinalIgnoreCase)))
                {
                    score += 3.0;
                    termMatched = true;
                }

                if (titleLower.Contains(term, StringComparison.Ordinal))
                {
                    score += 2.0;
                    termMatched = true;
                }

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
                    SourceFile: DocSourcePath.ToSafe(chunk.SourceFile),
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
        var snapshot = _chunks;

        // Official documentation always takes precedence over tenant documentation for identical identifiers.
        if (snapshot.TryGetValue(new DocIndexKey(string.Empty, id), out var official) && scope.CanSee(official))
        {
            return Task.FromResult<DocChunk?>(official with { SourceFile = DocSourcePath.ToSafe(official.SourceFile) });
        }

        if (scope.TenantId is not null &&
            snapshot.TryGetValue(new DocIndexKey(scope.TenantId, id), out var tenantChunk) &&
            scope.CanSee(tenantChunk))
        {
            return Task.FromResult<DocChunk?>(tenantChunk with { SourceFile = DocSourcePath.ToSafe(tenantChunk.SourceFile) });
        }

        return Task.FromResult<DocChunk?>(null);
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
            SourceFile: DocSourcePath.ToSafe(matchingChunk.SourceFile),
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
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();

        return Task.FromResult<IReadOnlyList<string>>(topics);
    }

    private static DocIndexKey KeyOf(DocChunk chunk) =>
        new(chunk.TenantId?.Trim() ?? string.Empty, chunk.Id);

    /// <summary>0 = exact match on the error-code metadata, 1 = the title contains the code, -1 = no match.</summary>
    private static int ErrorCodeMatchRank(DocChunk chunk, string cleanCode)
    {
        if (chunk.ErrorCodes.Any(ec => ec.Equals(cleanCode, StringComparison.OrdinalIgnoreCase)))
        {
            return 0;
        }

        return chunk.Title.Contains(cleanCode, StringComparison.OrdinalIgnoreCase) ? 1 : -1;
    }

    private readonly record struct DocIndexKey(string TenantKey, DocChunkIdentifier ChunkId);

    private sealed class DocIndexKeyComparer : IEqualityComparer<DocIndexKey>
    {
        public static readonly DocIndexKeyComparer Instance = new();

        public bool Equals(DocIndexKey x, DocIndexKey y) =>
            string.Equals(x.TenantKey, y.TenantKey, StringComparison.Ordinal) &&
            string.Equals(x.ChunkId.AppId, y.ChunkId.AppId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.ChunkId.Category, y.ChunkId.Category, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.ChunkId.DocId, y.ChunkId.DocId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.ChunkId.SectionId, y.ChunkId.SectionId, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(DocIndexKey obj)
        {
            var hash = new HashCode();
            hash.Add(obj.TenantKey, StringComparer.Ordinal);
            hash.Add(obj.ChunkId.AppId, StringComparer.OrdinalIgnoreCase);
            hash.Add(obj.ChunkId.Category, StringComparer.OrdinalIgnoreCase);
            hash.Add(obj.ChunkId.DocId, StringComparer.OrdinalIgnoreCase);
            hash.Add(obj.ChunkId.SectionId, StringComparer.OrdinalIgnoreCase);
            return hash.ToHashCode();
        }
    }
}
