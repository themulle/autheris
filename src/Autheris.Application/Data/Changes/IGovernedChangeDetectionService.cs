namespace Autheris.Application.Data.Changes;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;

/// <summary>
/// Provides table change versions, minimum valid retention versions, and raw change records since a version (#77).
/// </summary>
public interface IChangeVersionProvider
{
    /// <summary>
    /// Returns the current monotonic change version of <paramref name="table"/>, or <c>null</c> when the source does not support change tracking.
    /// </summary>
    ValueTask<long?> GetCurrentVersionAsync(TableIdentifier table, CancellationToken ct = default);

    /// <summary>
    /// Returns the minimum valid change version retained by the source for <paramref name="table"/> (analogous to MSSQL <c>CHANGE_TRACKING_MIN_VALID_VERSION</c>).
    /// A cursor older than this version fails closed with <see cref="ChangeDetectionErrorCodes.CursorExpired"/>.
    /// </summary>
    ValueTask<long?> GetMinValidVersionAsync(TableIdentifier table, CancellationToken ct = default);

    /// <summary>
    /// VF-11: true when the provider reports the pre-image of deleted and updated rows (<see cref="SourceChangeRecord.PreImage"/>). A scoped table
    /// (tenant column or row-level filter) can only disclose a removal with such a pre-image; without it changesSince is not supported there.
    /// </summary>
    bool ProvidesPreImages { get; }

    /// <summary>
    /// Returns raw change records on <paramref name="table"/> strictly after <paramref name="sinceVersion"/> up to <paramref name="upToVersion"/>,
    /// ordered by version, at most <paramref name="maxRecords"/> (VF-13). The service asks for the cap plus one to detect an overflow.
    /// </summary>
    Task<IReadOnlyList<SourceChangeRecord>> GetChangesBetweenAsync(
        TableIdentifier table,
        long sinceVersion,
        long upToVersion,
        int maxRecords,
        CancellationToken ct = default);

    /// <summary>
    /// Reads current raw rows for the specified primary keys on <paramref name="table"/>.
    /// </summary>
    Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ReadCurrentRowsByPrimaryKeysAsync(
        TableIdentifier table,
        IReadOnlyList<string> primaryKeyColumns,
        IReadOnlyList<CompositeKey> keys,
        CancellationToken ct = default);
}

/// <summary>
/// Issue #77: Governed change detection (<c>ETag</c> / <c>If-None-Match</c>) and stateless pull-based <c>changesSince</c> cursor service.
/// </summary>
public interface IGovernedChangeDetectionService
{
    /// <summary>
    /// Checks whether <paramref name="table"/> has changed relative to <paramref name="ifNoneMatchToken"/>.
    /// When the token matches the current version, returns <c>NotModified = true</c> with <c>QueryCost = 0</c> without reading any table rows.
    /// </summary>
    Task<ChangeCheckResult> CheckTableModifiedAsync(
        TableIdentifier table,
        ClaimsPrincipal user,
        TenantId tenantId,
        string? ifNoneMatchToken = null,
        string? clientIp = null,
        CancellationToken ct = default);

    /// <summary>
    /// Issues a signed stateless cursor bound to the caller's <c>(TenantId, SubjectId)</c>, table, and <paramref name="syncVersion"/>.
    /// </summary>
    string IssueCursor(
        TableIdentifier table,
        ClaimsPrincipal user,
        TenantId tenantId,
        long syncVersion,
        DateTimeOffset? issuedAtUtc = null);

    /// <summary>
    /// Validates and decodes a signed cursor for the given caller and table.
    /// Throws <c>GatewaySecurityException</c> with <c>CURSOR_CALLER_MISMATCH</c>, <c>CURSOR_EXPIRED</c>, or <c>CURSOR_INVALID</c> on violation.
    /// </summary>
    ChangesSinceCursorPayload ValidateCursor(
        string cursor,
        TableIdentifier expectedTable,
        ClaimsPrincipal user,
        TenantId tenantId,
        long? minValidVersion = null);

    /// <summary>
    /// Executes a governed <c>changesSince</c> query returning <c>Upserted</c> rows (RLS-filtered and masked),
    /// <c>Removed</c> keys (physical deletes subject to <c>DeletedKeyPolicy</c> plus RLS-exit updates), and a new signed cursor.
    /// </summary>
    Task<ChangesSinceResponse> GetChangesSinceAsync(
        ChangesSinceRequest request,
        CancellationToken ct = default);
}
