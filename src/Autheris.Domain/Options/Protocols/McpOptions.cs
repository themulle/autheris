using System.ComponentModel.DataAnnotations;

namespace Autheris.Domain.Options;

/// <summary>Model Context Protocol (MCP) server options (<c>Gateway:Mcp</c>).</summary>
public sealed class McpOptions
{
    /// <summary>Enables the MCP server endpoint.</summary>
    public bool Enabled { get; init; } = false;

    /// <summary>HTTP route path where the MCP endpoint is mapped.</summary>
    public string EndpointPath { get; init; } = "/mcp";

    [Range(256, 128000)] public int MaxTokensPerCall { get; init; } = 4096;
    [Range(1, 10000)] public int MaxResultRows { get; init; } = 100;

    /// <summary>Optional allowlist of MCP operation names permitted on this endpoint.</summary>
    public List<string> AllowedOperations { get; init; } = [];

    /// <summary>Enables permissive developer CORS headers on the MCP endpoint (non-Production only).</summary>
    public bool EnableDeveloperCors { get; init; } = false;

    /// <summary>Allowed origins when <see cref="EnableDeveloperCors"/> is enabled.</summary>
    public List<string> DeveloperCorsOrigins { get; init; } = [];

    /// <summary>tool-group switches (startup only). All groups default to on.</summary>
    public McpToolGroupOptions Tools { get; init; } = new();

    /// <summary>
    /// tool names switched off individually (disabled wins over an enabled group). Null and empty both mean
    /// "nothing disabled" (a JSON <c>[]</c> binds to null).
    /// </summary>
    public List<string>? DisabledTools { get; init; }

    private static readonly string[] DefaultAgentScopes = ["Agent.Read"];

    /// <summary>
    /// Token scopes that mark an autonomous agent. A GraphQL or WebSQL response to a token carrying one of these scopes (or a scope mapped to
    /// the AutonomousAgents tier in <c>Gateway:ResourceGroups:ScopeTiers</c>) passes the same output guardrails as MCP results. When not
    /// configured, <c>Agent.Read</c> applies; a configured list replaces that default. Nullable on purpose: the configuration binder appends to
    /// pre-filled lists, so a non-empty default could not be replaced.
    /// </summary>
    public List<string>? AgentScopes { get; init; }

    /// <summary>Configured <see cref="AgentScopes"/> or the default list (Agent.Read); an empty list also yields the default (fail closed).</summary>
    public IReadOnlyList<string> GetEffectiveAgentScopes() => AgentScopes is { Count: > 0 } ? AgentScopes : DefaultAgentScopes;
}

/// <summary>the four MCP tool groups. A group off removes all its tools, resources and prompts.</summary>
public sealed class McpToolGroupOptions
{
    /// <summary>Enables catalog discovery MCP tools.</summary>
    public bool Catalog { get; init; } = true;

    /// <summary>Enables governed query execution MCP tools.</summary>
    public bool Query { get; init; } = true;

    /// <summary>Enables API documentation and schema inspection MCP tools.</summary>
    public bool Api { get; init; } = true;

    /// <summary>Enables access-request and consent MCP tools.</summary>
    public bool Access { get; init; } = true;
}
