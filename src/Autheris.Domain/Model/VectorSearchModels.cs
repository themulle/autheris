namespace Autheris.Domain.Model;

using System;
using System.Collections.Generic;
using Autheris.Domain.Common;
using Autheris.Domain.Kernel;

public enum VectorDistanceMetric
{
    Cosine = 1,
    Euclidean = 2,
    DotProduct = 3
}

public sealed record VectorSearchRequest(
    TableIdentifier TargetCollection,
    IReadOnlyList<float>? QueryVector = null,
    string? RawQueryText = null,
    int TopK = 10,
    float MinSimilarityScore = 0.70f,
    VectorDistanceMetric Metric = VectorDistanceMetric.Cosine,
    IReadOnlyDictionary<string, object?>? MetadataFilters = null,
    IReadOnlyList<string>? ProjectedPayloadFields = null
);

public sealed record VectorDocumentChunk(
    string ChunkId,
    string DocumentId,
    int ChunkIndex,
    string ContentText,
    float SimilarityScore,
    TenantId TenantId,
    IReadOnlyDictionary<string, object?> Metadata,
    IReadOnlyList<float>? Vector = null
);

public sealed record GovernedVectorResult(
    TableIdentifier Collection,
    IReadOnlyList<VectorDocumentChunk> Chunks,
    TableAccessDecision AccessDecision,
    ExecutionMetrics Metrics
);
