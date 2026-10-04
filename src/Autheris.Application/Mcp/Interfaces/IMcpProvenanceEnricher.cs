namespace Autheris.Application.Mcp.Interfaces;

using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>
/// Data provenance and lineage enricher attaching audit and source metadata to AI responses (F-AI-06).
/// </summary>
public interface IMcpProvenanceEnricher
{
    /// <summary>
    /// Constructs a provenance envelope for a target table.
    /// </summary>
    Task<McpProvenanceEnvelope> CreateProvenanceAsync(TableIdentifier targetTable, CancellationToken ct = default);

    /// <summary>
    /// Attaches the provenance envelope (_provenance) into a JSON payload.
    /// </summary>
    string EnrichPayloadWithProvenance(string jsonContent, McpProvenanceEnvelope provenance);
}
