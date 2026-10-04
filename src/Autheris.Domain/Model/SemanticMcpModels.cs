namespace Autheris.Domain.Model;

using System;
using System.Collections.Generic;

/// <summary>
/// A semantic resource exposed to AI agents via MCP (e.g. glossary://, dbt://).
/// </summary>
public sealed record McpResourceItem(
    string Uri,
    string Name,
    string Description,
    string MimeType,
    string Text
);

/// <summary>
/// Detailed result of an AST pre-flight query simulation (F-AI-04).
/// </summary>
public sealed record PreFlightQuerySimulationResult(
    bool IsAllowed,
    int EstimatedRowCount,
    long EstimatedDbBytesScan,
    int EstimatedResponseTokens,
    IReadOnlyList<string> AppliedMaskingRules,
    IReadOnlyList<string> ActiveRlsFilters,
    IReadOnlyList<string> OptimizationRecommendations,
    string? BlockReason = null
);

/// <summary>
/// Data provenance and lineage metadata footnote envelope attached to MCP responses (F-AI-06).
/// </summary>
public sealed record McpProvenanceEnvelope(
    string DbtModel,
    string GitCommit,
    string OpenMetadataUrn,
    DateTimeOffset DataFreshness,
    IReadOnlyList<string> ActivePolicies
);
