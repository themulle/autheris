namespace Autheris.Api.Endpoints;

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Errors;
using Autheris.Api.Extensions;
using Autheris.Application.Catalog.Visibility;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

public static partial class CatalogApiEndpoints
{
    internal static async Task<IResult> ListContainersAsync(
        HttpContext http,
        string? kind,
        int? limit,
        [FromServices] IContainerCatalogService? service,
        CancellationToken ct)
    {
        ContainerKind? filter = null;
        if (!string.IsNullOrWhiteSpace(kind))
        {
            if (!TryParseContainerKind(kind, out var parsed))
            {
                return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "The container kind is invalid.", http);
            }

            filter = parsed;
        }

        return await ListTilesAsync(http, filter, limit, service, static tiles => new { items = tiles }, ct).ConfigureAwait(false);
    }

    internal static async Task<IResult> ListBusinessDomainsAsync(
        HttpContext http,
        int? limit,
        [FromServices] IContainerCatalogService? service,
        CancellationToken ct) =>
        await ListTilesAsync(http, ContainerKind.Domain, limit, service, static tiles => new { domains = tiles, totalDomains = tiles.Count }, ct)
            .ConfigureAwait(false);

    internal static async Task<IResult> GetContainerAsync(
        HttpContext http,
        string kind,
        Guid id,
        [FromServices] IContainerCatalogService? service,
        CancellationToken ct)
    {
        var sec = http.GetSecurityContext();
        if (!sec.IsAuthenticated || sec.TenantClaimInvalid)
        {
            return Results.Unauthorized();
        }

        // An unknown kind, an unknown id and a service that is not wired answer exactly like a missing container.
        if (service is null || !TryParseContainerKind(kind, out var parsed))
        {
            return ApiProblems.NotFound(ApiErrorCodes.ResourceNotFound, "Requested container was not found.", http);
        }

        var tile = await service.GetAsync(sec.TenantId, http.User, parsed, id, ct).ConfigureAwait(false);
        return tile is null
            ? ApiProblems.NotFound(ApiErrorCodes.ResourceNotFound, "Requested container was not found.", http)
            : Results.Ok(tile);
    }

    private static async Task<IResult> ListTilesAsync(
        HttpContext http,
        ContainerKind? kind,
        int? limit,
        IContainerCatalogService? service,
        Func<System.Collections.Generic.IReadOnlyList<ContainerTile>, object> shape,
        CancellationToken ct)
    {
        var sec = http.GetSecurityContext();
        if (!sec.IsAuthenticated || sec.TenantClaimInvalid)
        {
            return Results.Unauthorized();
        }

        if (service is null)
        {
            return Results.Ok(shape([]));
        }

        var tiles = await service.ListAsync(sec.TenantId, http.User, kind, limit, ct).ConfigureAwait(false);
        return Results.Ok(shape(tiles));
    }

    private static bool TryParseContainerKind(string? value, out ContainerKind kind)
    {
        if (string.Equals(value, "domain", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "domains", StringComparison.OrdinalIgnoreCase))
        {
            kind = ContainerKind.Domain;
            return true;
        }

        if (string.Equals(value, "dataProduct", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "data-product", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "product", StringComparison.OrdinalIgnoreCase))
        {
            kind = ContainerKind.DataProduct;
            return true;
        }

        kind = default;
        return false;
    }
}
