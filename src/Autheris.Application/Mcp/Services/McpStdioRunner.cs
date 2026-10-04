namespace Autheris.Application.Mcp.Services;

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Mcp.Interfaces;
using Microsoft.Extensions.Logging;

/// <summary>
/// Model Context Protocol (MCP) Stdio Transport runner for local developer CLIs (Claude Code, Cursor, Windsurf).
/// Reads newline-delimited JSON-RPC 2.0 requests from standard input and streams responses to standard output.
/// </summary>
public sealed class McpStdioRunner(
    IMcpProtocolHandler protocolHandler,
    ILogger<McpStdioRunner>? logger = null) : IMcpStdioRunner
{
    private readonly IMcpProtocolHandler _protocolHandler = protocolHandler ?? throw new ArgumentNullException(nameof(protocolHandler));
    private readonly ILogger<McpStdioRunner>? _logger = logger;

    public async Task RunAsync(
        TextReader input,
        TextWriter output,
        string servicePrincipalId = "cli-developer",
        string tenantId = "legacy-single-tenant",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        var session = _protocolHandler.CreateSession(servicePrincipalId, tenantId);
        _logger?.LogInformation("Started MCP Stdio session '{SessionId}' for principal '{Principal}'.", session.SessionId, servicePrincipalId);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line == null)
                {
                    // End of stream / stdin closed by host client
                    break;
                }

                var trimmed = line.Trim();
                if (string.IsNullOrWhiteSpace(trimmed))
                {
                    continue;
                }

                try
                {
                    var responseJson = await _protocolHandler.HandleMessageAsync(session.SessionId, trimmed, cancellationToken).ConfigureAwait(false);
                    await output.WriteLineAsync(responseJson.AsMemory(), cancellationToken).ConfigureAwait(false);
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Error processing JSON-RPC stdio message: {Message}", ex.Message);
                    var errorResponse = $"{{\"jsonrpc\":\"2.0\",\"error\":{{\"code\":-32603,\"message\":{System.Text.Json.JsonSerializer.Serialize(ex.Message)}}},\"id\":null}}";
                    await output.WriteLineAsync(errorResponse.AsMemory(), cancellationToken).ConfigureAwait(false);
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _protocolHandler.RemoveSession(session.SessionId);
            _logger?.LogInformation("Terminated MCP Stdio session '{SessionId}'.", session.SessionId);
        }
    }
}
