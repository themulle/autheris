namespace Autheris.Api.Hosting.Warmup;

using System.Text.Json;
using Autheris.Api.Mcp;
using Autheris.Application.Mcp.Interfaces;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

/// <summary>
/// PLAN-OPS-150 WP-C (best effort): serialises a <see cref="ListToolsResult"/> built from <see cref="IMcpToolRegistry"/> with the MCP
/// SDK's JSON options so the first <c>tools/list</c> pays no reflection cost. Depends on the registry only: no principal, scope,
/// handler or tool invocation, and the output is discarded and never logged (SEC-OPS150-11).
/// </summary>
public sealed class McpSerializationWarmup(IMcpToolRegistry toolRegistry) : IStartupWarmup
{
    public string Name => "McpSerialization";

    public WarmupKind Kind => WarmupKind.BestEffort;

    public Task RunAsync(CancellationToken cancellationToken)
    {
        var tools = toolRegistry.GetAvailableTools()
            .Select(t => new Tool { Name = t.Name, Description = t.Description, InputSchema = GatewayMcpServer.Schema(t.InputJsonSchema) })
            .ToList();
        if (tools.Count == 0)
        {
            // An empty registry would not touch the tool converters; a constant sample keeps the warm-up meaningful.
            tools.Add(new Tool { Name = "warmup", Description = "warmup", InputSchema = GatewayMcpServer.Schema(null) });
        }

        cancellationToken.ThrowIfCancellationRequested();
        _ = JsonSerializer.SerializeToUtf8Bytes(new ListToolsResult { Tools = tools }, McpJsonUtilities.DefaultOptions);
        return Task.CompletedTask;
    }
}
