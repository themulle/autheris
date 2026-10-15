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
}
