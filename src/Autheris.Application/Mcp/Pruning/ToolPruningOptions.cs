namespace Autheris.Application.Mcp.Pruning;

/// <summary>
/// F-AI-07: Configuration options for Dynamic Semantic Schema Pruning and Just-in-Time MCP Tools.
/// </summary>
public sealed record ToolPruningOptions
{
    /// <summary>Maximum number of tools injected into the agent system context (default: 8).</summary>
    public int MaxTools { get; init; } = 8;

    /// <summary>Maximum estimated tokens allocated for tool schema definitions (default: 4000).</summary>
    public int MaxToolDefinitionTokens { get; init; } = 4000;

    /// <summary>Minimum semantic relevance score (0.0 to 1.0) to qualify a tool (default: 0.25f).</summary>
    public float MinSimilarityThreshold { get; init; } = 0.25f;

    /// <summary>Always include verified Few-Shot Golden Query tools (default: true).</summary>
    public bool ForceIncludeGoldenQueries { get; init; } = true;
}
