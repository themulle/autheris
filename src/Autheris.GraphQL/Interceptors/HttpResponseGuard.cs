namespace Autheris.GraphQL.Interceptors;

using Microsoft.AspNetCore.Http;

/// <summary>
/// GQL-2: GraphQL request middlewares also run for operations over graphql-ws, where the HttpContext is the upgrade
/// request whose response has long started (101). Setting headers or the status there throws; these helpers skip it.
/// </summary>
internal static class HttpResponseGuard
{
    public static bool CanModify(HttpContext httpContext) => !httpContext.Response.HasStarted;

    public static void SetHeader(HttpContext httpContext, string name, string value)
    {
        if (CanModify(httpContext))
        {
            httpContext.Response.Headers[name] = value;
        }
    }

    public static void RemoveHeader(HttpContext httpContext, string name)
    {
        if (CanModify(httpContext))
        {
            httpContext.Response.Headers.Remove(name);
        }
    }

    public static void SetStatus(HttpContext httpContext, int statusCode)
    {
        if (CanModify(httpContext))
        {
            httpContext.Response.StatusCode = statusCode;
        }
    }
}
