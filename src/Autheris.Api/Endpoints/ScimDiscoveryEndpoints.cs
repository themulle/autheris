namespace Autheris.Api.Endpoints;

using Autheris.Api.Extensions;
using Autheris.Application.Identity.Services;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

public static class ScimDiscoveryEndpoints
{
    public static IEndpointRouteBuilder MapScimDiscoveryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/scim/v2/{realmId}");
        group.AddEndpointFilter(async (ctx, next) =>
        {
            if (ctx.HttpContext.User.Identity?.IsAuthenticated == true
                && ctx.HttpContext.Request.RouteValues.TryGetValue("realmId", out var r)
                && r is string realmId
                && !string.IsNullOrWhiteSpace(realmId)
                && ctx.HttpContext.RequestServices.GetService<IScimSyncStateTracker>() is { } tracker)
            {
                await tracker.RecordAuthenticatedContactAsync(
                    ctx.HttpContext.User.GetTenantId(),
                    realmId,
                    isWrite: false,
                    ctx.HttpContext.RequestAborted).ConfigureAwait(false);
            }

            return await next(ctx).ConfigureAwait(false);
        });

        group.MapGet("/ServiceProviderConfig", (string realmId) =>
        {
            var config = new
            {
                schemas = new[] { ScimSchemaUris.ServiceProviderConfig },
                documentationUri = "https://tools.ietf.org/html/rfc7644",
                patch = new { supported = true },
                bulk = new { supported = false, maxOperations = 0, maxPayloadSize = 0 },
                filter = new { supported = false, maxResults = 0 },
                changePassword = new { supported = false },
                sort = new { supported = false },
                etag = new { supported = false },
                authenticationSchemes = new[]
                {
                    new
                    {
                        name = "OAuth Bearer Token",
                        description = "Authentication scheme using the OAuth Bearer Token Standard",
                        specUri = "https://tools.ietf.org/html/rfc6750",
                        type = "oauthbearertoken",
                        primary = true
                    }
                }
            };
            return Results.Ok(config);
        }).WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

        group.MapGet("/Schemas", (string realmId) => GetSchemas())
            .WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

        group.MapGet("/ResourceTypes", (string realmId) => GetResourceTypes(realmId))
            .WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

        return app;
    }

    internal static IResult GetSchemas()
    {
        var schemas = new
        {
            schemas = new[] { ScimSchemaUris.ListResponse },
            totalResults = 4,
            itemsPerPage = 4,
            startIndex = 1,
            Resources = new object[]
            {
                new
                {
                    schemas = new[] { ScimSchemaUris.Schema },
                    id = ScimSchemaUris.CoreUser,
                    name = "User",
                    description = "User Account",
                    schema = ScimSchemaUris.CoreUser,
                    attributes = new object[]
                    {
                        new { name = "userName", type = "string", multiValued = false, required = true, uniqueness = "server" },
                        new { name = "externalId", type = "string", multiValued = false, required = false, uniqueness = "server" },
                        new { name = "displayName", type = "string", multiValued = false, required = false, uniqueness = "none" },
                        new { name = "active", type = "boolean", multiValued = false, required = false, uniqueness = "none" }
                    }
                },
                new
                {
                    schemas = new[] { ScimSchemaUris.Schema },
                    id = ScimSchemaUris.CoreGroup,
                    name = "Group",
                    description = "Group",
                    schema = ScimSchemaUris.CoreGroup,
                    attributes = new object[]
                    {
                        new { name = "displayName", type = "string", multiValued = false, required = true, uniqueness = "none" },
                        new
                        {
                            name = "members",
                            type = "complex",
                            multiValued = true,
                            required = false,
                            subAttributes = new object[]
                            {
                                new { name = "value", type = "string", multiValued = false, required = true },
                                new { name = "display", type = "string", multiValued = false, required = false },
                                new { name = "type", type = "string", multiValued = false, required = false, canonicalValues = new[] { ScimMemberTypes.User, ScimMemberTypes.Group } }
                            }
                        }
                    }
                },
                new
                {
                    schemas = new[] { ScimSchemaUris.Schema },
                    id = ScimSchemaUris.AutherisUserExtension,
                    name = "AutherisUser",
                    description = "Autheris hybrid AD and Entra correlation attributes for SCIM Users",
                    schema = ScimSchemaUris.AutherisUserExtension,
                    attributes = new object[]
                    {
                        new { name = "objectSid", type = "string", multiValued = false, required = false, uniqueness = "server" },
                        new { name = "onPremisesImmutableId", type = "string", multiValued = false, required = false, uniqueness = "server" },
                        new { name = "userPrincipalName", type = "string", multiValued = false, required = false, uniqueness = "server" },
                        new { name = "entraObjectId", type = "string", multiValued = false, required = false, uniqueness = "server" }
                    }
                },
                new
                {
                    schemas = new[] { ScimSchemaUris.Schema },
                    id = ScimSchemaUris.AutherisGroupExtension,
                    name = "AutherisGroup",
                    description = "Autheris AD and Entra group correlation extension requiring canonical non-privileged objectSid",
                    schema = ScimSchemaUris.AutherisGroupExtension,
                    attributes = new object[]
                    {
                        new { name = "objectSid", type = "string", multiValued = false, required = true, uniqueness = "server" }
                    }
                }
            }
        };
        return Results.Ok(schemas);
    }

    internal static IResult GetResourceTypes(string realmId)
    {
        var resourceTypes = new
        {
            schemas = new[] { ScimSchemaUris.ListResponse },
            totalResults = 2,
            itemsPerPage = 2,
            startIndex = 1,
            Resources = new object[]
            {
                new
                {
                    schemas = new[] { ScimSchemaUris.ResourceType },
                    id = "User",
                    name = "User",
                    endpoint = $"/scim/v2/{realmId}/Users",
                    schema = ScimSchemaUris.CoreUser,
                    schemaExtensions = new object[]
                    {
                        new { schema = ScimSchemaUris.AutherisUserExtension, required = false }
                    }
                },
                new
                {
                    schemas = new[] { ScimSchemaUris.ResourceType },
                    id = "Group",
                    name = "Group",
                    endpoint = $"/scim/v2/{realmId}/Groups",
                    schema = ScimSchemaUris.CoreGroup,
                    schemaExtensions = new object[]
                    {
                        new { schema = ScimSchemaUris.AutherisGroupExtension, required = true }
                    }
                }
            }
        };
        return Results.Ok(resourceTypes);
    }
}
