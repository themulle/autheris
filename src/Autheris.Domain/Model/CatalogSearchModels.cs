namespace Autheris.Domain.Model;

using System;
using System.Collections.Generic;
using Autheris.Domain.Common;

/// <summary>
/// Search mode for catalog and schema search.
/// </summary>
public enum CatalogSearchMode
{
    /// <summary>Combines BM25 and vector search via Reciprocal Rank Fusion (default).</summary>
    Hybrid = 0,

    /// <summary>Pure semantic vector search (ideal for natural-language intents).</summary>
    SemanticVector = 1,

    /// <summary>Pure deterministic keyword search via BM25 (ideal for table abbreviations).</summary>
    KeywordBm25 = 2
}

/// <summary>
/// Relationship type between two tables in the schema graph.
/// </summary>
public enum TableRelationshipType
{
    ForeignKey = 0,
    PrimaryKeyJoin = 1,
    SemanticInferred = 2
}

/// <summary>
/// Describes a join path or relationship between two tables.
/// </summary>
public sealed record TableRelationship(
    TableIdentifier FromTable,
    string FromColumn,
    TableIdentifier ToTable,
    string ToColumn,
    TableRelationshipType Type,
    string? Description = null,
    bool Incoming = false);

/// <summary>
/// Parameters for a catalog search query issued by an agent or API client.
/// </summary>
public sealed record CatalogSearchQuery(
    string QueryText,
    string? DomainFilter = null,
    int Limit = 5,
    CatalogSearchMode Mode = CatalogSearchMode.Hybrid,
    float MinRelevanceScore = 0.01f,
    bool ExpandRelations = true,
    int MaxRelationsPerHit = 3,
    string? Language = null,
    DiscoveryAccessState? AccessStatus = null);

/// <summary>
/// Represents a single scored catalog search hit with schema and join details.
/// </summary>
public sealed record CatalogSearchHit(
    TableIdentifier TableIdentifier,
    string DisplayName,
    string? Description,
    string? Domain,
    string Sensitivity,
    double CombinedScore,
    double? Bm25Score,
    double? VectorScore,
    IReadOnlyList<string> MatchedTerms,
    IReadOnlyList<string> RelevantColumns,
    IReadOnlyList<TableRelationship> RelatedJoinPaths,
    string? SuggestedGraphQlField,
    string Kind = "table",
    string? DatasetId = null,
    string? EffectiveMode = null,
    string? Language = null,
    string? AccessStatus = null);

/// <summary>
/// Typed key uniquely identifying an entity in the catalog index.
/// </summary>
public readonly record struct CatalogEntryKey(string Kind, string Id)
{
    public const string KindTable = "table";
    public const string KindProcedure = "procedure";

    public static CatalogEntryKey ForTable(TableIdentifier table) => new(KindTable, table.ToString());
    public static CatalogEntryKey ForTable(Guid tableId) => new(KindTable, tableId.ToString("D"));
    public override string ToString() => $"{Kind}:{Id}";
}
