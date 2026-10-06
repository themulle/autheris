namespace Autheris.Application.Mcp.Services;

using System;
using System.Collections.Concurrent;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;

/// <summary>
/// Thread-safe in-memory session store for active MCP connections.
/// </summary>
public sealed class McpSessionStore : IMcpSessionStore
{
    private readonly ConcurrentDictionary<string, McpSessionContext> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Func<string, string, Task>> _sseSenders = new(StringComparer.Ordinal);
    private readonly ILogger<McpSessionStore> _logger;
    private readonly Autheris.Application.State.IDistributedClusterStateProvider? _clusterState;

    public McpSessionStore(ILogger<McpSessionStore> logger, Autheris.Application.State.IDistributedClusterStateProvider? clusterState = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _clusterState = clusterState;
    }

    internal const int MaxAllowedSessions = 10000;

    // SEC M-09: A single principal can no longer exhaust the global session pool.
    internal const int MaxSessionsPerPrincipal = 20;
    private static readonly TimeSpan DefaultSessionTtl = TimeSpan.FromHours(1);
    private readonly object _createLock = new();

    public McpSessionContext CreateSession(string servicePrincipalId, string tenantId)
        => CreateSession(servicePrincipalId, tenantId, null, null, null, null);

    public McpSessionContext CreateSession(
        string servicePrincipalId,
        string tenantId,
        string? userSid = null,
        IReadOnlyList<string>? roles = null,
        IReadOnlyList<string>? groupSids = null,
        string? clientIp = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(servicePrincipalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        // Strict TenantId domain validation prevents injection into downstream logs/JSON (N-9)
        var validatedTenant = new Autheris.Domain.Common.TenantId(tenantId);

        var now = DateTimeOffset.UtcNow;
        var ownerKey = GetOwnerKey(servicePrincipalId, userSid);
        McpSessionContext session;
        string sessionId;

        lock (_createLock)
        {
            // Cleanup expired sessions if store is getting large (L-2)
            if (_sessions.Count >= MaxAllowedSessions)
            {
                PurgeExpired(now);

                if (_sessions.Count >= MaxAllowedSessions)
                {
                    _logger.LogWarning("Maximum active MCP sessions limit ({Max}) reached. Rejecting session creation.", MaxAllowedSessions);
                    throw new McpSessionLimitExceededException($"Maximum active MCP sessions limit ({MaxAllowedSessions}) reached. Please retry later.");
                }
            }

            // SEC M-09: Per-principal limit (expired sessions of this principal are purged first).
            if (CountActiveSessionsForOwner(ownerKey, now) >= MaxSessionsPerPrincipal)
            {
                _logger.LogWarning("MCP session limit per principal ({Max}) reached for {PrincipalId}. Rejecting session creation.", MaxSessionsPerPrincipal, ownerKey);
                throw new McpSessionLimitExceededException($"Maximum active MCP sessions per principal ({MaxSessionsPerPrincipal}) reached. Close unused sessions or retry later.");
            }

            sessionId = Guid.NewGuid().ToString("N");
            session = new McpSessionContext(
                sessionId,
                servicePrincipalId,
                validatedTenant.Value,
                now,
                now,
                userSid,
                roles,
                groupSids,
                clientIp);
            _sessions[sessionId] = session;
        }

        if (_clusterState != null)
        {
            try
            {
                _clusterState.SetAsync($"mcp:session:{sessionId}", session, DefaultSessionTtl).AsTask().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to persist MCP session {SessionId} in cluster state.", sessionId);
            }
        }

        _logger.LogInformation("Created new MCP session {SessionId} for principal {PrincipalId} (UserSid: {UserSid}) in tenant {TenantId}.",
            sessionId, servicePrincipalId, userSid ?? "none", validatedTenant.Value);

        return session;
    }

    public McpSessionContext? GetSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return null;
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            if (_clusterState != null)
            {
                try
                {
                    var remote = _clusterState.GetAsync<McpSessionContext>($"mcp:session:{sessionId}").AsTask().GetAwaiter().GetResult();
                    if (remote != null)
                    {
                        session = remote;
                        _sessions[sessionId] = session;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to retrieve remote MCP session {SessionId} from cluster state.", sessionId);
                }
            }

            if (session == null) return null;
        }

        var now = DateTimeOffset.UtcNow;
        if (now - session.LastActiveAt > DefaultSessionTtl)
        {
            RemoveSession(sessionId);
            _logger.LogWarning("MCP session {SessionId} expired due to inactivity (TTL: {Ttl}).", sessionId, DefaultSessionTtl);
            return null;
        }

        var updated = session with { LastActiveAt = now };
        _sessions[sessionId] = updated;
        if (_clusterState != null)
        {
            try
            {
                _clusterState.SetAsync($"mcp:session:{sessionId}", updated, DefaultSessionTtl).AsTask().GetAwaiter().GetResult();
            }
            catch { /* non-critical */ }
        }
        return updated;
    }

