namespace Autheris.Api.Middleware;

using System.Diagnostics;
using System.Text.Json;
using Autheris.Application.Extensibility;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public sealed class GatewayExtensibilityMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IExtensibilityPipeline _pipeline;
    private readonly GatewayOptions _options;
    private readonly ILogger<GatewayExtensibilityMiddleware> _logger;

    private const long MaxExtensibilityBufferBytes = 16 * 1024 * 1024; // 16 MB limit to prevent LOH DoS / OOM

    /// <summary>Ingress item carrying the connection-derived client IP (SEC M-06).</summary>
    public const string ClientIpItemKey = "GatewayClientIp";

    public GatewayExtensibilityMiddleware(
        RequestDelegate _next,
        IExtensibilityPipeline pipeline,
        IOptions<GatewayOptions> options,
        ILogger<GatewayExtensibilityMiddleware> logger)
    {
        this._next = _next;
        _pipeline = pipeline;
        _options = options.Value;
        _logger = logger;
    }

    internal static bool IsStreamingRequest(HttpContext context)
    {
        if (context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpUpgradeFeature>()?.IsUpgradableRequest == true ||
            context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpExtendedConnectFeature>()?.IsExtendedConnect == true ||
            context.Request.Headers.Upgrade.ToString().Contains("websocket", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var accept = context.Request.Headers.Accept.ToString();
        return accept.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase) ||
               accept.Contains("multipart/mixed", StringComparison.OrdinalIgnoreCase) ||
               accept.Contains("application/x-ndjson", StringComparison.OrdinalIgnoreCase) ||
               accept.Contains("application/graphql-response+jsonl", StringComparison.OrdinalIgnoreCase);
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!_options.Extensibility.Enabled)
        {
            await _next(context);
            return;
        }

        var endpoint = _options.GraphQL.EndpointPath.StartsWith('/')
            ? _options.GraphQL.EndpointPath
            : "/" + _options.GraphQL.EndpointPath;

        // Apply to GraphQL endpoint and API endpoints
        if (!context.Request.Path.StartsWithSegments(endpoint) && !context.Request.Path.StartsWithSegments("/api"))
        {
            await _next(context);
            return;
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in context.Request.Headers)
        {
            headers[k] = v.ToString();
        }

        var ingressContext = new IngressContext
        {
            User = context.User,
            Path = context.Request.Path.Value ?? "/",
            Method = context.Request.Method,
            Headers = headers,
            Query = context.Request.QueryString.Value
        };

        // SEC M-06: Client IP from the connection (already resolved by UseForwardedHeaders against KnownProxies),
        // never from a raw client-controlled X-Forwarded-For header.
        ingressContext.Items[ClientIpItemKey] = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        // 1. Ingress phase
        var ingressResult = await _pipeline.ProcessIngressAsync(ingressContext, context.RequestAborted);
        if (ingressResult.Decision != IngressDecision.Continue)
        {
            context.Response.StatusCode = ingressResult.StatusCode;
            foreach (var (k, v) in ingressResult.ResponseHeaders)
            {
                context.Response.Headers[k] = v;
            }

            if (ingressResult.ShortCircuitPayload != null)
            {
                await context.Response.WriteAsJsonAsync(ingressResult.ShortCircuitPayload, context.RequestAborted);
            }
            else
            {
                await context.Response.WriteAsJsonAsync(new
                {
                    decision = ingressResult.Decision.ToString(),
                    reason = ingressResult.Reason,
                    statusCode = ingressResult.StatusCode
                }, context.RequestAborted);
            }
            return;
        }

        // Store ingress items in HttpContext.Items for downstream handlers
        foreach (var (k, v) in ingressContext.Items)
        {
            context.Items[k] = v;
        }

        // SEC M-04: Streaming requests (WebSocket upgrade, SSE, multipart/@defer) are never buffered.
        if (IsStreamingRequest(context))
        {
            await _next(context);
            return;
        }

        // 2. Execution phase with bounded response interception for Egress
        var originalBodyStream = context.Response.Body;
        await using var boundedStream = new BoundedResponseBufferStream(originalBodyStream, MaxExtensibilityBufferBytes, context.Response);
        context.Response.Body = boundedStream;

        var sw = Stopwatch.StartNew();
        try
        {
            await _next(context);
        }
        finally
        {
            sw.Stop();
            context.Response.Body = originalBodyStream;
        }

        // Buffer limit protection: the response was streamed through without egress transformation.
        if (boundedStream.IsPassThrough)
        {
            _logger.LogWarning("Response was streamed without egress transformation ({Reason}); extensibility buffering limit is {Limit} bytes.",
                boundedStream.PassThroughReason, MaxExtensibilityBufferBytes);
            return;
        }

        var responseBytes = boundedStream.GetBufferedBytes();

        // 3. Egress phase
        var egressContext = new EgressContext
        {
            IngressContext = ingressContext,
            StatusCode = context.Response.StatusCode,
            ResponseBytes = responseBytes,
            Elapsed = sw.Elapsed
        };

        var egressResult = await _pipeline.ProcessEgressAsync(egressContext, context.RequestAborted);

        foreach (var (k, v) in egressResult.AdditionalHeaders)
        {
            context.Response.Headers[k] = v;
        }

        byte[] finalBytes = responseBytes;
        if (egressResult.Handled && egressResult.MutatedResponseBytes.HasValue)
        {
            finalBytes = egressResult.MutatedResponseBytes.Value.ToArray();
        }
        else if (egressResult.Handled && egressResult.MutatedResponseText != null)
        {
            finalBytes = System.Text.Encoding.UTF8.GetBytes(egressResult.MutatedResponseText);
        }

        if (finalBytes.Length > 0 && !context.Response.HasStarted)
        {
            context.Response.ContentLength = finalBytes.Length;
            await context.Response.Body.WriteAsync(finalBytes, context.RequestAborted);
        }
    }
}
