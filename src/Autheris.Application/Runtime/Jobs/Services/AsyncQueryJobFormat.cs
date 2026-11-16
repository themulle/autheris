namespace Autheris.Application.Runtime.Jobs.Services;

using System;

/// <summary>Allow-list and constant download metadata mapping for async query job formats.</summary>
public static class AsyncQueryJobFormat
{
    public const string Json = "json";
    public const string Csv = "csv";
    public const string JsonContentType = "application/json";
    public const string CsvContentType = "text/csv";
    public const string JsonExtension = ".json";
    public const string CsvExtension = ".csv";
    public const string InvalidFormatCode = "invalid_format";

    /// <summary>
    /// Validates and normalizes <paramref name="rawFormat"/> against the allow-list (<c>json</c> or <c>csv</c>,
    /// ordinal after trim + lowercase; null/blank defaults to <c>json</c>).
    /// </summary>
    public static bool TryNormalize(string? rawFormat, out string normalized)
    {
        if (string.IsNullOrWhiteSpace(rawFormat))
        {
            normalized = Json;
            return true;
        }

        var trimmed = rawFormat.AsSpan().Trim();
        if (trimmed.Length is < 3 or > 4)
        {
            normalized = string.Empty;
            return false;
        }

        var lowered = trimmed.ToString().ToLowerInvariant();
        if (string.Equals(lowered, Json, StringComparison.Ordinal))
        {
            normalized = Json;
            return true;
        }

        if (string.Equals(lowered, Csv, StringComparison.Ordinal))
        {
            normalized = Csv;
            return true;
        }

        normalized = string.Empty;
        return false;
    }

    public static bool TryResolveDownloadMetadata(string? storedFormat, out string contentType, out string fileExtension)
    {
        if (!TryNormalize(storedFormat, out var normalized))
        {
            contentType = string.Empty;
            fileExtension = string.Empty;
            return false;
        }

        switch (normalized)
        {
            case Json:
                contentType = JsonContentType;
                fileExtension = JsonExtension;
                return true;
            case Csv:
                contentType = CsvContentType;
                fileExtension = CsvExtension;
                return true;
            default:
                contentType = string.Empty;
                fileExtension = string.Empty;
                return false;
        }
    }
}
