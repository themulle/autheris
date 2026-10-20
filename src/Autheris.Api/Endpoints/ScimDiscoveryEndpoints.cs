namespace Autheris.Api.Endpoints;

using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

public static class ScimDiscoveryEndpoints
{
    public static IEndpointRouteBuilder MapScimDiscoveryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/scim/v2/{realmId}");

        group.MapGet("/ServiceProviderConfig", (string realmId) =>
        {
            var config = new
            {
                schemas = new[] { "urn:ietf:params:scim:schemas:core:2.0:ServiceProviderConfig" },
                documentationUri = "https://tools.ietf.org/html/rfc7644",
                patch = new { supported = true },
                bulk = new { supported = false, maxOperations = 0, maxPayloadSize = 0 },
                filter = new { supported = true, maxResults = 1000 },
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
        });

        group.MapGet("/Schemas", (string realmId) =>
        {
            var schemas = new
            {
                schemas = new[] { "urn:ietf:params:scim:api:messages:2.0:ListResponse" },
                totalResults = 2,
                itemsPerPage = 2,
                startIndex = 1,
                Resources = new object[]
                {
                    new
                    {
                        id = "urn:ietf:params:scim:schemas:core:2.0:User",
                        name = "User",
                        description = "User Account",
                        schema = "urn:ietf:params:scim:schemas:core:2.0:User"
                    },
                    new
                    {
                        id = "urn:ietf:params:scim:schemas:core:2.0:Group",
                        name = "Group",
                        description = "Group",
                        schema = "urn:ietf:params:scim:schemas:core:2.0:Group"
                    }
                }
            };
            return Results.Ok(schemas);
        });

        group.MapGet("/ResourceTypes", (string realmId) =>
        {
            var resourceTypes = new
            {
                schemas = new[] { "urn:ietf:params:scim:api:messages:2.0:ListResponse" },
                totalResults = 2,
                itemsPerPage = 2,
                startIndex = 1,
                Resources = new object[]
                {
                    new
                    {
                        schemas = new[] { "urn:ietf:params:scim:schemas:core:2.0:ResourceType" },
                        id = "User",
                        name = "User",
                        endpoint = $"/scim/v2/{realmId}/Users",
                        schema = "urn:ietf:params:scim:schemas:core:2.0:User"
                    },
                    new
                    {
                        schemas = new[] { "urn:ietf:params:scim:schemas:core:2.0:ResourceType" },
                        id = "Group",
                        name = "Group",
                        endpoint = $"/scim/v2/{realmId}/Groups",
                        schema = "urn:ietf:params:scim:schemas:core:2.0:Group"
                    }
                }
            };
            return Results.Ok(resourceTypes);
        });

        return app;
    }
}
