using Autheris.Application.Mcp.Interfaces;
using Autheris.Domain.Model;

namespace Autheris.Tests.Common.Fakes;

/// <summary>
/// Configurable <see cref="IMcpQueryExecutor"/> test double recording executed calls.
/// </summary>
public sealed class StubMcpQueryExecutor(Func<McpToolDefinition, string, McpSessionContext, string>? execute = null) : IMcpQueryExecutor
{
    public StubMcpQueryExecutor(string responseJson)
        : this((_, _, _) => responseJson)
    {
    }

    public List<(McpToolDefinition Tool, string ArgumentsJson, McpSessionContext Session)> Calls { get; } = [];

    public Task<string> ExecuteOperationAsync(
        McpToolDefinition tool,
        string argumentsJson,
        McpSessionContext sessionContext,
        CancellationToken cancellationToken = default)
    {
        Calls.Add((tool, argumentsJson, sessionContext));
        return Task.FromResult(execute is not null ? execute(tool, argumentsJson, sessionContext) : """{"data":{}}""");
    }
}
