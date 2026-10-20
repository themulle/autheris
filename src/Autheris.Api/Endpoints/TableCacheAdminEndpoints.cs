namespace Autheris.Api.Endpoints;

using System;
using System.Threading;
using System.Threading.Tasks;
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
            CancellationToken ct) =>
        {
            if (!TableIdentifier.TryParse(tableName, out var table))
            {
                return Results.BadRequest($"Invalid table identifier format: '{tableName}'. Expected 'domain.schema.table' or 'schema.table'.");
            }

            if (!catalog.TryGet(table, out var target) || target is null)
            {
                return Results.NotFound(new { Message = $"Table '{table}' is not registered in the table cache catalog." });
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
            CancellationToken ct) =>
        {
            if (!TableIdentifier.TryParse(tableName, out var table))
            {
                return Results.BadRequest($"Invalid table identifier format: '{tableName}'. Expected 'domain.schema.table' or 'schema.table'.");
            }

            if (!catalog.TryGet(table, out var target) || target is null)
            {
                return Results.NotFound(new { Message = $"Table '{table}' is not registered in the table cache catalog." });
            }

            await invalidator.InvalidateAsync(target.Table, "Admin request", ct).ConfigureAwait(false);
            return Results.Ok(new { Message = $"Table cache invalidated for '{target.Table}'." });
        });

        return endpoints;
    }
}
