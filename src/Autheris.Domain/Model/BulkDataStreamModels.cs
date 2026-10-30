namespace Autheris.Domain.Model;

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// PLAN-DATA-178: Request payload for governed single-table bulk keyset streaming.
/// </summary>
public sealed record BulkTableStreamRequest(
    [property: JsonPropertyName("select")] IReadOnlyList<string>? Select = null,
    [property: JsonPropertyName("filter")] TypedFilterNode? Filter = null,
    [property: JsonPropertyName("cursor")] string? Cursor = null,
    [property: JsonPropertyName("limit")] int? Limit = null,
    [property: JsonPropertyName("format")] string Format = "ndjson",
    [property: JsonPropertyName("includeMetadata")] bool IncludeMetadata = false)
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; init; }
}

/// <summary>
/// PLAN-DATA-178: Typed JSON filter AST node supporting parameter-driven evaluation
/// with zero raw SQL injection risk.
/// </summary>
public sealed record TypedFilterNode(
    [property: JsonPropertyName("field")] string? Field = null,
    [property: JsonPropertyName("op")] string? Op = null,
    [property: JsonPropertyName("value")] JsonElement? Value = null,
    [property: JsonPropertyName("and")] IReadOnlyList<TypedFilterNode>? And = null,
    [property: JsonPropertyName("or")] IReadOnlyList<TypedFilterNode>? Or = null,
    [property: JsonPropertyName("not")] TypedFilterNode? Not = null);

/// <summary>
/// Keyset pagination cursor token representation.
/// </summary>
public sealed record KeysetCursorToken(
    IReadOnlyDictionary<string, object?> KeyValues);

/// <summary>
/// Thrown when a keyset cursor is invalid, malformed, tampered, or mismatched with table keys.
/// </summary>
public sealed class InvalidCursorException : ArgumentException
{
    public const string ErrorCode = "INVALID_CURSOR";

    public InvalidCursorException(string message) : base(message) { }
    public InvalidCursorException(string message, Exception innerException) : base(message, innerException) { }
    public InvalidCursorException(string message, string paramName) : base(message, paramName) { }
}

/// <summary>
/// Thrown when keyset streaming is unavailable because the table lacks primary key / unique index
/// or key columns are masked / denied.
/// </summary>
public sealed class KeysetStreamingUnavailableException : ArgumentException
{
    public const string ErrorCode = "KEYSET_STREAMING_UNAVAILABLE";

    public KeysetStreamingUnavailableException(string message) : base(message) { }
    public KeysetStreamingUnavailableException(string message, Exception innerException) : base(message, innerException) { }
    public KeysetStreamingUnavailableException(string message, string paramName) : base(message, paramName) { }
}

/// <summary>
/// Thrown when a table declares required filter columns that are not constrained in the request or row policy.
/// </summary>
public sealed class RequiredFilterColumnMissingException : ArgumentException
{
    public const string ErrorCode = "REQUIRED_FILTER_COLUMN_MISSING";

    public RequiredFilterColumnMissingException(string message) : base(message) { }
    public RequiredFilterColumnMissingException(string message, Exception innerException) : base(message, innerException) { }
    public RequiredFilterColumnMissingException(string message, string paramName) : base(message, paramName) { }
}
