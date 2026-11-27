namespace Autheris.GraphQL.Interceptors;

using Microsoft.AspNetCore.Http;

/// <summary>
/// GQL-2: GraphQL request middlewares also run for operations over graphql-ws, where the HttpContext is the upgrade
/// request whose response has long started (101). Setting headers or the status there throws; these helpers skip it.
/// An in-process execution for an MCP tool call must not touch the MCP transport response either (status, cost and rate-limit headers).
/// </summary>
internal static class HttpResponseGuard
{
    public static bool CanModify(HttpContext? httpContext) =>
        httpContext != null
        && !httpContext.Response.HasStarted
        && !(httpContext.Items.TryGetValue(Autheris.Application.Mcp.McpTransportItems.ToolCallKey, out var mcp) && mcp is true);

    public static void SetHeader(HttpContext? httpContext, string name, string value)
    {
        if (CanModify(httpContext))
        {
            httpContext!.Response.Headers[name] = value;
        }
    }

    public static void RemoveHeader(HttpContext? httpContext, string name)
    {
        if (CanModify(httpContext))
        {
            httpContext!.Response.Headers.Remove(name);
        }
    }

    public static void SetStatus(HttpContext? httpContext, int statusCode)
    {
        if (CanModify(httpContext))
        {
            httpContext!.Response.StatusCode = statusCode;
        }
    }
}
