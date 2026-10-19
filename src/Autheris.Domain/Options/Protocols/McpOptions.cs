using System.ComponentModel.DataAnnotations;

namespace Autheris.Domain.Options;

public sealed class McpOptions
{
    public bool Enabled { get; init; } = false;
    public string EndpointPath { get; init; } = "/mcp";
    [Range(256, 128000)] public int MaxTokensPerCall { get; init; } = 4096;
    [Range(1, 10000)] public int MaxResultRows { get; init; } = 100;
    public bool RequirePiiMasking { get; init; } = true;
    public List<string> AllowedOperations { get; init; } = [];
    public bool AllowAnonymousDiscovery { get; init; } = false;
    public bool EnableDeveloperCors { get; init; } = false;
    public List<string> DeveloperCorsOrigins { get; init; } = [];

    /// <summary>PLAN-SEC-115: tool-group switches (startup only). All groups default to on.</summary>
    public McpToolGroupOptions Tools { get; init; } = new();

    /// <summary>
    /// PLAN-SEC-115: tool names switched off individually (disabled wins over an enabled group). Null and empty both mean
    /// "nothing disabled" (a JSON <c>[]</c> binds to null).
    /// </summary>
    public List<string>? DisabledTools { get; init; }
}

/// <summary>PLAN-SEC-115: the four MCP tool groups. A group off removes all its tools, resources and prompts.</summary>
public sealed class McpToolGroupOptions
{
    public bool Catalog { get; init; } = true;
    public bool Query { get; init; } = true;
    public bool Api { get; init; } = true;
    public bool Access { get; init; } = true;
}
