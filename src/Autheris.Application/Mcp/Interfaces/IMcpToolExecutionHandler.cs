namespace Autheris.Application.Mcp.Interfaces;

using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Model;

/// <summary>
/// Pipeline handler for executing MCP tools without bloating AiDataGuardrailService or GatewayMcpQueryExecutor.
/// </summary>
public interface IMcpToolExecutionHandler
{
    bool CanHandle(string toolName);

    Task<string> ExecuteToolAsync(
        McpToolDefinition tool,
        string argumentsJson,
        McpSessionContext sessionContext,
        CancellationToken ct = default);
}
