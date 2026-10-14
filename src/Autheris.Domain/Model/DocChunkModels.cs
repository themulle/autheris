namespace Autheris.Domain.Model;

using System;
using System.Collections.Generic;

public enum DocCategory
{
    Runbook,
    Architecture,
    Adr,
    ApiEndpoint,
    ErrorCode,
    Configuration,
    Guide
}

public sealed record DocChunkIdentifier(string AppId, string Category, string DocId, string SectionId)
{
    public override string ToString() => $"{AppId}::{Category}::{DocId}#{SectionId}";

    public static bool TryParse(string input, out DocChunkIdentifier? identifier)
    {
        identifier = null;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var parts = input.Split("::", StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
        {
            return false;
        }

        var appId = parts[0];
        var category = parts[1];
        var remainder = parts[2];

        var hashIdx = remainder.IndexOf('#', StringComparison.Ordinal);
        var docId = hashIdx >= 0 ? remainder[..hashIdx] : remainder;
        var sectionId = hashIdx >= 0 && hashIdx < remainder.Length - 1 ? remainder[(hashIdx + 1)..] : "root";

        identifier = new DocChunkIdentifier(appId, category, docId, sectionId);
        return true;
    }
}

public sealed record DocChunk(
    DocChunkIdentifier Id,
    string AppId,
    DocCategory Category,
    string Title,
    string Content,
    string Summary,
    IReadOnlyList<string> Keywords,
    IReadOnlyList<string> ErrorCodes,
    string SourceFile,
    string? AnchorId,
    DateTimeOffset UpdatedAt
);

public sealed record DocSearchResult(
    DocChunkIdentifier Id,
    string AppId,
    DocCategory Category,
    string Title,
    string Summary,
    double RelevanceScore,
    IReadOnlyList<string> MatchedTerms,
    string SourceFile
);

public sealed record ErrorCodeDefinition(
    string ErrorCode,
    string Symptom,
    string RootCause,
    string Resolution,
    string? RunbookRef,
    string SourceFile
);
