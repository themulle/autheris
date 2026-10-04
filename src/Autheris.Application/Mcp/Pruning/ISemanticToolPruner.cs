namespace Autheris.Application.Mcp.Pruning;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Model;

/// <summary>
/// F-AI-07: Service interface for dynamic runtime schema pruning.
/// Filters large supergraph tool libraries to the most relevant tools for an agent prompt,
/// slashing prompt token consumption by up to 80%.
/// </summary>
public interface ISemanticToolPruner
{
    ValueTask<IReadOnlyList<McpToolDefinition>> PruneToolsAsync(
        string userPrompt,
        IReadOnlyList<McpToolDefinition> availableTools,
        ToolPruningOptions? options = null,
        CancellationToken ct = default);
}
