namespace Autheris.Application.Data.Changes;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>
/// Issue #77: Stable error codes for governed change detection and stateless <c>changesSince</c> cursors.
/// </summary>
public static class ChangeDetectionErrorCodes
{
    public const string CursorCallerMismatch = "CURSOR_CALLER_MISMATCH";
    public const string CursorExpired = "CURSOR_EXPIRED";
    public const string CursorInvalid = "CURSOR_INVALID";
    public const string PrimaryKeyNotClear = "PRIMARY_KEY_NOT_CLEAR";
    public const string ChangeTrackingNotSupported = "CHANGE_TRACKING_NOT_SUPPORTED";
}

/// <summary>
/// Options for governed change tokens (<c>ETag</c>) and HMAC-signed <c>changesSince</c> cursors (#77).
/// </summary>
public sealed record ChangeDetectionOptions
{
    /// <summary>HMAC-SHA256 signing secret for stateless change tokens and cursors.</summary>
    public string SigningSecret { get; init; } = "autheris-default-change-detection-hmac-secret-key-2026";

    /// <summary>Maximum age of an issued cursor before it is rejected with <c>CURSOR_EXPIRED</c>.</summary>
    public TimeSpan MaxCursorAge { get; init; } = TimeSpan.FromDays(7);

    /// <summary>Maximum number of changed keys materialized in a single <c>changesSince</c> call.</summary>
    public int MaxChangesPerCall { get; init; } = 1000;
}

/// <summary>
/// Result of checking whether one or more tables have changed since a caller's <c>If-None-Match</c> token (#77 Part 1).
/// </summary>
public sealed record ChangeCheckResult(
    bool SupportsChangeTracking,
    bool NotModified,
    string? ChangeToken,
    long? CurrentVersion,
    double QueryCost);

/// <summary>
/// Raw change record reported by an underlying change tracking store (e.g., MSSQL Change Tracking, PostgreSQL xmin/CDC, Iceberg snapshot diff).
/// </summary>
public sealed record SourceChangeRecord(
    IReadOnlyDictionary<string, object?> PrimaryKeyValues,
    CdcOperation Operation,
    long ChangeVersion,
    IReadOnlyDictionary<string, object?>? PreImage = null);

/// <summary>
/// Signed stateless cursor payload for <c>changesSince</c> queries (#77 Part 2).
/// </summary>
public sealed record ChangesSinceCursorPayload(
    string TenantId,
    string SubjectId,
    string TableIdentifier,
    long SyncVersion,
    DateTimeOffset IssuedAtUtc);

/// <summary>
/// Request for a governed <c>changesSince</c> delta query (#77 Part 2).
/// </summary>
public sealed record ChangesSinceRequest(
    TableIdentifier Table,
    string? Cursor,
    ClaimsPrincipal User,
    TenantId TenantId,
    IReadOnlyList<IReadOnlyDictionary<string, object?>>? KnownKeys = null,
    IReadOnlyList<string>? SelectColumns = null,
    string? ClientIp = null);

/// <summary>
/// Governed delta response containing net upserted rows, removed primary keys, and the next signed cursor (#77 Part 2).
/// </summary>
public sealed record ChangesSinceResponse(
    string Dataset,
    string NextCursor,
    long SyncVersion,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Upserted,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Removed,
    bool HasMore = false);
