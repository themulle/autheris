namespace Autheris.Api.Endpoints;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Errors;
using Autheris.Domain.Caching.TableCache;
using Autheris.Domain.Common;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

public static class TableCacheAdminEndpoints
{
    public static IEndpointRouteBuilder MapTableCacheAdminEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/admin/cache")
            .RequireAuthorization("AdminPolicy");

        group.MapGet("/tables", (ITableCacheCatalog catalog) =>
        {
            return Results.Ok(catalog.GetAll());
        });

        group.MapGet("/stats", (ITableCacheManager manager) =>
        {
            return Results.Ok(manager.GetStats());
        });

        group.MapPost("/refresh/{tableName}", async (
            string tableName,
            ITableCacheCatalog catalog,
            ITableCacheManager manager,
            HttpContext context,
            CancellationToken ct) =>
        {
            if (!TableIdentifier.TryParse(tableName, out var table))
            {
                return ApiProblems.BadRequest(ApiErrorCodes.InvalidTableIdentifier, "The table identifier must be 'domain.schema.table' or 'schema.table'.", context);
            }

            if (!catalog.TryGet(table, out var target) || target is null)
            {
                return ApiProblems.NotFound(ApiErrorCodes.TableNotRegistered, "The table is not registered in the table cache catalog.", context);
            }

            try
            {
                await manager.ForceRefreshAsync(target.Table, ct).ConfigureAwait(false);
                return Results.Ok(new { Message = $"Table cache refresh initiated for '{target.Table}'." });
            }
            catch (Exception)
            {
                return Results.Problem(detail: "Table cache refresh operation failed.", statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        group.MapPost("/invalidate/{tableName}", async (
            string tableName,
            ITableCacheCatalog catalog,
            ITableCacheInvalidator invalidator,
            HttpContext context,
            CancellationToken ct) =>
        {
            if (!TableIdentifier.TryParse(tableName, out var table))
            {
                return ApiProblems.BadRequest(ApiErrorCodes.InvalidTableIdentifier, "The table identifier must be 'domain.schema.table' or 'schema.table'.", context);
            }

            if (!catalog.TryGet(table, out var target) || target is null)
            {
                return ApiProblems.NotFound(ApiErrorCodes.TableNotRegistered, "The table is not registered in the table cache catalog.", context);
            }

            await invalidator.InvalidateAsync(target.Table, "Admin request", ct).ConfigureAwait(false);
            return Results.Ok(new { Message = $"Table cache invalidated for '{target.Table}'." });
        });

        return endpoints;
    }
}
