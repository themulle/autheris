namespace Autheris.Api.Middleware;

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

        var tenantId = context.User.FindFirst("tenant_id")?.Value;
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
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.ContentType = "application/json";

            var err = JsonSerializer.Serialize(new
            {
                errors = new[]
                {
                    new
                    {
                        message = "Maximum concurrent incremental delivery streams exceeded for client.",
                        extensions = new { code = "INCREMENTAL_STREAM_LIMIT_EXCEEDED", client = clientKey }
                    }
                }
            });

            await context.Response.WriteAsync(err, context.RequestAborted).ConfigureAwait(false);
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
