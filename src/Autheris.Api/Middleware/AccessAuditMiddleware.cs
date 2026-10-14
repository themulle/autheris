using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Diagnostics;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Autheris.Api.Middleware;

/// <summary>
/// Central access audit middleware providing comprehensive, fail-closed audit by default (PLAN-AUDIT-02-LUECKENLOSES-ZUGRIFFS-AUDIT §3.1).
/// Guarantees exactly one audit completion log entry per request unless explicitly exempted.
/// </summary>
public sealed class AccessAuditMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IOptions<GatewayOptions> _options;
    private readonly ILogger<AccessAuditMiddleware> _logger;
    private readonly IAuthFailureAggregator? _authFailureAggregator;
    private const int MaxTargetLength = 256;
    private const int UnroutedLogSampleInterval = 100;
    private static long _unroutedLogSequence;

    public AccessAuditMiddleware(
        RequestDelegate next,
        IOptions<GatewayOptions> options,
        ILogger<AccessAuditMiddleware> logger,
        IAuthFailureAggregator? authFailureAggregator = null)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _authFailureAggregator = authFailureAggregator;
    }

    public async Task InvokeAsync(HttpContext context, IAuditLogRepository auditRepository, AuditContext auditContext)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(auditRepository);
        ArgumentNullException.ThrowIfNull(auditContext);

        context.Features.Set(auditContext);

        var path = context.Request.Path.Value ?? "/";
        auditContext.Channel = ResolveChannel(path, context);
        auditContext.SourceIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        auditContext.TraceId = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;

        var exceptionOccurred = false;
        try
        {
            await _next(context).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            exceptionOccurred = true;
            if (!auditContext.IsHandled)
            {
                auditContext.Error("CLIENT_ABORTED", AuditEventTypes.QueryExecutionError);
            }
            throw;
        }
        catch (Exception ex)
        {
            exceptionOccurred = true;
            if (!auditContext.IsHandled)
            {
                auditContext.Error(ex.Message, AuditEventTypes.QueryExecutionError);
            }
            throw;
        }
        finally
        {
            await FinalizeAuditAsync(context, auditRepository, auditContext, exceptionOccurred).ConfigureAwait(false);
        }
    }

    private async Task FinalizeAuditAsync(HttpContext context, IAuditLogRepository auditRepository, AuditContext auditContext, bool exceptionOccurred)
    {
        if (auditContext.IsHandled)
        {
            return;
        }

        var endpoint = context.GetEndpoint();
        var exemption = endpoint?.Metadata.GetMetadata<AuditExemption>();
        if (exemption != null)
        {
            return;
        }

        var policy = endpoint?.Metadata.GetMetadata<AuditPolicy>();
        int statusCode = context.Response.StatusCode;

        // Router-produced 4xx (404/405/400...) on unrouted paths are scanner noise: never persist a chain row with a
        // client-chosen target. 401/403/429 and any exception/5xx path keep the regular audit behaviour (fail-closed).
        if (endpoint == null
            && !exceptionOccurred
            && statusCode >= StatusCodes.Status400BadRequest
            && statusCode < StatusCodes.Status500InternalServerError
            && statusCode is not (StatusCodes.Status401Unauthorized or StatusCodes.Status403Forbidden or StatusCodes.Status429TooManyRequests))
        {
            RecordUnroutedRequest(context, auditContext, statusCode);
            auditContext.MarkHandled();
            return;
        }
        var isDelegated = policy?.Level == AuditLevel.Delegated;
        if (isDelegated && !exceptionOccurred && statusCode < StatusCodes.Status400BadRequest)
        {
            // Delegated routes audit their successful path in the domain layer (which calls MarkHandled).
            // Denials, failures and exceptions are never delegated away: they are finalized here (fail-closed).
            return;
        }

        ResolveSecurityContext(context, auditContext);

        if (statusCode == StatusCodes.Status401Unauthorized)
        {
            auditContext.Deny("UNAUTHORIZED", AuditEventTypes.AuthFailed);
        }
        else if (statusCode == StatusCodes.Status403Forbidden)
        {
            auditContext.Deny("FORBIDDEN", AuditEventTypes.AuthzEndpointDenied);
        }
        else if (statusCode == StatusCodes.Status429TooManyRequests)
        {
            auditContext.Deny("RATE_LIMIT_EXCEEDED", AuditEventTypes.RateLimitExceeded);
        }
        else if (statusCode >= 500 && auditContext.Decision != "DENY")
        {
            auditContext.Error($"HTTP_{statusCode}", AuditEventTypes.QueryExecutionError);
        }
        else if (isDelegated && statusCode >= StatusCodes.Status400BadRequest && statusCode < 500 && auditContext.Decision == "ALLOW")
        {
            auditContext.Deny($"HTTP_{statusCode}", AuditEventTypes.AuthzEndpointDenied);
        }

        var eventType = auditContext.EventType ?? policy?.EventType ?? (auditContext.Decision == "DENY" ? AuditEventTypes.AuthzEndpointDenied : AuditEventTypes.TableQuery);
        var rawTarget = string.IsNullOrWhiteSpace(auditContext.Target) ? context.Request.Path.Value ?? "/" : auditContext.Target;
        var target = Truncate(rawTarget, MaxTargetLength);

        var detailsBuilder = new AuditDetailsBuilder()
            .WithField("channel", auditContext.Channel)
            .WithField("sourceIp", auditContext.SourceIp)
            .WithField("method", context.Request.Method)
            .WithField("path", context.Request.Path.Value)
            .WithField("statusCode", statusCode)
            .WithField("reasonCode", auditContext.ReasonCode);

        if (auditContext.Rows.HasValue) detailsBuilder.WithField("rows", auditContext.Rows.Value);
        if (auditContext.Bytes.HasValue) detailsBuilder.WithField("bytes", auditContext.Bytes.Value);

        foreach (var (k, v) in auditContext.AdditionalDetails)
        {
            detailsBuilder.WithField(k, v);
        }

        var entry = new AuditLogEntry
        {
            TenantId = auditContext.TenantId,
            EventType = eventType,
            ActorSid = auditContext.ActorSid,
            TargetTable = target,
            Decision = auditContext.Decision,
            TraceId = auditContext.TraceId,
            DetailsJson = detailsBuilder.Build()
        };

        if ((statusCode == StatusCodes.Status401Unauthorized || statusCode == StatusCodes.Status429TooManyRequests)
            && _authFailureAggregator != null)
        {
            if (statusCode == StatusCodes.Status429TooManyRequests)
            {
                await _authFailureAggregator.RecordRateLimitAsync(
                    auditContext.SourceIp ?? "unknown",
                    auditContext.ActorSid.Value,
                    auditContext.TenantId.Value).ConfigureAwait(false);
            }
            else
            {
                await _authFailureAggregator.RecordFailureAsync(
                    auditContext.SourceIp ?? "unknown",
                    auditContext.ReasonCode ?? "AUTH_FAILED",
                    auditContext.ActorSid.Value,
                    auditContext.TenantId.Value).ConfigureAwait(false);
            }
        }
        else
        {
            try
            {
                await auditRepository.RecordAuditEventAsync(entry).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to record audit log entry in AccessAuditMiddleware: {Message}", ex.Message);
                if (auditContext.Decision != "DENY")
                {
                    throw;
                }
            }
        }

        auditContext.MarkHandled();
    }

    private void RecordUnroutedRequest(HttpContext context, AuditContext auditContext, int statusCode)
    {
        GatewayDiagnostics.UnroutedRequestCounter.Add(
            1,
            new KeyValuePair<string, object?>("status_code", statusCode),
            new KeyValuePair<string, object?>("channel", auditContext.Channel));

        if (Interlocked.Increment(ref _unroutedLogSequence) % UnroutedLogSampleInterval == 1)
        {
            _logger.LogInformation(
                "Unrouted request answered with {StatusCode} (sampled 1/{SampleInterval}): {Method} {Path}",
                statusCode,
                UnroutedLogSampleInterval,
                context.Request.Method,
                Truncate(context.Request.Path.Value ?? "/", 128));
        }
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private static void ResolveSecurityContext(HttpContext context, AuditContext auditContext)
    {
        if (context.User?.Identity?.IsAuthenticated == true)
        {
            var sidClaim = context.User.FindFirst("sid")?.Value
                ?? context.User.FindFirst(ClaimTypes.PrimarySid)?.Value
                ?? context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? context.User.FindFirst("sub")?.Value;

            if (!string.IsNullOrWhiteSpace(sidClaim))
            {
                auditContext.ActorSid = new Sid(sidClaim);
            }

            // Tenant attribution uses the same precedence as the rest of the pipeline: the resolved SecurityPrincipalContext
            // first, then the canonical claim resolution. An invalid claim never fails the audit finalizer (API-7: that
            // surfaced as a 500); the entry keeps the default tenant, is flagged, and never records the raw claim value.
            if (context.Items.TryGetValue(SecurityPrincipalContext.ItemKey, out var item) && item is SecurityPrincipalContext principalContext)
            {
                auditContext.TenantId = principalContext.TenantId;
            }
            else
            {
                try
                {
                    auditContext.TenantId = context.User.GetTenantId();
                }
                catch (System.Security.SecurityException)
                {
                    auditContext.AdditionalDetails["tenantClaim"] = "INVALID";
                }
            }
        }
    }

    private static string ResolveChannel(string path, HttpContext context)
    {
        if (path.StartsWith("/graphql", StringComparison.OrdinalIgnoreCase)) return "GraphQL";
        if (path.StartsWith("/api/v1/flight", StringComparison.OrdinalIgnoreCase)) return "Flight";
        if (path.StartsWith("/mcp", StringComparison.OrdinalIgnoreCase)) return "MCP";
        if (path.StartsWith("/odata", StringComparison.OrdinalIgnoreCase)) return "OData";
        if (path.StartsWith("/api/v1/websql", StringComparison.OrdinalIgnoreCase) || path.StartsWith("/api/v1/sql", StringComparison.OrdinalIgnoreCase)) return "WebSQL";
        if (path.StartsWith("/api/v1/lakehouse", StringComparison.OrdinalIgnoreCase)) return "Iceberg";
        if (path.StartsWith("/api/v1/duckdb", StringComparison.OrdinalIgnoreCase)) return "DuckDB";
        if (path.StartsWith("/api/v1/envoy", StringComparison.OrdinalIgnoreCase)) return "Envoy";
        if (path.StartsWith("/api/webhooks", StringComparison.OrdinalIgnoreCase)) return "Webhook";
        return "REST";
    }
}
