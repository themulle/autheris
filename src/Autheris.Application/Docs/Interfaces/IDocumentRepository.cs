namespace Autheris.Application.Docs.Interfaces;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Model;

/// <summary>Composite key identifying an official (<c>TenantId == null</c>) or tenant document.</summary>
public sealed record DocKey(string? TenantId, string AppId, DocCategory Category, string DocId);

/// <summary>Per-tenant document quota enforced transactionally on writes.</summary>
public sealed record DocQuota(int MaxDocumentsPerTenant, long MaxBytesPerTenant);

/// <summary>Persisted document state with optimistic version and optional tombstone timestamp.</summary>
public sealed record DocRecord(
    string? TenantId, string AppId, DocCategory Category, string DocId,
    string Title, string Content, string ContentSha256, int ByteCount,
    IReadOnlyList<string> Tags, string? ErrorCode, string? AuthorSid,
    long Version, DateTimeOffset UpdatedAt, DateTimeOffset? DeletedAt = null)
{
    public bool IsDeleted => DeletedAt.HasValue;
    public string ETag => $"\"{Version}\"";
}

/// <summary>Snapshot of current per-tenant document quota usage.</summary>
public sealed record DocTenantUsage(string TenantId, int DocCount, long TotalBytes, DateTimeOffset UpdatedAt);

/// <summary>Outcome status of a document store write operation.</summary>
public enum DocWriteStatus { Created, Replaced, Deleted, AlreadyExists, VersionMismatch, NotFound, ReservedKey, QuotaExceeded }

/// <summary>Result returned by atomic document write operations.</summary>
public sealed record DocWriteOutcome(DocWriteStatus Status, DocRecord? Record = null)
{
    public bool Succeeded => Status is DocWriteStatus.Created or DocWriteStatus.Replaced or DocWriteStatus.Deleted;
}

/// <summary>Transactional store for official and tenant-scoped documentation assets and quotas.</summary>
public interface IDocumentRepository
{
    Task<DocWriteOutcome> CreateAsync(DocRecord record, DocQuota quota, CancellationToken ct = default);
    Task<DocWriteOutcome> ReplaceAsync(DocRecord record, long? expectedVersion, DocQuota quota, CancellationToken ct = default);
    Task<DocWriteOutcome> CreateManyAsync(IReadOnlyList<DocRecord> records, DocQuota quota, CancellationToken ct = default);
    Task<DocWriteOutcome> DeleteAsync(DocKey key, long? expectedVersion, DateTimeOffset deletedAt, CancellationToken ct = default);
    Task<int> PurgeTenantAsync(string tenantId, CancellationToken ct = default);
    Task<DocRecord?> GetAsync(DocKey key, CancellationToken ct = default);
    Task<IReadOnlyList<DocRecord>> ReadChangedSinceAsync(DateTimeOffset? since, CancellationToken ct = default);
    Task<int> PurgeTombstonesAsync(DateTimeOffset cutoff, CancellationToken ct = default);
    Task<DocTenantUsage> GetTenantUsageAsync(string tenantId, CancellationToken ct = default);
}
