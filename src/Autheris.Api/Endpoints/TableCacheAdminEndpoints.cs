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
            ITableCacheManager manager,
            CancellationToken ct) =>
        {
            if (!TableIdentifier.TryParse(tableName, out var table))
            {
                return Results.BadRequest($"Invalid table identifier format: '{tableName}'. Expected 'domain.schema.table' or 'schema.table'.");
            }

            try
            {
                await manager.ForceRefreshAsync(table, ct).ConfigureAwait(false);
                return Results.Ok(new { Message = $"Table cache refresh initiated for '{table}'." });
            }
            catch (Exception)
            {
                return Results.Problem(detail: "Table cache refresh operation failed.", statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        group.MapPost("/invalidate/{tableName}", async (
            string tableName,
            ITableCacheInvalidator invalidator,
            CancellationToken ct) =>
        {
            if (!TableIdentifier.TryParse(tableName, out var table))
            {
                return Results.BadRequest($"Invalid table identifier format: '{tableName}'. Expected 'domain.schema.table' or 'schema.table'.");
            }

            await invalidator.InvalidateAsync(table, "Admin request", ct).ConfigureAwait(false);
            return Results.Ok(new { Message = $"Table cache invalidated for '{table}'." });
        });

        return endpoints;
    }
}
