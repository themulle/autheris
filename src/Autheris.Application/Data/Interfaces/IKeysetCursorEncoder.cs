namespace Autheris.Application.Data.Interfaces;

using System.Collections.Generic;
using Autheris.Domain.Model;

/// <summary>
/// Context binding for keyset cursors (Issue #237 / #222) to prevent cross-dataset, cross-tenant, cross-caller,
/// cross-filter or cross-direction cursor replay. Bulk-stream cursors are built exclusively through
/// <c>KeysetCursorContextFactory.ForBulkStream</c> so the issuing endpoint and the decoding planner bind the identical context.
/// </summary>
/// <param name="TenantId">The request tenant id value (compared exactly).</param>
/// <param name="DataSource">The catalog domain of the table (compared case-insensitively).</param>
/// <param name="TableName">The table name (compared case-insensitively).</param>
/// <param name="Direction">The seek direction (compared case-insensitively).</param>
/// <param name="Columns">The key columns the cursor seeks on (compared case-insensitively, in order).</param>
/// <param name="Caller">The caller SID the cursor was issued to (compared exactly).</param>
/// <param name="Schema">The table schema (compared case-insensitively).</param>
/// <param name="RequestHash">Digest of the request shape (filter and projection) the cursor continues (compared exactly).</param>
public sealed record KeysetCursorContext(
    string? TenantId = null,
    string? DataSource = null,
    string? TableName = null,
    string? Direction = null,
    IReadOnlyList<string>? Columns = null,
    string? Caller = null,
    string? Schema = null,
    string? RequestHash = null);

/// <summary>
/// PLAN-DATA-178 / SEC-DATA178-03: Deterministic keyset cursor encoder and decoder
/// providing opaque, tamper-evident composite primary key pagination tokens.
/// </summary>
public interface IKeysetCursorEncoder
{
    /// <summary>
    /// Serializes primary key values into an opaque base64url JSON token whose signature binds <paramref name="context"/>.
    /// A context is mandatory: unbound cursors are never issued.
    /// </summary>
    string EncodeCursor(IReadOnlyDictionary<string, object?> pkValues, KeysetCursorContext context);

    /// <summary>
    /// Decodes and strictly validates a base64url JSON cursor against table primary key columns, types, and the exact
    /// <paramref name="context"/> binding (no unbound fallback). Throws <see cref="InvalidCursorException"/> on malformed,
    /// missing, extraneous keys, type mismatch, or context mismatch.
    /// </summary>
    IReadOnlyDictionary<string, object?> DecodeCursor(string cursorBase64, TableMetadata metadata, KeysetCursorContext context);
}
