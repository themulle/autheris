namespace Autheris.Api.Middleware;

using Autheris.Api.Errors;
using System;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Application.Performance.IncrementalDelivery;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

/// <summary>
/// F-PERF-12: Incremental Delivery Concurrency and Connection Throttling Middleware.
/// Governs streaming multipart HTTP connections and prevents connection pool exhaustion.
/// </summary>
public sealed class IncrementalDeliveryMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<IncrementalDeliveryMiddleware> _logger;

    public IncrementalDeliveryMiddleware(
        RequestDelegate next,
        ILogger<IncrementalDeliveryMiddleware> logger)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task InvokeAsync(HttpContext context, IIncrementalDeliveryManager? manager)
    {
        if (manager == null || !manager.IsEnabled)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var acceptHeader = context.Request.Headers.Accept.ToString();
        var isMultipartRequest = acceptHeader.Contains("multipart/mixed", StringComparison.OrdinalIgnoreCase);

        if (!isMultipartRequest)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // DT-SC-03: a slot key, not a decision; only a valid tenant claim contributes (an invalid or conflicting one is ignored).
        var tenantClaim = Autheris.Domain.Tenancy.TenantClaimReader.Read(context.User);
        var tenantId = tenantClaim.Kind == Autheris.Domain.Tenancy.ClaimTenantKind.Valid ? tenantClaim.Tenant.Value : null;
        var subjectId = context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                        ?? context.User.FindFirst("sub")?.Value
                        ?? context.User.FindFirst("oid")?.Value;

        var clientKey = (tenantId, subjectId) switch
        {
            (not null, not null) => $"{tenantId}:{subjectId}",
            (not null, null) => tenantId,
            (null, not null) => subjectId,
            _ => context.Connection.RemoteIpAddress?.ToString() ?? "anonymous"
        };

        if (!manager.TryAcquireStreamSlot(clientKey))
        {
            await ApiProblems.WriteTooManyRequests(context, ApiErrorCodes.IncrementalStreamLimitExceeded, "Maximum concurrent incremental delivery streams exceeded for client.", 1, context.RequestAborted).ConfigureAwait(false);
            return;
        }

        try
        {
            await _next(context).ConfigureAwait(false);
        }
        finally
        {
            manager.ReleaseStreamSlot(clientKey);
        }
    }
}
