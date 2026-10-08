namespace Autheris.Api.Middleware;

using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Http;

/// <summary>
/// Finding 3.3 / R13: read-only tokens (<see cref="TokenAccessScope"/>) may only send safe requests or POST to the
/// query endpoints. Those endpoints reject writes themselves (GraphQL mutations, WebSQL DML).
/// </summary>
public sealed class ReadOnlyTokenMiddleware
{
    private readonly RequestDelegate _next;
    private readonly PathString[] _queryPaths;

    public ReadOnlyTokenMiddleware(RequestDelegate next, PathString graphQlPath, PathString mcpPath)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _queryPaths =
        [
            graphQlPath,
            mcpPath,
            "/api/v1/sql",
            "/api/sql",
            "/api/v1/queries",
            "/api/v1/export/arrow"
        ];
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.User.IsReadOnly() || IsPermitted(context.Request))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            errors = new[]
            {
                new
                {
                    message = "This token only permits read access.",
                    extensions = new { code = "READ_ONLY_TOKEN" }
                }
            }
        }), context.RequestAborted).ConfigureAwait(false);
    }

    private bool IsPermitted(HttpRequest request) =>
        HttpMethods.IsGet(request.Method) ||
        HttpMethods.IsHead(request.Method) ||
        HttpMethods.IsOptions(request.Method) ||
        (HttpMethods.IsPost(request.Method) && _queryPaths.Any(p => request.Path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase)));
}
