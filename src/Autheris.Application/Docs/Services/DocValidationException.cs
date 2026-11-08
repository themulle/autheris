namespace Autheris.Application.Docs.Services;

using System;

/// <summary>
/// Thrown when document content, title, tags or identifiers violate ingestion validation rules.
/// </summary>
public sealed class DocValidationException(string code, string? message = null) : Exception(message ?? code)
{
    public string Code { get; } = code;
}
