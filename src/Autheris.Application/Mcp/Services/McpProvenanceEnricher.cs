namespace Autheris.Application.Mcp.Services;

using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;

/// <summary>
/// Data provenance and lineage enricher attaching audit and source metadata to AI responses (F-AI-06).
/// </summary>
public sealed class McpProvenanceEnricher(ILogger<McpProvenanceEnricher> logger) : IMcpProvenanceEnricher
{
    private readonly ILogger<McpProvenanceEnricher> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public Task<McpProvenanceEnvelope> CreateProvenanceAsync(
        TableIdentifier targetTable,
        CancellationToken ct = default)
    {
        var envelope = new McpProvenanceEnvelope(
            DbtModel: $"models/marts/{targetTable.Domain}/{targetTable.TableName}.sql",
            GitCommit: "HEAD",
            OpenMetadataUrn: $"urn:table:corp_dw.{targetTable.Domain}.{targetTable.TableName}",
            DataFreshness: DateTimeOffset.UtcNow,
            ActivePolicies: ["RLS_TENANT_ISOLATION", "MASK_SENSITIVE_PII"]
        );

        return Task.FromResult(envelope);
    }

    public string EnrichPayloadWithProvenance(string jsonContent, McpProvenanceEnvelope provenance)
    {
        if (string.IsNullOrWhiteSpace(jsonContent))
        {
            return JsonSerializer.Serialize(new { _provenance = provenance });
        }

        try
        {
            var node = JsonNode.Parse(jsonContent);
            if (node is JsonObject obj)
            {
                obj["_provenance"] = JsonSerializer.SerializeToNode(provenance);
                return obj.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
            }

            var wrapped = new JsonObject
            {
                ["data"] = node,
                ["_provenance"] = JsonSerializer.SerializeToNode(provenance)
            };
            return wrapped.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse JSON content when enriching with provenance footnote.");
            return jsonContent;
        }
    }
}
