namespace Autheris.Application.Mcp.Services;

/// <summary>MCP tool behaviour hints (issue #706).</summary>
public readonly record struct McpToolHints(bool ReadOnly, bool Destructive);

/// <summary>Issue #706: behaviour hints of every MCP tool.</summary>
public static class McpToolAnnotations
{
    public static bool IsClassified(string toolName) => false;

    public static McpToolHints For(string toolName, bool readOnlySession, bool sqlDmlAllowed) => new(ReadOnly: false, Destructive: true);
}
