namespace Autheris.Application.Mcp.Interfaces;

using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Model;

/// <summary>
/// Execution bridge executing GraphQL operations or data queries on behalf of an MCP tool call.
/// </summary>
public interface IMcpQueryExecutor
{
    /// <summary>
    /// Executes the tool's targeted GraphQL operation with the given arguments and session context.
    /// </summary>
    Task<string> ExecuteOperationAsync(
        McpToolDefinition tool,
        string argumentsJson,
        McpSessionContext sessionContext,
        CancellationToken cancellationToken = default);
}
