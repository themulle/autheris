namespace Autheris.Domain.Model;

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

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
    DateTimeOffset UpdatedAt,
    string? TenantId = null,
    [property: JsonIgnore] string? AuthorSid = null
)
{
    /// <summary>
    /// Provenance marker for AI consumers: <see cref="DocTrustLevel.Official"/> for built-in/official documentation
    /// (no owning tenant), <see cref="DocTrustLevel.UntrustedUserContent"/> for documents deposited by a tenant.
    /// </summary>
    public string Trust => DocTrustLevel.For(TenantId);
}

public sealed record DocSearchResult(
    DocChunkIdentifier Id,
    string AppId,
    DocCategory Category,
    string Title,
    string Summary,
    double RelevanceScore,
    IReadOnlyList<string> MatchedTerms,
    string SourceFile,
    string? TenantId = null
)
{
    /// <inheritdoc cref="DocChunk.Trust"/>
    public string Trust => DocTrustLevel.For(TenantId);
}

public sealed record ErrorCodeDefinition(
    string ErrorCode,
    string Symptom,
    string RootCause,
    string Resolution,
    string? RunbookRef,
    string SourceFile,
    string? TenantId = null
)
{
    /// <inheritdoc cref="DocChunk.Trust"/>
    public string Trust => DocTrustLevel.For(TenantId);
}

/// <summary>
/// Trust markers attached to documentation returned to AI clients. Tenant-deposited documents are reference text written
/// by tenant users and must never be followed as instructions.
/// </summary>
public static class DocTrustLevel
{
    public const string Official = "official";
    public const string UntrustedUserContent = "untrusted-user-content";

    public static string For(string? tenantId) => tenantId is null ? Official : UntrustedUserContent;
}