    public McpSessionContext? RefreshPrincipalContext(string sessionId, IReadOnlyList<string> roles, IReadOnlyList<string> groupSids, string? clientIp = null)
    {
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(groupSids);

        if (string.IsNullOrWhiteSpace(sessionId)) return null;

        while (_sessions.TryGetValue(sessionId, out var current))
        {
            var updated = current with { Roles = roles, GroupSids = groupSids, ClientIp = string.IsNullOrWhiteSpace(clientIp) ? current.ClientIp : clientIp };
            if (_sessions.TryUpdate(sessionId, updated, current))
            {
                if (_clusterState != null)
                {
                    try
                    {
                        _clusterState.SetAsync($"mcp:session:{sessionId}", updated, DefaultSessionTtl).AsTask().GetAwaiter().GetResult();
                    }
                    catch { /* non-critical */ }
                }
                return updated;
            }
        }

        return null;
    }

    internal int Count => _sessions.Count;

    private static string GetOwnerKey(string servicePrincipalId, string? userSid)
        => string.IsNullOrWhiteSpace(userSid) ? $"sp:{servicePrincipalId}" : $"user:{userSid}";

    private int CountActiveSessionsForOwner(string ownerKey, DateTimeOffset now)
    {
        var count = 0;
        foreach (var kvp in _sessions)
        {
            var s = kvp.Value;
            if (!string.Equals(GetOwnerKey(s.ServicePrincipalId, s.UserSid), ownerKey, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (now - s.LastActiveAt > DefaultSessionTtl)
            {
                RemoveSession(kvp.Key);
                continue;
            }

            count++;
        }

        return count;
    }

    private void PurgeExpired(DateTimeOffset now)
    {
        foreach (var kvp in _sessions)
        {
            if (now - kvp.Value.LastActiveAt > DefaultSessionTtl)
            {
                RemoveSession(kvp.Key);
            }
        }
    }

    public bool RemoveSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return false;
        _sseSenders.TryRemove(sessionId, out _);
        if (_clusterState != null)
        {
            try
            {
                _clusterState.RemoveAsync($"mcp:session:{sessionId}").AsTask().GetAwaiter().GetResult();
            }
            catch { /* non-critical */ }
        }
        var removed = _sessions.TryRemove(sessionId, out _);
        if (removed)
        {
            _logger.LogInformation("Terminated MCP session {SessionId}.", sessionId);
        }
        return removed;
    }

    public void RegisterSseSender(string sessionId, Func<string, string, Task> sendEventAsync)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(sendEventAsync);
        _sseSenders[sessionId] = sendEventAsync;

        // K-K14: Subscribe to cross-node SSE broadcast events for this session
        if (_clusterState != null)
        {
            try
            {
                _clusterState.SubscribeAsync<McpSsePayload>($"mcp:sse:{sessionId}", async payload =>
                {
                    if (_sseSenders.TryGetValue(payload.SessionId, out var localSender))
                    {
                        try
                        {
                            await localSender(payload.EventType, payload.EventData).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Failed to dispatch routed cross-node SSE event to MCP session {SessionId}.", payload.SessionId);
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to subscribe to cross-node SSE events for MCP session {SessionId}.", sessionId);
            }
        }
    }

    public async Task<bool> SendEventAsync(string sessionId, string eventType, string eventData)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return false;
        if (_sseSenders.TryGetValue(sessionId, out var sender))
        {
            try
            {
                await sender(eventType, eventData).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to dispatch SSE event to MCP session {SessionId}.", sessionId);
                _sseSenders.TryRemove(sessionId, out _);
                return false;
            }
        }

        // K-K14: Cross-node SSE routing
        if (_clusterState != null)
        {
            try
            {
                await _clusterState.PublishEventAsync($"mcp:sse:{sessionId}", new McpSsePayload(sessionId, eventType, eventData)).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to publish cross-node SSE event for MCP session {SessionId}.", sessionId);
            }
        }

        return false;
    }
}

public sealed record McpSsePayload(string SessionId, string EventType, string EventData);
