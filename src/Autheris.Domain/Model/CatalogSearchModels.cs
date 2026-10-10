namespace Autheris.Domain.Model;

using System;
using System.Collections.Generic;
using Autheris.Domain.Common;

/// <summary>
/// Suchmodus für die Katalog- und Schemasuche.
/// </summary>
public enum CatalogSearchMode
{
    /// <summary>Kombiniert BM25 und Vektorsuche via Reciprocal Rank Fusion (Standard).</summary>
    Hybrid = 0,

    /// <summary>Reine semantische Vektorsuche (ideal für natürlichsprachige Intentionen).</summary>
    SemanticVector = 1,

    /// <summary>Reine deterministische Keyword-Suche via BM25 (ideal für Tabellenkürzel).</summary>
    KeywordBm25 = 2
}

/// <summary>
/// Typ der Relation zwischen zwei Tabellen im Schema-Graph.
/// </summary>
public enum TableRelationshipType
{
    ForeignKey = 0,
    PrimaryKeyJoin = 1,
    SemanticInferred = 2
}

/// <summary>
/// Beschreibt einen Join-Pfad oder eine Relation zwischen zwei Tabellen.
/// </summary>
public sealed record TableRelationship(
    TableIdentifier FromTable,
    string FromColumn,
    TableIdentifier ToTable,
    string ToColumn,
    TableRelationshipType Type,
    string? Description = null);

/// <summary>
/// Parameter einer Katalog-Suchanfrage durch einen Agenten oder API-Client.
/// </summary>
public sealed record CatalogSearchQuery(
    string QueryText,
    string? DomainFilter = null,
    int Limit = 5,
    CatalogSearchMode Mode = CatalogSearchMode.Hybrid,
    float MinRelevanceScore = 0.01f,
    bool ExpandRelations = true,
    int MaxRelationsPerHit = 3);

/// <summary>
/// Repräsentiert einen einzelnen bewerteten Treffer der Katalogsuche mit Schema- und Join-Details.
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
    string? SuggestedGraphQlField);

/// <summary>
/// Strukturiertes Dokument für die Indexierung einer Tabelle im Vektor- und Keyword-Index.
/// </summary>
public sealed record TableSearchDocument(
    TableIdentifier Identifier,
    string NormalizedText,
    IReadOnlyList<string> Tokens,
    IReadOnlyList<string> ColumnNames,
    IReadOnlyList<string> PrimaryKeys,
    IReadOnlyList<TableRelationship> ForeignKeys,
    string Sensitivity,
    ReadOnlyMemory<float> NormalizedVector);
