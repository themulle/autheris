namespace Autheris.Api.Endpoints;

using System;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Autheris.Api.Extensions;
using Autheris.Api.Serialization;
using Autheris.Application.Interfaces;
using Autheris.Application.OData.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Autheris.Extensions.OData;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

public static partial class ODataEndpoints
{
    public static IEndpointRouteBuilder MapODataEndpoints(this IEndpointRouteBuilder app, GatewayOptions gatewayOptions)
    {
        // OData v4 / Power BI & Excel Direct Adapter Endpoints
        async Task<IResult> HandleServiceDocumentAsync(IODataHandler odataHandler, HttpContext context, IWebHostEnvironment env)
        {
            if (RequiresMetadataChallenge(gatewayOptions, env, context))
            {
                return Results.Challenge();
            }
            context.Response.Headers["OData-Version"] = "4.0";
            var serviceRoot = ResolveServiceRoot(context);
            var doc = await odataHandler.GetServiceDocumentAsync(serviceRoot, context.User, context.RequestAborted);
            return Results.Json(doc, contentType: "application/json;odata.metadata=minimal;charset=utf-8");
        }

        async Task<IResult> HandleMetadataAsync(
            IODataHandler odataHandler,
            HttpContext context,
            IWebHostEnvironment env)
        {
            if (RequiresMetadataChallenge(gatewayOptions, env, context))
            {
                return Results.Challenge();
            }
            context.Response.Headers["OData-Version"] = "4.0";
            var xml = await odataHandler.GetMetadataCsdlAsync(context.User, context.RequestAborted);
            return Results.Content(xml, "application/xml;charset=utf-8");
        }

        app.MapGet("/odata", HandleServiceDocumentAsync)
            .RequireAuthorization()
            .WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

        app.MapGet("/odata/v4", HandleServiceDocumentAsync)
            .RequireAuthorization()
            .WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

        app.MapGet("/odata/$metadata", HandleMetadataAsync)
            .RequireAuthorization()
            .WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

        app.MapGet("/odata/v4/$metadata", HandleMetadataAsync)
            .RequireAuthorization()
            .WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

        bool IsOpenApiAuthorized(HttpContext context)
        {
            if (gatewayOptions.IsOpenSchemaAllowed)
            {
                return true;
            }
            if (context.User?.Identity?.IsAuthenticated != true)
            {
                return false;
            }
            // G3: the generated spec lists the schema of ALL tenants and is not filtered per caller, so it is
            // restricted to global administrators. Tenant-scoped roles (DataOwner, SchemaAdmin, CatalogReader)
            // use the tenant-filtered $metadata / service document instead.
            return IsOpenApiAdmin(context.User);
        }

        IResult? CheckOpenApiAuth(HttpContext context)
        {
            if (IsOpenApiAuthorized(context))
            {
                return null;
            }
            // API-15: challenge (WWW-Authenticate) instead of a bare 401, so Kerberos/Negotiate clients can authenticate.
            return context.User?.Identity?.IsAuthenticated == true ? Results.Forbid() : Results.Challenge();
        }

        RouteHandlerBuilder ConfigureOpenApiAuth(RouteHandlerBuilder builder)
        {
            if (!gatewayOptions.IsOpenSchemaAllowed)
            {
                builder.RequireAuthorization();
            }
            else
            {
                // SEC M-03: explicit opt-out of the authenticated-user fallback policy (OpenSchema docs only)
                builder.AllowAnonymous();
            }
            return builder;
        }

        // Dynamic OpenAPI 3.1 & Swagger UI Explorer (F-API-03)
        ConfigureOpenApiAuth(app.MapGet("/odata/v4/$openapi", async (
            IDynamicOpenApiGenerator generator,
            IOpenApiCacheManager cacheManager,
            HttpContext context) =>
        {
            var authCheck = CheckOpenApiAuth(context);
            if (authCheck != null) return authCheck;

            var format = context.Request.Query["format"].ToString();
            var accept = context.Request.Headers.Accept.ToString();
            bool isYaml = string.Equals(format, "yaml", StringComparison.OrdinalIgnoreCase) ||
                          accept.Contains("application/yaml", StringComparison.OrdinalIgnoreCase);

            var mode = context.Request.Query["mode"].ToString();
            bool isModular = string.Equals(mode, "modular", StringComparison.OrdinalIgnoreCase);

            var bytes = await cacheManager.GetOrAddAsync(
                domainScope: null,
                isYaml: isYaml,
                isModular: isModular,
                factory: ct => isYaml
                    ? generator.GenerateOpenApiYamlAsync(null, isModular, ct)
                    : generator.GenerateOpenApiJsonAsync(null, isModular, ct),
                ct: context.RequestAborted);

            var contentType = isYaml ? "application/yaml;charset=utf-8" : "application/json;charset=utf-8";
            return Results.Bytes(bytes, contentType: contentType);
        })).WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

        // OpenAPI Catalog Index Endpoint (Lists all available domain slices & API specs)
        ConfigureOpenApiAuth(app.MapGet("/odata/v4/$openapi/index", async (
            IDynamicOpenApiGenerator generator,
            HttpContext context) =>
        {
            var authCheck = CheckOpenApiAuth(context);
            if (authCheck != null) return authCheck;

            // The generator appends the configured OData server prefix ("/odata/v4") itself, so it must only get the origin.
            var origin = $"{context.Request.Scheme}://{context.Request.Host}";
            var indexDoc = await generator.GetIndexDocumentAsync(origin, context.RequestAborted);
            return Results.Json(indexDoc, contentType: "application/json;charset=utf-8");
        })).WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

        ConfigureOpenApiAuth(app.MapGet("/api/v1/openapi/index", async (
            IDynamicOpenApiGenerator generator,
            HttpContext context) =>
        {
            var authCheck = CheckOpenApiAuth(context);
            if (authCheck != null) return authCheck;

            // The generator appends the configured OData server prefix ("/odata/v4") itself, so it must only get the origin.
            var origin = $"{context.Request.Scheme}://{context.Request.Host}";
            var indexDoc = await generator.GetIndexDocumentAsync(origin, context.RequestAborted);
            return Results.Json(indexDoc, contentType: "application/json;charset=utf-8");
        })).WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

        // Isolated Entity Schema Endpoint ($ref target for modular OpenAPI specifications)
        ConfigureOpenApiAuth(app.MapGet("/odata/v4/$openapi/schemas/{domain}/{schema}/{tableName}", async (
            string domain,
            string schema,
            string tableName,
            IDynamicOpenApiGenerator generator,
            HttpContext context) =>
        {
            var authCheck = CheckOpenApiAuth(context);
            if (authCheck != null) return authCheck;

            var tableId = new TableIdentifier(domain, schema, tableName);
            var json = await generator.GenerateEntitySchemaJsonAsync(tableId, context.RequestAborted);
            if (json == null)
            {
                // OData v4 error object (protocol allowlist, NL-SC-29): fixed message, no table-name echo.
                return Results.Json(
                    new { error = new { code = "NotFound", message = "The requested entity set was not found." } },
                    statusCode: StatusCodes.Status404NotFound);
            }

            return Results.Content(json, "application/json;charset=utf-8");
        })).WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

        ConfigureOpenApiAuth(app.MapGet("/odata/v4/{domain}/openapi.json", async (
            string domain,
            IDynamicOpenApiGenerator generator,
            IOpenApiCacheManager cacheManager,
            HttpContext context) =>
        {
            var authCheck = CheckOpenApiAuth(context);
            if (authCheck != null) return authCheck;

            var mode = context.Request.Query["mode"].ToString();
            bool isModular = string.Equals(mode, "modular", StringComparison.OrdinalIgnoreCase);

            var bytes = await cacheManager.GetOrAddAsync(
                domainScope: domain,
                isYaml: false,
                isModular: isModular,
                factory: ct => generator.GenerateOpenApiJsonAsync(domain, isModular, ct),
                ct: context.RequestAborted);

            return Results.Bytes(bytes, contentType: "application/json;charset=utf-8");
        })).WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

        ConfigureOpenApiAuth(app.MapGet("/odata/v4/{domain}/openapi.yaml", async (
            string domain,
            IDynamicOpenApiGenerator generator,
            IOpenApiCacheManager cacheManager,
            HttpContext context) =>
        {
            var authCheck = CheckOpenApiAuth(context);
            if (authCheck != null) return authCheck;

            var mode = context.Request.Query["mode"].ToString();
            bool isModular = string.Equals(mode, "modular", StringComparison.OrdinalIgnoreCase);

            var bytes = await cacheManager.GetOrAddAsync(
                domainScope: domain,
                isYaml: true,
                isModular: isModular,
                factory: ct => generator.GenerateOpenApiYamlAsync(domain, isModular, ct),
                ct: context.RequestAborted);

            return Results.Bytes(bytes, contentType: "application/yaml;charset=utf-8");
        })).WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

        // Swagger UI: page under /ui/swagger (analogous to /graphql for Nitro); /docs and /odata/v4/$swagger remain as aliases.
        // The UI assets are served from the assembly (no CDN, offline-capable).
        IResult ServeSwaggerUi(HttpContext context, IWebHostEnvironment env)
        {
            var authCheck = CheckSwaggerAuth(gatewayOptions, env, context);
            if (authCheck != null)
            {
                return authCheck;
            }
            var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
            context.Response.Headers.ContentSecurityPolicy = $"default-src 'self'; script-src 'self' 'nonce-{nonce}'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; object-src 'none'; base-uri 'self';";
            return Results.Content(GetSwaggerUiHtml(nonce), "text/html;charset=utf-8");
        }

        // SEC M-03: handlers perform their own (OpenSchema/Dev/authenticated) check
        app.MapGet("/ui/swagger", ServeSwaggerUi).AllowAnonymous().WithAuditExemption("Developer swagger documentation");
        app.MapGet("/odata/v4/$swagger", ServeSwaggerUi).AllowAnonymous().WithAuditExemption("Developer swagger documentation");
        app.MapGet("/docs", ServeSwaggerUi).AllowAnonymous().WithAuditExemption("Developer swagger documentation");
        app.MapGet("/swagger", ServeSwaggerUi).AllowAnonymous().WithAuditExemption("Developer swagger documentation");

        // Static, public library files (allowlist only, no path resolution from client)
        app.MapGet("/ui/swagger/assets/{file}", (string file) =>
        {
            if (!SwaggerUiAssets.TryGetValue(file, out var contentType))
            {
                return Results.NotFound();
            }
            var stream = typeof(ODataEndpoints).Assembly.GetManifestResourceStream("swagger-ui/" + file);
            if (stream is null)
            {
                return Results.NotFound();
            }
            return Results.Stream(stream, contentType, enableRangeProcessing: false);
        }).AllowAnonymous().WithAuditExemption("Swagger UI static asset");

        app.MapGet("/odata/{domain}/{schema}/{tableName}", HandleEntitySetRequestAsync)
           .WithMetadata(new ParquetOutputSupportedMetadata())
           .RequireAuthorization()
           .WithAudit(AuditLevel.Full, AuditEventTypes.TableQuery);

        app.MapGet("/odata/v4/{domain}/{schema}/{tableName}", HandleEntitySetRequestAsync)
           .WithMetadata(new ParquetOutputSupportedMetadata())
           .RequireAuthorization()
           .WithAudit(AuditLevel.Full, AuditEventTypes.TableQuery);

        app.MapGet("/odata/{domain}/{schema}/{tableName}/$count", HandleCountRequestAsync)
           .RequireAuthorization()
           .WithAudit(AuditLevel.Full, AuditEventTypes.TableQuery);

        app.MapGet("/odata/v4/{domain}/{schema}/{tableName}/$count", HandleCountRequestAsync)
           .RequireAuthorization()
           .WithAudit(AuditLevel.Full, AuditEventTypes.TableQuery);

        app.MapGet("/odata/{entitySetName}", HandleFlatEntitySetRequestAsync)
           .WithMetadata(new ParquetOutputSupportedMetadata())
           .RequireAuthorization()
           .WithAudit(AuditLevel.Full, AuditEventTypes.TableQuery);

        app.MapGet("/odata/v4/{entitySetName}", HandleFlatEntitySetRequestAsync)
           .WithMetadata(new ParquetOutputSupportedMetadata())
           .RequireAuthorization()
           .WithAudit(AuditLevel.Full, AuditEventTypes.TableQuery);

        return app;
    }
}
