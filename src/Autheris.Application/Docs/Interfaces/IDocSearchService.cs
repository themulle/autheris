namespace Autheris.Application.Docs.Interfaces;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Model;

public interface IDocSearchService
{
    Task IndexChunkAsync(DocChunk chunk, CancellationToken cancellationToken = default);
    Task IndexChunksAsync(IEnumerable<DocChunk> chunks, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DocSearchResult>> SearchAsync(string query, DocCategory? category = null, int limit = 10, CancellationToken cancellationToken = default);
    Task<DocChunk?> GetChunkAsync(DocChunkIdentifier id, CancellationToken cancellationToken = default);
    Task<ErrorCodeDefinition?> LookupErrorCodeAsync(string errorCode, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> ListTopicsAsync(string? appId = null, CancellationToken cancellationToken = default);
}

public interface IDocIngestor
{
    Task<IReadOnlyList<DocChunk>> IngestMarkdownFileAsync(string filePath, string appId, DocCategory category, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DocChunk>> IngestMarkdownContentAsync(string markdownContent, string docId, string appId, DocCategory category, string sourceFile, CancellationToken cancellationToken = default);
}
