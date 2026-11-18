namespace Autheris.Api.Endpoints;

using System.Threading;
using System.Threading.Tasks;
using Autheris.GraphQL.Catalog;
using Microsoft.AspNetCore.Http;

public static partial class CatalogApiEndpoints
{
    /// <summary>
    /// Issue #802: <c>GET /api/v1/catalog/graphql-schema-status</c> answers 200 <c>{"status":"Current"}</c> or 503
    /// <c>{"status":"Updating"}</c> with <c>Retry-After: 1</c>, and 404 when the GraphQL catalog module is not registered. A client
    /// polls it after a catalog write before it queries GraphQL. The body never carries catalog names, counts or versions.
    /// </summary>
    internal static async Task<IResult> GetGraphQlSchemaStatusAsync(ICatalogSchemaFreshness? freshness, HttpContext httpContext, CancellationToken ct)
    {
        httpContext.Response.Headers.CacheControl = "no-store";
        if (freshness is null)
        {
            return Results.NotFound();
        }

        if (await freshness.IsSchemaCurrentAsync(ct).ConfigureAwait(false))
        {
            return Results.Ok(new { status = "Current" });
        }

        httpContext.Response.Headers.RetryAfter = "1";
        return Results.Json(new { status = "Updating" }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}
