namespace Autheris.Application.Data.Interfaces;

using System.Collections.Generic;
using Autheris.Domain.Model;

/// <summary>
/// Context binding for keyset cursors (Issue #237 / #222) to prevent cross-dataset, cross-tenant, or cross-direction cursor replay.
/// </summary>
public sealed record KeysetCursorContext(
    string? TenantId = null,
    string? DataSource = null,
    string? TableName = null,
    string? Direction = null,
    IReadOnlyList<string>? Columns = null,
    string? Caller = null);

/// <summary>
/// PLAN-DATA-178 / SEC-DATA178-03: Deterministic keyset cursor encoder and decoder
/// providing opaque, tamper-evident composite primary key pagination tokens.
/// </summary>
public interface IKeysetCursorEncoder
{
    /// <summary>
    /// Serializes primary key values into an opaque base64url JSON token, optionally binding contextual metadata.
    /// </summary>
    string EncodeCursor(IReadOnlyDictionary<string, object?> pkValues, KeysetCursorContext? context = null);

    /// <summary>
    /// Decodes and strictly validates a base64url JSON cursor against table primary key columns, types, and context binding.
    /// Throws <see cref="InvalidCursorException"/> on malformed, missing, extraneous keys, type mismatch, or context mismatch.
    /// </summary>
    IReadOnlyDictionary<string, object?> DecodeCursor(string cursorBase64, TableMetadata metadata, KeysetCursorContext? context = null);
}
