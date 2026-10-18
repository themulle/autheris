namespace Autheris.Application.Data.Interfaces;

using System.Collections.Generic;
using Autheris.Domain.Model;

/// <summary>
/// PLAN-DATA-178 / SEC-DATA178-03: Deterministic keyset cursor encoder and decoder
/// providing opaque, tamper-evident composite primary key pagination tokens.
/// </summary>
public interface IKeysetCursorEncoder
{
    /// <summary>
    /// Serializes primary key values into an opaque base64url JSON token.
    /// </summary>
    string EncodeCursor(IReadOnlyDictionary<string, object?> pkValues);

    /// <summary>
    /// Decodes and strictly validates a base64url JSON cursor against table primary key columns and types.
    /// Throws <see cref="InvalidCursorException"/> on malformed, missing, extraneous keys, or type mismatch.
    /// </summary>
    IReadOnlyDictionary<string, object?> DecodeCursor(string cursorBase64, TableMetadata metadata);
}
