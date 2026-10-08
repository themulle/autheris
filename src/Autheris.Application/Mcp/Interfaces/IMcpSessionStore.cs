namespace Autheris.Application.Mcp.Interfaces;

using Autheris.Domain.Model;

/// <summary>
/// Thread-safe singleton store managing active MCP sessions across scoped HTTP requests.
/// </summary>
public interface IMcpSessionStore
{
    /// <summary>
    /// Creates and persists a new session.
    /// </summary>
    McpSessionContext CreateSession(
        string servicePrincipalId,
        string tenantId,
        string? userSid = null,
        System.Collections.Generic.IReadOnlyList<string>? roles = null,
        System.Collections.Generic.IReadOnlyList<string>? groupSids = null,
        string? clientIp = null,
        bool isReadOnly = false,
        System.Collections.Generic.IReadOnlyDictionary<string, string>? additionalClaims = null);

    /// <summary>
    /// Retrieves an active session of this node by ID (no remote lookup).
    /// </summary>
    McpSessionContext? GetSession(string sessionId);

    /// <summary>
    /// MCP-2: retrieves an active session by ID, including sessions created on another cluster node (awaited remote read).
    /// </summary>
    System.Threading.Tasks.ValueTask<McpSessionContext?> GetSessionAsync(string sessionId, System.Threading.CancellationToken ct = default);

    /// <summary>
    /// Removes and terminates an active session.
    /// </summary>
    bool RemoveSession(string sessionId);

    /// <summary>
    /// Registers an active SSE event sender callback for the specified session.
    /// </summary>
    void RegisterSseSender(string sessionId, System.Func<string, string, System.Threading.Tasks.Task> sendEventAsync);

    /// <summary>
    /// Sends an SSE event to the client stream of an active session.
    /// </summary>
    System.Threading.Tasks.Task<bool> SendEventAsync(string sessionId, string eventType, string eventData);

    /// <summary>
    /// SEC H-16: Replaces the authorization attributes (roles, group SIDs) of a session with those of the
    /// current request principal, so that tool calls never run with stale or foreign privileges.
    /// Returns the updated session or null if the session does not exist (any more).
    /// </summary>
    McpSessionContext? RefreshPrincipalContext(
        string sessionId,
        System.Collections.Generic.IReadOnlyList<string> roles,
        System.Collections.Generic.IReadOnlyList<string> groupSids,
        string? clientIp = null);
}

/// <summary>
/// SEC M-09: Raised when the global or per-principal MCP session limit is reached. Endpoints map it to HTTP 429.
/// </summary>
public sealed class McpSessionLimitExceededException : System.InvalidOperationException
{
    public McpSessionLimitExceededException()
    {
    }

    public McpSessionLimitExceededException(string message)
        : base(message)
    {
    }

    public McpSessionLimitExceededException(string message, System.Exception innerException)
        : base(message, innerException)
    {
    }
}

