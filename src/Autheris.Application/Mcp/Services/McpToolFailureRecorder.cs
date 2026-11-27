namespace Autheris.Application.Mcp.Services;

using System.Text.Json;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Mcp.Diagnostics;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;

/// <summary>
/// Best-effort record of a tool call that failed outside the guardrail's own audit points (an exception escaped the guardrail): one
/// <c>DENY</c> audit row with a fixed text and the deny verdict metric. It never throws and never changes the result of the call.
/// </summary>
public static class McpToolFailureRecorder
{
    public const string FixedDetails = "Tool execution failed before the guardrail completed (fail-closed).";

    public static async Task RecordDenyAsync(
        IAuditLogRepository? auditLog,
        ILogger? logger,
        McpSessionContext session,
        string toolName,
        string code,
        CancellationToken ct)
    {
        try
        {
            McpDiagnostics.RecordGuardrailVerdict("deny", toolName, false, false);
            if (auditLog is null)
            {
                return;
            }

            var actor = !string.IsNullOrWhiteSpace(session.UserSid) ? new Sid(session.UserSid) : new Sid(session.ServicePrincipalId);
            var tenant = TenantId.TryParse(session.TenantId, out var tid) ? tid : TenantId.Platform;
            await auditLog.RecordAuditEventAsync(new AuditLogEntry
            {
                TenantId = tenant,
                EventType = "MCP_TOOL_EXECUTION",
                ActorSid = actor,
                TargetTable = toolName,
                Decision = "DENY",
                TraceId = session.SessionId,
                DetailsJson = JsonSerializer.Serialize(new
                {
                    sessionId = session.SessionId,
                    tenantId = session.TenantId,
                    tool = toolName,
                    decision = "DENY",
                    details = FixedDetails,
                    code,
                    isMasked = false,
                    truncated = false,
                    estimatedTokens = 0,
                    timestamp = DateTimeOffset.UtcNow
                })
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger?.LogError(ex, "Failed to record the failure audit event for MCP tool '{ToolName}'.", toolName);
        }
    }
}
