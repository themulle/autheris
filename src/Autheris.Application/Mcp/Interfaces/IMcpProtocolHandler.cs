namespace Autheris.Application.Mcp.Interfaces;

using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Model;

/// <summary>
/// Handles incoming Model Context Protocol (MCP) JSON-RPC 2.0 messages and manages SSE sessions.
/// </summary>
public interface IMcpProtocolHandler
{
    /// <summary>
    /// Creates a new MCP session for an authenticated principal.
    /// </summary>
    McpSessionContext CreateSession(
        string servicePrincipalId,
        string tenantId,
        string? userSid = null,
        System.Collections.Generic.IReadOnlyList<string>? roles = null,
        System.Collections.Generic.IReadOnlyList<string>? groupSids = null,
        string? clientIp = null);

    /// <summary>
    /// Gets an existing active session by ID.
    /// </summary>
    McpSessionContext? GetSession(string sessionId);

    /// <summary>
    /// Closes and removes an active session.
    /// </summary>
    bool RemoveSession(string sessionId);

    /// <summary>
    /// Processes an incoming JSON-RPC 2.0 payload from an AI client.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="jsonRpcPayload">The raw JSON-RPC 2.0 message.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The JSON-RPC 2.0 response payload.</returns>
    ValueTask<string> HandleMessageAsync(
        string sessionId,
        string jsonRpcPayload,
        CancellationToken cancellationToken = default);
}
