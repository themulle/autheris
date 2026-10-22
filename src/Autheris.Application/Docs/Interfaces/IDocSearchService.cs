namespace Autheris.Application.Docs.Interfaces;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Model;

/// <summary>
/// Which documents a caller may see. Official documentation (shipped with the gateway, synced from the document store or
/// deposited by a cluster administrator; <see cref="DocChunk.TenantId"/> is null) is visible to everyone. A document deposited
/// by a tenant is visible only to callers of that tenant, so one tenant can never place text in front of another tenant's agents.
/// The default value sees official documentation only.
/// </summary>
public readonly record struct DocAccessScope
{
    private DocAccessScope(string? tenantId) => TenantId = tenantId;

    /// <summary>The caller's tenant; null when the caller is not bound to a tenant (official documentation only).</summary>
    public string? TenantId { get; }

    /// <summary>Official documentation only (callers without a tenant-bound session, e.g. the REST discovery endpoints).</summary>
    public static DocAccessScope OfficialOnly => default;

    /// <summary>Official documentation plus the documents deposited by <paramref name="tenantId"/>.</summary>
    public static DocAccessScope ForTenant(string? tenantId) =>
        string.IsNullOrWhiteSpace(tenantId) ? default : new DocAccessScope(tenantId.Trim());

    public bool CanSee(DocChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        return chunk.TenantId is null ||
               (TenantId is not null && string.Equals(chunk.TenantId, TenantId, StringComparison.Ordinal));
    }
}

public interface IDocSearchService
{
    Task IndexChunkAsync(DocChunk chunk, CancellationToken cancellationToken = default);
    Task IndexChunksAsync(IEnumerable<DocChunk> chunks, CancellationToken cancellationToken = default);

    // The unscoped overloads see official documentation only (DocAccessScope.OfficialOnly).
    Task<IReadOnlyList<DocSearchResult>> SearchAsync(string query, DocCategory? category = null, int limit = 10, CancellationToken cancellationToken = default);
    Task<DocChunk?> GetChunkAsync(DocChunkIdentifier id, CancellationToken cancellationToken = default);
    Task<ErrorCodeDefinition?> LookupErrorCodeAsync(string errorCode, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> ListTopicsAsync(string? appId = null, CancellationToken cancellationToken = default);

    // Tenant-scoped overloads: official documentation plus the caller tenant's own documents.
    Task<IReadOnlyList<DocSearchResult>> SearchAsync(string query, DocAccessScope scope, DocCategory? category = null, int limit = 10, CancellationToken cancellationToken = default);
    Task<DocChunk?> GetChunkAsync(DocChunkIdentifier id, DocAccessScope scope, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves an error code deterministically: an exact match on the chunks' error-code metadata wins over a title match,
    /// official documentation wins over tenant documents within the same match kind, and remaining ties are broken by the
    /// chunk identifier (ordinal), so a tenant document can never shadow the official definition of a code.
    /// </summary>
    Task<ErrorCodeDefinition?> LookupErrorCodeAsync(string errorCode, DocAccessScope scope, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> ListTopicsAsync(DocAccessScope scope, string? appId = null, CancellationToken cancellationToken = default);
}

public interface IDocIngestor
{
    Task<IReadOnlyList<DocChunk>> IngestMarkdownFileAsync(string filePath, string appId, DocCategory category, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DocChunk>> IngestMarkdownContentAsync(string markdownContent, string docId, string appId, DocCategory category, string sourceFile, CancellationToken cancellationToken = default);
}
