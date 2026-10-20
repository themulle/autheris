namespace Autheris.Api.Endpoints;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Security;
using Autheris.Application.Identity.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

public static class ScimUsersEndpoints
{
    public static IEndpointRouteBuilder MapScimUsersEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/scim/v2/{realmId}/Users")
            .RequireAuthorization(GatewayAuthSchemes.Scim);

        group.MapGet("", async (
            string realmId,
            int? startIndex,
            int? count,
            string? filter,
            HttpContext httpContext,
            IScimService scimService,
            CancellationToken ct) =>
        {
            var tenantId = httpContext.User.GetTenantId();
            var result = await scimService.ListUsersAsync(
                tenantId, realmId, startIndex ?? 1, count ?? 100, filter, ct).ConfigureAwait(false);
            return Results.Ok(result);
        });

        group.MapGet("/{id}", async (
            string realmId,
            string id,
            HttpContext httpContext,
            IScimService scimService,
            CancellationToken ct) =>
        {
            var tenantId = httpContext.User.GetTenantId();
            var user = await scimService.GetUserAsync(tenantId, realmId, id, ct).ConfigureAwait(false);
            return user != null ? Results.Ok(user) : Results.NotFound();
        });

        group.MapPost("", async (
            string realmId,
            ScimUser user,
            HttpContext httpContext,
            IScimService scimService,
            CancellationToken ct) =>
        {
            var tenantId = httpContext.User.GetTenantId();
            var created = await scimService.CreateUserAsync(tenantId, realmId, user, ct).ConfigureAwait(false);
            return Results.Created($"/scim/v2/{realmId}/Users/{created.UserKey}", created);
        });

        group.MapPut("/{id}", async (
            string realmId,
            string id,
            ScimUser user,
            HttpContext httpContext,
            IScimService scimService,
            CancellationToken ct) =>
        {
            var tenantId = httpContext.User.GetTenantId();
            var updated = await scimService.UpdateUserAsync(tenantId, realmId, id, user, ct).ConfigureAwait(false);
            return updated != null ? Results.Ok(updated) : Results.NotFound();
        });

        group.MapPatch("/{id}", async (
            string realmId,
            string id,
            ScimPatchRequest patch,
            HttpContext httpContext,
            IScimService scimService,
            CancellationToken ct) =>
        {
            var tenantId = httpContext.User.GetTenantId();
            var patched = await scimService.PatchUserAsync(tenantId, realmId, id, patch, ct).ConfigureAwait(false);
            return patched != null ? Results.Ok(patched) : Results.NotFound();
        });

        group.MapDelete("/{id}", async (
            string realmId,
            string id,
            HttpContext httpContext,
            IScimService scimService,
            CancellationToken ct) =>
        {
            var tenantId = httpContext.User.GetTenantId();
            var deleted = await scimService.DeleteUserAsync(tenantId, realmId, id, ct).ConfigureAwait(false);
            return deleted ? Results.NoContent() : Results.NotFound();
        });

        return app;
    }
}
