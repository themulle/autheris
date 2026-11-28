namespace Autheris.Application.Mcp;

/// <summary>Keys of <c>HttpContext.Items</c> entries that describe the MCP transport request.</summary>
public static class McpTransportItems
{
    /// <summary>Set to <c>true</c> while an MCP tool call runs; the HTTP response then belongs to the MCP transport, not to in-process executions.</summary>
    public const string ToolCallKey = "Autheris.Mcp.ToolCall";
}
