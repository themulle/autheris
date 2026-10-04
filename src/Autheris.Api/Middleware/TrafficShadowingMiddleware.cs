namespace Autheris.Api.Middleware;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Autheris.Application.Diagnostics.Shadowing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

/// <summary>
/// F-OPS-01: AST-Aware Production Traffic Shadowing Middleware.
/// Evaluates query safety via AST filters, redacts PII, and non-blockingly enqueues
/// safe read queries for asynchronous replay against dark canary/staging environments.
/// </summary>
public sealed class TrafficShadowingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<TrafficShadowingMiddleware> _logger;

    public TrafficShadowingMiddleware(
        RequestDelegate next,
        ILogger<TrafficShadowingMiddleware> logger)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task InvokeAsync(HttpContext context, ITrafficShadowingService? shadowingService)
    {
        if (shadowingService == null || !shadowingService.IsEnabled || !shadowingService.ShouldSample())
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var path = context.Request.Path.Value ?? string.Empty;
        var isTargetEndpoint = path.Contains("/graphql", StringComparison.OrdinalIgnoreCase) ||
                               path.Contains("/api/sql", StringComparison.OrdinalIgnoreCase) ||
                               path.Contains("/websql", StringComparison.OrdinalIgnoreCase);

        if (!isTargetEndpoint)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // SEC M-07: Guard against memory exhaustion from oversized payloads in shadowing path (max 2 MB)
        if (context.Request.ContentLength > 2 * 1024 * 1024)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        string? body = null;
        if (context.Request.ContentLength > 0 || context.Request.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
        {
            context.Request.EnableBuffering();
            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true);
            body = await reader.ReadToEndAsync(context.RequestAborted).ConfigureAwait(false);
            context.Request.Body.Position = 0;
        }

        var (isSafe, reason) = AstShadowingFilter.IsSafeForShadowing(path, body);
        if (isSafe)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (headerKey, headerValues) in context.Request.Headers)
            {
                headers[headerKey] = headerValues.ToString();
            }

            var shadowReq = new ShadowRequest(
                Method: context.Request.Method,
                PathAndQuery: context.Request.Path + context.Request.QueryString,
                Headers: headers,
                Body: body
            );

            shadowingService.EnqueueShadowRequest(shadowReq);
        }
        else
        {
            _logger.LogDebug("F-OPS-01 Dropped request from shadowing: {Reason}", reason);
        }

        await _next(context).ConfigureAwait(false);
    }
}
