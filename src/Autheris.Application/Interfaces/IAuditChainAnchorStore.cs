using Autheris.Domain.Model;

namespace Autheris.Application.Interfaces;

/// <summary>
/// SEC H-17: Signed end anchor of the audit hash chain (last sequence number + last entry hash).
/// <see cref="Signature"/> is an HMAC-SHA256 (hex) computed by the audit repository with a dedicated HKDF sub-key.
/// </summary>
public sealed record AuditChainAnchor(long Sequence, string EntryHash, DateTimeOffset UpdatedAt, string Signature);

/// <summary>
/// SEC H-17: Persists the audit chain end anchor OUTSIDE of the audited database (file on a separate volume,
/// WORM storage, notary service ...). The repository never trusts the database alone for the chain tail.
/// </summary>
public interface IAuditChainAnchorStore
{
    /// <summary>Returns the last persisted anchor or <c>null</c> if none exists yet.</summary>
    AuditChainAnchor? Load();

    /// <summary>Atomically replaces the persisted anchor.</summary>
    void Save(AuditChainAnchor anchor);
}

/// <summary>A contiguous slice (by rowid) of the audit chain.</summary>
public sealed record AuditChainRecord(long RowId, long? Sequence, AuditLogEntry Entry);

/// <summary>Rowid range and exact record count of the audit chain covering an export window.</summary>
public sealed record AuditChainRange(long FirstRowId, long LastRowId, long Count);

/// <summary>
/// SEC M-31: Gap-free, paged access to the audit chain for WORM exports (no silent result capping).
/// </summary>
public interface IAuditChainExportSource
{
    /// <summary>Determines the contiguous rowid range spanning all entries in the window (null if none).</summary>
    Task<AuditChainRange?> GetAuditChainRangeAsync(DateTimeOffset windowFrom, DateTimeOffset windowTo, CancellationToken ct = default);

    /// <summary>Returns up to <paramref name="pageSize"/> records with rowid in (<paramref name="afterRowId"/>, <paramref name="lastRowIdInclusive"/>] ordered by rowid.</summary>
    Task<IReadOnlyList<AuditChainRecord>> GetAuditChainPageAsync(long afterRowId, long lastRowIdInclusive, int pageSize, CancellationToken ct = default);

    /// <summary>The current end anchor if it exists and its signature is valid; otherwise <c>null</c>.</summary>
    AuditChainAnchor? GetVerifiedChainAnchor();
}
