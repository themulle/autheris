namespace Autheris.Application.Mcp.Services;

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;

/// <summary>
/// Thread-safe in-memory session store for active MCP connections.
/// MCP-2: cluster state (Redis) is never awaited synchronously and never inside a lock. Writes run in the background
/// (they were already best effort); the only remote read is <see cref="GetSessionAsync"/>.
/// MCP-3: the cross-node SSE subscription of a session is disposed when the session ends.
/// </summary>
public sealed class McpSessionStore : IMcpSessionStore
{
    private readonly ConcurrentDictionary<string, McpSessionContext> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Func<string, string, Task>> _sseSenders = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IAsyncDisposable> _sseSubscriptions = new(StringComparer.Ordinal);
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
                var safeOwnerKeyForLog = ownerKey.Replace("\r", string.Empty).Replace("\n", string.Empty);
                _logger.LogWarning("MCP session limit per principal ({Max}) reached for {PrincipalId}. Rejecting session creation.", MaxSessionsPerPrincipal, safeOwnerKeyForLog);
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

        PersistInBackground(sessionId, session);

        var safeCreatedSessionId = sessionId.Replace("\r", string.Empty).Replace("\n", string.Empty);
        var safeCreatedSpn = servicePrincipalId.Replace("\r", string.Empty).Replace("\n", string.Empty);
        var safeCreatedUserSid = (userSid ?? "none").Replace("\r", string.Empty).Replace("\n", string.Empty);
        _logger.LogInformation("Created new MCP session {SessionId} for principal {PrincipalId} (UserSid: {UserSid}) in tenant {TenantId}.",
            safeCreatedSessionId, safeCreatedSpn, safeCreatedUserSid, validatedTenant.Value);

        return session;
    }

    /// <summary>Local lookup only (no blocking remote read); sessions of other nodes are found by <see cref="GetSessionAsync"/>.</summary>
    public McpSessionContext? GetSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || !_sessions.TryGetValue(sessionId, out var session))
        {
            return null;
        }

        return Touch(sessionId, session);
    }

    public async ValueTask<McpSessionContext?> GetSessionAsync(string sessionId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return null;
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            if (_clusterState == null)
            {
                return null;
            }

            try
            {
                session = await _clusterState.GetAsync<McpSessionContext>($"mcp:session:{sessionId}", ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var safeGetSessionId = sessionId.Replace("\r", string.Empty).Replace("\n", string.Empty);
                _logger.LogWarning(ex, "Failed to retrieve remote MCP session {SessionId} from cluster state.", safeGetSessionId);
            }

            if (session == null)
            {
                return null;
            }

            _sessions[sessionId] = session;
        }

        return Touch(sessionId, session);
    }

    private McpSessionContext? Touch(string sessionId, McpSessionContext session)
    {
        var now = DateTimeOffset.UtcNow;
        if (now - session.LastActiveAt > DefaultSessionTtl)
        {
            RemoveSession(sessionId);
            var safeTouchSessionId = sessionId.Replace("\r", string.Empty).Replace("\n", string.Empty);
            _logger.LogWarning("MCP session {SessionId} expired due to inactivity (TTL: {Ttl}).", safeTouchSessionId, DefaultSessionTtl);
            return null;
        }

        var updated = session with { LastActiveAt = now };
        _sessions[sessionId] = updated;
        PersistInBackground(sessionId, updated);
        return updated;
    }

    /// <summary>MCP-2: best-effort write to the cluster state without blocking the caller (and never inside a lock).</summary>
    private void PersistInBackground(string sessionId, McpSessionContext session)
    {
        if (_clusterState == null)
        {
            return;
        }

        _ = PersistAsync(sessionId, session);
    }

    private async Task PersistAsync(string sessionId, McpSessionContext session)
    {
        try
        {
            await _clusterState!.SetAsync($"mcp:session:{sessionId}", session, DefaultSessionTtl).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var safePersistSessionId = sessionId.Replace("\r", string.Empty).Replace("\n", string.Empty);
            _logger.LogWarning(ex, "Failed to persist MCP session {SessionId} in cluster state.", safePersistSessionId);
        }
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
                PersistInBackground(sessionId, updated);
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
        var removed = _sessions.TryRemove(sessionId, out _);

        // MCP-2 / MCP-3: remote cleanup (cluster entry, cross-node SSE subscription) runs in the background, so this
        // method never blocks - it is also called while _createLock is held.
        _sseSubscriptions.TryRemove(sessionId, out var subscription);
        if (_clusterState != null || subscription != null)
        {
            _ = CleanupRemoteAsync(sessionId, subscription);
        }

        if (removed)
        {
            var safeRemoveSessionId = sessionId.Replace("\r", string.Empty).Replace("\n", string.Empty);
            _logger.LogInformation("Terminated MCP session {SessionId}.", safeRemoveSessionId);
        }
        return removed;
    }

    private async Task CleanupRemoteAsync(string sessionId, IAsyncDisposable? subscription)
    {
        try
        {
            if (subscription != null)
            {
                await subscription.DisposeAsync().ConfigureAwait(false);
            }

            if (_clusterState != null)
            {
                await _clusterState.RemoveAsync($"mcp:session:{sessionId}").ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            var safeCleanupSessionId = sessionId.Replace("\r", string.Empty).Replace("\n", string.Empty);
            _logger.LogDebug(ex, "Failed to clean up cluster state of MCP session {SessionId}.", safeCleanupSessionId);
        }
    }

    internal int SseSubscriptionCount => _sseSubscriptions.Count;

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
                var subscription = _clusterState.SubscribeAsync<McpSsePayload>($"mcp:sse:{sessionId}", async payload =>
                {
                    if (_sseSenders.TryGetValue(payload.SessionId, out var localSender))
                    {
                        try
                        {
                            await localSender(payload.EventType, payload.EventData).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            var safePayloadSessionId = (payload.SessionId ?? string.Empty).Replace("\r", string.Empty).Replace("\n", string.Empty);
                            _logger.LogDebug(ex, "Failed to dispatch routed cross-node SSE event to MCP session {SessionId}.", safePayloadSessionId);
                        }
                    }
                });

                // MCP-3: keep the subscription so it is released with the session (it leaked before).
                if (_sseSubscriptions.TryGetValue(sessionId, out var previous))
                {
                    _ = previous.DisposeAsync().AsTask();
                }

                _sseSubscriptions[sessionId] = subscription;
            }
            catch (Exception ex)
            {
                var safeSubSessionId = sessionId.Replace("\r", string.Empty).Replace("\n", string.Empty);
                _logger.LogWarning(ex, "Failed to subscribe to cross-node SSE events for MCP session {SessionId}.", safeSubSessionId);
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
                var safeSendSessionId = sessionId.Replace("\r", string.Empty).Replace("\n", string.Empty);
                _logger.LogDebug(ex, "Failed to dispatch SSE event to MCP session {SessionId}.", safeSendSessionId);
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
                var safePubSessionId = sessionId.Replace("\r", string.Empty).Replace("\n", string.Empty);
                _logger.LogWarning(ex, "Failed to publish cross-node SSE event for MCP session {SessionId}.", safePubSessionId);
            }
        }

        return false;
    }
}

public sealed record McpSsePayload(string SessionId, string EventType, string EventData);
