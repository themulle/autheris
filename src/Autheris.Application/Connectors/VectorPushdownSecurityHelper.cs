namespace Autheris.Application.Connectors;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>
/// Security Expert Guardrails for Vector Pushdown across pgvector, Qdrant, and Milvus.
/// Enforces mandatory tenant filtering, parameterization, and post-execution RLS evaluation.
/// </summary>
public static class VectorPushdownSecurityHelper
{
    public static string BuildPgVectorQuery(
        VectorSearchRequest request,
        TenantId tenantId,
        string? rlsPredicateSql,
        out Dictionary<string, object> parameters)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(tenantId.Value))
        {
            throw new SecurityException("INV-VEC-01: Vector search rejected. TenantId cannot be null or empty.");
        }

        parameters = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["@tenant_id"] = tenantId.Value,
            ["@limit"] = Math.Clamp(request.TopK, 1, 200),
            ["@query_vector"] = request.QueryVector ?? (object)Array.Empty<float>()
        };

        var operatorSql = request.Metric switch
        {
            VectorDistanceMetric.Cosine => "<=>",
            VectorDistanceMetric.Euclidean => "<->",
            VectorDistanceMetric.DotProduct => "<#>",
            _ => "<=>"
        };

        var whereClause = "WHERE tenant_id = @tenant_id";
        if (!string.IsNullOrWhiteSpace(rlsPredicateSql))
        {
            whereClause += $" AND ({rlsPredicateSql})";
        }

        var collectionName = request.TargetCollection.TableName;
        // Verify identifier safety against SQL injection
        if (!collectionName.All(c => char.IsLetterOrDigit(c) || c == '_'))
        {
            throw new SecurityException($"Collection name '{collectionName}' contains invalid characters.");
        }

        return $"SELECT chunk_id, document_id, chunk_index, content_text, (embedding {operatorSql} @query_vector) AS distance " +
               $"FROM {collectionName} {whereClause} " +
               $"ORDER BY embedding {operatorSql} @query_vector ASC LIMIT @limit;";
    }

    public static Dictionary<string, object> BuildQdrantFilter(
        VectorSearchRequest request,
        TenantId tenantId)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(tenantId.Value))
        {
            throw new SecurityException("INV-VEC-01: Qdrant vector filter rejected. Missing TenantId.");
        }

        var mustConditions = new List<Dictionary<string, object>>
        {
            new()
            {
                ["key"] = "tenant_id",
                ["match"] = new Dictionary<string, object> { ["value"] = tenantId.Value }
            }
        };

        if (request.MetadataFilters != null)
        {
            foreach (var (k, v) in request.MetadataFilters)
            {
                if (string.Equals(k, "tenant_id", StringComparison.OrdinalIgnoreCase))
                {
                    continue; // Do not allow caller to overwrite tenant_id
                }

                if (!k.All(c => char.IsLetterOrDigit(c) || c == '_'))
                {
                    continue; // Skip invalid or malicious identifier keys
                }

                if (v != null)
                {
                    mustConditions.Add(new Dictionary<string, object>
                    {
                        ["key"] = k,
                        ["match"] = new Dictionary<string, object> { ["value"] = v }
                    });
                }
            }
        }

        return new Dictionary<string, object>
        {
            ["must"] = mustConditions
        };
    }

    public static string BuildMilvusFilter(
        VectorSearchRequest request,
        TenantId tenantId)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(tenantId.Value))
        {
            throw new SecurityException("INV-VEC-01: Milvus vector filter rejected. Missing TenantId.");
        }

        var expr = $"tenant_id == \"{tenantId.Value.Replace("\"", "\\\"")}\"";

        if (request.MetadataFilters != null)
        {
            foreach (var (k, v) in request.MetadataFilters)
            {
                if (string.Equals(k, "tenant_id", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!k.All(c => char.IsLetterOrDigit(c) || c == '_'))
                {
                    continue; // Prevent Milvus Boolean Expression injection via malicious keys
                }

                if (v is string s)
                {
                    expr += $" && {k} == \"{s.Replace("\"", "\\\"")}\"";
                }
                else if (v is int or long or double or float or bool)
                {
                    expr += $" && {k} == {v.ToString()?.ToLowerInvariant()}";
                }
            }
        }

        return expr;
    }

    public static IReadOnlyList<VectorDocumentChunk> FilterChunksByRls(
        IReadOnlyList<VectorDocumentChunk> rawChunks,
        TenantId expectedTenantId,
        Func<VectorDocumentChunk, bool>? customPredicate = null)
    {
        ArgumentNullException.ThrowIfNull(rawChunks);
        if (string.IsNullOrWhiteSpace(expectedTenantId.Value))
        {
            throw new ArgumentException("Expected tenant ID cannot be empty.", nameof(expectedTenantId));
        }

        var filtered = new List<VectorDocumentChunk>(rawChunks.Count);
        foreach (var chunk in rawChunks)
        {
            // Zero-Trust Invariant: Chunk tenant must strictly match expected tenant
            if (chunk.TenantId != expectedTenantId)
            {
                continue; // Drop silently or audit as violation attempt
            }

            if (customPredicate != null && !customPredicate(chunk))
            {
                continue;
            }

            filtered.Add(chunk);
        }

        return filtered;
    }
}
