using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Visibility;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using HotChocolate;
using Microsoft.AspNetCore.Http;

namespace Autheris.GraphQL.Types;

public sealed partial class Query
{
    /// <summary>
    /// Container tiles (business domains and data products) with the caller's catalog access state. Same service, page cap and
    /// tenant derivation as the REST endpoints and the MCP tool; tiles carry metadata only.
    /// </summary>
    public async Task<IReadOnlyList<ContainerTile>> GetContainersAsync(
        string? kind = null,
        int? limit = null,
        [Service] IContainerCatalogService? containerCatalog = null,
        [Service] IHttpContextAccessor httpContextAccessor = default!,
        HotChocolate.Resolvers.IResolverContext? resolverContext = null,
        CancellationToken ct = default)
    {
        var (principal, tenantId) = ResolveContainerCaller(httpContextAccessor, resolverContext);

        ContainerKind? filter = null;
        if (!string.IsNullOrWhiteSpace(kind))
        {
            if (!TryParseContainerKind(kind, out var parsed))
            {
                throw new GraphQLException(ErrorBuilder.New()
                    .SetCode("BAD_REQUEST")
                    .SetMessage("The container kind is invalid.")
                    .Build());
            }

            filter = parsed;
        }

        if (containerCatalog is null)
        {
            return [];
        }

        return await containerCatalog.ListAsync(tenantId, principal, filter, limit, ct).ConfigureAwait(false);
    }

    /// <summary>One container tile. An unknown kind, an unknown id and an unwired service all answer null, exactly like a missing container.</summary>
    public async Task<ContainerTile?> GetContainerAsync(
        string kind,
        Guid id,
        [Service] IContainerCatalogService? containerCatalog = null,
        [Service] IHttpContextAccessor httpContextAccessor = default!,
        HotChocolate.Resolvers.IResolverContext? resolverContext = null,
        CancellationToken ct = default)
    {
        var (principal, tenantId) = ResolveContainerCaller(httpContextAccessor, resolverContext);
        if (containerCatalog is null || !TryParseContainerKind(kind, out var parsed))
        {
            return null;
        }

        return await containerCatalog.GetAsync(tenantId, principal, parsed, id, ct).ConfigureAwait(false);
    }

    private static (ClaimsPrincipal Principal, TenantId Tenant) ResolveContainerCaller(
        IHttpContextAccessor httpContextAccessor, HotChocolate.Resolvers.IResolverContext? resolverContext)
    {
        var principal = Autheris.GraphQL.Security.GraphQlCallerPrincipal.Resolve(resolverContext, httpContextAccessor);
        if (principal?.Identity?.IsAuthenticated != true || principal.GetUserSid() == null)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("UNAUTHORIZED")
                .SetMessage("Authentication is required for catalog queries.")
                .Build());
        }

        try
        {
            return (principal, principal.GetTenantId());
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or Autheris.Domain.Exceptions.GatewaySecurityException)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("UNAUTHORIZED")
                .SetMessage("No valid tenant in the authentication token.")
                .Build());
        }
    }

    private static bool TryParseContainerKind(string? value, out ContainerKind kind)
    {
        if (string.Equals(value, "domain", StringComparison.OrdinalIgnoreCase))
        {
            kind = ContainerKind.Domain;
            return true;
        }

        if (string.Equals(value, "dataProduct", StringComparison.OrdinalIgnoreCase))
        {
            kind = ContainerKind.DataProduct;
            return true;
        }

        kind = default;
        return false;
    }
}
