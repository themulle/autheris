namespace Autheris.Api.Middleware;

using Autheris.Api.Errors;
using System;
using System.Threading.Tasks;
using Autheris.Api.Extensions;
using Autheris.Api.Security;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

/// <summary>
/// SEC M-05: Enforces <c>Content-Type: application/json</c> on MCP JSON-RPC POST requests so browsers cannot submit
/// cross-site simple requests (<c>text/plain</c> or form-urlencoded) carrying ambient credentials (WP-ARC-C-04).
/// </summary>
public sealed class StrictMcpGuardMiddleware
{
    private readonly RequestDelegate _next;
    private readonly GatewayOptions _options;

    public StrictMcpGuardMiddleware(RequestDelegate next, IOptions<GatewayOptions> options)
        : this(next, (options ?? throw new ArgumentNullException(nameof(options))).Value)
    {
    }

    public StrictMcpGuardMiddleware(RequestDelegate next, GatewayOptions options)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (HttpMethods.IsPost(context.Request.Method) &&
            McpPathResolver.IsMcpRequest(context.Request.Path, _options) &&
            !GatewayApplicationBuilderExtensions.IsJsonContentType(context.Request.ContentType))
        {
            await ApiProblems.WriteAsync(context, StatusCodes.Status415UnsupportedMediaType, ApiErrorCodes.UnsupportedMediaType, "MCP requests must use 'Content-Type: application/json'.", context.RequestAborted).ConfigureAwait(false);
            return;
        }

        await _next(context).ConfigureAwait(false);
    }
}
