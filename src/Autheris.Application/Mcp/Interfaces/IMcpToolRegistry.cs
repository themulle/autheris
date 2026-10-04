namespace Autheris.Application.Mcp.Interfaces;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Model;

/// <summary>
/// Registry for discovering and exposing GraphQL operations as Model Context Protocol (MCP) tools.
/// </summary>
public interface IMcpToolRegistry
{
    /// <summary>
    /// Registers or updates an MCP tool definition.
    /// </summary>
    void RegisterTool(McpToolDefinition tool);

    /// <summary>
    /// Gets all tools available for execution by AI agents.
    /// </summary>
    IReadOnlyList<McpToolDefinition> GetAvailableTools();

    /// <summary>
    /// Retrieves a specific tool definition by name.
    /// </summary>
    McpToolDefinition? FindTool(string toolName);
}
