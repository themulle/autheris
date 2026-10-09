namespace Autheris.Api.Endpoints;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Api.Middleware;
using Autheris.Api.Serialization;
using Autheris.Application.Common;
using Autheris.Application.Interfaces;
using Autheris.Application.OData.Interfaces;
using Autheris.Application.Serialization;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Options;
using Autheris.Extensions.OData;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

public static class ODataEndpoints
{
    /// <summary>API-15: Swagger UI outside Development needs an authenticated caller unless OpenSchema is enabled.</summary>
    internal static bool RequiresSwaggerChallenge(GatewayOptions gatewayOptions, IWebHostEnvironment env, HttpContext context) =>
        !gatewayOptions.IsOpenSchemaAllowed && !env.IsDevelopment() && context.User?.Identity?.IsAuthenticated != true;

    /// <summary>API-15: Evaluates Swagger UI authorization: challenge if unauthenticated outside Dev, forbid if authenticated non-admin.</summary>
    internal static IResult? CheckSwaggerAuth(GatewayOptions gatewayOptions, IWebHostEnvironment env, HttpContext context)
    {
        if (gatewayOptions.IsOpenSchemaAllowed || env.IsDevelopment())
        {
            return null;
        }
        if (context.User?.Identity?.IsAuthenticated != true)
        {
            return Results.Challenge();
        }
        if (!IsOpenApiAdmin(context.User))
        {
            return Results.Forbid();
        }
        return null;
    }

    /// <summary>API-15: Metadata and service document outside Development require an authenticated caller (challenge).</summary>
    internal static bool RequiresMetadataChallenge(GatewayOptions gatewayOptions, IWebHostEnvironment env, HttpContext context) =>
        !gatewayOptions.IsOpenSchemaAllowed && !env.IsDevelopment() && context.User?.Identity?.IsAuthenticated != true;

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
            var serviceRoot = $"{context.Request.Scheme}://{context.Request.Host}/odata/v4";
            var doc = await odataHandler.GetServiceDocumentAsync(serviceRoot, context.User, context.RequestAborted);
            return Results.Json(doc, contentType: "application/json;odata.metadata=minimal;charset=utf-8");
        }

        app.MapGet("/odata/v4", HandleServiceDocumentAsync).RequireAuthorization();

        app.MapGet("/odata/v4/$metadata", async (
            IODataHandler odataHandler,
            HttpContext context,
            IWebHostEnvironment env) =>
        {
            if (RequiresMetadataChallenge(gatewayOptions, env, context))
            {
                return Results.Challenge();
            }
            context.Response.Headers["OData-Version"] = "4.0";
            var xml = await odataHandler.GetMetadataCsdlAsync(context.User, context.RequestAborted);
            return Results.Content(xml, "application/xml;charset=utf-8");
        }).RequireAuthorization();

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
        }));

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
        }));

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
        }));

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
                return Results.NotFound(new { error = $"Table '{domain}.{schema}.{tableName}' not found in metadata repository." });
            }

            return Results.Content(json, "application/json;charset=utf-8");
        }));

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
        }));

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
        }));

        // Swagger UI: Seite unter /ui/swagger (analog /graphql fuer Nitro); /docs und /odata/v4/$swagger bleiben als Aliase.
        // Die UI-Assets werden aus dem Assembly ausgeliefert (kein CDN, offline-faehig).
        IResult ServeSwaggerUi(HttpContext context, IWebHostEnvironment env)
        {
            var authCheck = CheckSwaggerAuth(gatewayOptions, env, context);
            if (authCheck != null)
            {
                return authCheck;
            }
            var nonce = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
            context.Response.Headers.ContentSecurityPolicy = $"default-src 'self'; script-src 'self' 'nonce-{nonce}'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; object-src 'none'; base-uri 'self';";
            return Results.Content(GetSwaggerUiHtml(nonce), "text/html;charset=utf-8");
        }

        // SEC M-03: handlers perform their own (OpenSchema/Dev/authenticated) check
        app.MapGet("/ui/swagger", ServeSwaggerUi).AllowAnonymous();
        app.MapGet("/odata/v4/$swagger", ServeSwaggerUi).AllowAnonymous();
        app.MapGet("/docs", ServeSwaggerUi).AllowAnonymous();
        app.MapGet("/swagger", ServeSwaggerUi).AllowAnonymous();

        // Statische, oeffentliche Bibliotheksdateien (nur Allowlist, keine Pfadauflosung vom Client)
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
        }).AllowAnonymous();

        app.MapGet("/odata/v4/{domain}/{schema}/{tableName}", HandleEntitySetRequestAsync)
           .WithMetadata(new ParquetOutputSupportedMetadata())
           .RequireAuthorization();

        app.MapGet("/odata/v4/{domain}/{schema}/{tableName}/$count", HandleCountRequestAsync)
           .RequireAuthorization();

        app.MapGet("/odata/v4/{entitySetName}", HandleFlatEntitySetRequestAsync)
           .WithMetadata(new ParquetOutputSupportedMetadata())
           .RequireAuthorization();

        return app;
    }

    /// <summary>
    /// Befund 4a.2 & SR15-31: Support single-segment entity set name path (/odata/v4/lwetem_prod_md_crane) as an alias
    /// matching the OData 4.0 CSDL EntitySet name, strictly isolated to the caller's tenant catalog.
    /// </summary>
    internal static async Task<IResult> HandleFlatEntitySetRequestAsync(
        string entitySetName,
        IODataHandler odataHandler,
        ITableMetadataRepository metadataRepo,
        HttpContext context)
    {
        var tables = await metadataRepo.GetAllTablesAsync(context.RequestAborted).ConfigureAwait(false);
        var tenant = context.User.GetTenantId();
        var candidateTables = tables
            .Where(t => tenant == TenantId.LegacySingleTenant ||
                        string.Equals(t.Identifier.Domain, tenant.Value, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(t.Identifier.Domain, "default", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var matchingTables = candidateTables
            .Where(t => string.Equals(ODataCsdlGenerator.GetEntityName(t), entitySetName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matchingTables.Count != 1)
        {
            return Results.NotFound(new { error = new { code = "ResourceNotFound", message = $"The entity set '{entitySetName}' was not found." } });
        }

        var table = matchingTables[0];
        return await HandleEntitySetRequestAsync(
            table.Identifier.Domain,
            table.Identifier.Schema,
            table.Identifier.TableName,
            odataHandler,
            context).ConfigureAwait(false);
    }

    /// <summary>
    /// OData entity set query. F-DATA-01: with <c>Accept: application/vnd.apache.parquet</c> the governed rows of a
    /// successful result are returned as Apache Parquet; error results stay OData JSON.
    /// </summary>
    internal static bool IsOpenApiAdmin(System.Security.Claims.ClaimsPrincipal? user)
        => EndpointSecurity.IsCanonicalClusterAdmin(user) || EndpointSecurity.IsGlobalGovernanceAdmin(user);

    internal static async Task<IResult> HandleEntitySetRequestAsync(
        string domain,
        string schema,
        string tableName,
        IODataHandler odataHandler,
        HttpContext context)
    {
        context.Response.Headers["OData-Version"] = "4.0";
        var serviceRoot = $"{context.Request.Scheme}://{context.Request.Host}/odata/v4";
        var tableId = new TableIdentifier(domain, schema, tableName);

        var optionError = ValidateSystemQueryOptions(context.Request.Query);
        if (optionError != null)
        {
            return optionError;
        }

        int? top = null;
        if (context.Request.Query.TryGetValue("$top", out var topVal))
        {
            if (!int.TryParse(topVal, out var t) || t < 0)
            {
                return Results.Json(
                    new { error = new { code = "InvalidQueryOption", message = "The query parameter '$top' must be a non-negative integer." } },
                    statusCode: StatusCodes.Status400BadRequest,
                    contentType: "application/json;odata.metadata=minimal;charset=utf-8"
                );
            }
            top = t;
        }

        int? skip = null;
        if (context.Request.Query.TryGetValue("$skip", out var skipVal))
        {
            if (!long.TryParse(skipVal, out var s) || s < 0)
            {
                return Results.Json(
                    new { error = new { code = "InvalidQueryOption", message = "The query parameter '$skip' must be a non-negative integer." } },
                    statusCode: StatusCodes.Status400BadRequest,
                    contentType: "application/json;odata.metadata=minimal;charset=utf-8"
                );
            }
            if (s > ODataHandler.MaxSkip)
            {
                return Results.Json(
                    new { error = new { code = "InvalidQueryOption", message = $"The query parameter '$skip' must not exceed {ODataHandler.MaxSkip}." } },
                    statusCode: StatusCodes.Status400BadRequest,
                    contentType: "application/json;odata.metadata=minimal;charset=utf-8"
                );
            }
            skip = (int)s;
        }

        string? select = context.Request.Query["$select"].FirstOrDefault();
        string? orderBy = context.Request.Query.TryGetValue("$orderby", out var orderByVal) ? orderByVal.ToString() : null;
        string? filter = context.Request.Query.TryGetValue("$filter", out var filterVal) ? filterVal.ToString() : null;
        bool includeCount = false;
        if (context.Request.Query.TryGetValue("$count", out var countVal))
        {
            if (!bool.TryParse(countVal, out includeCount))
            {
                return ODataError(StatusCodes.Status400BadRequest, "InvalidQueryOption", "The query parameter '$count' must be 'true' or 'false'.");
            }
        }

        // Befund 1.2: Strict Content Negotiation. If Accept header requests non-OData formats (CSV, NDJSON, etc.), reject with 406.
        if (context.Request.Headers.Accept.Count > 0)
        {
            var acceptEval = ParquetContentNegotiation.Evaluate(context.Request);
            if (!acceptEval.ParquetPreferred && !acceptEval.HasJsonAlternative)
            {
                return Results.StatusCode(StatusCodes.Status406NotAcceptable);
            }
        }

        // F-DATA-01 / Befund 1.2: a Parquet request (via Accept header or $format=parquet) that cannot be served is rejected before the query is executed
        bool parquetRequested = ParquetContentNegotiation.IsParquetRequested(context.Request) ||
            string.Equals(context.Request.Query["$format"], "parquet", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(context.Request.Query["$format"], "application/vnd.apache.parquet", StringComparison.OrdinalIgnoreCase);
        IParquetExportService? parquetService = null;
        if (parquetRequested)
        {
            parquetService = context.RequestServices?.GetService<IParquetExportService>();
            if (await ParquetResponseWriter.TryRejectUnavailableAsync(context, parquetService, context.RequestAborted))
            {
                return Results.Empty;
            }
        }

        var headers = context.Request.Headers.ToDictionary(h => h.Key, h => h.Value.Select(v => v ?? string.Empty).ToArray());

        var result = await odataHandler.ExecuteEntitySetQueryAsync(
            principal: context.User,
            serviceRootUrl: serviceRoot,
            table: tableId,
            top: top,
            skip: skip,
            select: select,
            includeCount: includeCount,
            headers: headers,
            orderBy: orderBy,
            filter: filter,
            ct: context.RequestAborted
        );

        if (result.RetryAfterSeconds is int retryAfter)
        {
            context.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
        }

        if (parquetRequested && parquetService != null && result.StatusCode == StatusCodes.Status200OK)
        {
            var rows = ExtractEntitySetRows(result.Payload);
            await ParquetResponseWriter.WriteAsync(context, parquetService, tableName, rows, null, context.RequestAborted);
            return Results.Empty;
        }

        return Results.Json(result.Payload, statusCode: result.StatusCode, contentType: "application/json;odata.metadata=minimal;charset=utf-8");
    }

    /// <summary>
    /// 4a.3: <c>/$count</c> answers the number of rows the caller may read (same row filter as the entity set) as plain
    /// text, as OData 4.0 specifies for the count segment.
    /// </summary>
    internal static async Task<IResult> HandleCountRequestAsync(
        string domain,
        string schema,
        string tableName,
        IODataHandler odataHandler,
        HttpContext context)
    {
        context.Response.Headers["OData-Version"] = "4.0";
        foreach (var (key, _) in context.Request.Query)
        {
            if (key.StartsWith('$'))
            {
                return ODataError(StatusCodes.Status400BadRequest, "InvalidQueryOption", "The '/$count' segment takes no query options.");
            }
        }

        var serviceRoot = $"{context.Request.Scheme}://{context.Request.Host}/odata/v4";
        var headers = context.Request.Headers.ToDictionary(h => h.Key, h => h.Value.Select(v => v ?? string.Empty).ToArray());
        var result = await odataHandler.ExecuteEntitySetQueryAsync(
            principal: context.User,
            serviceRootUrl: serviceRoot,
            table: new TableIdentifier(domain, schema, tableName),
            top: 1,
            skip: null,
            select: null,
            includeCount: true,
            headers: headers,
            ct: context.RequestAborted).ConfigureAwait(false);

        if (result.RetryAfterSeconds is int retryAfter)
        {
            context.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
        }

        if (result.StatusCode != StatusCodes.Status200OK ||
            result.Payload is not IReadOnlyDictionary<string, object?> payload ||
            !payload.TryGetValue("@odata.count", out var count) || count == null)
        {
            var status = result.StatusCode == StatusCodes.Status200OK ? StatusCodes.Status501NotImplemented : result.StatusCode;
            return Results.Json(result.Payload, statusCode: status, contentType: "application/json;odata.metadata=minimal;charset=utf-8");
        }

        return Results.Text(Convert.ToString(count, CultureInfo.InvariantCulture), "text/plain; charset=utf-8");
    }

    /// <summary>System query options the entity set endpoint implements.</summary>
    private static readonly FrozenSet<string> SupportedSystemQueryOptions =
        new[] { "$top", "$skip", "$select", "$count", "$orderby", "$filter", "$format" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>OData v4 system query options that exist but are not implemented yet (answered with 501, never ignored).</summary>
    private static readonly FrozenSet<string> NotImplementedSystemQueryOptions =
        new[] { "$expand", "$search", "$apply", "$compute", "$skiptoken", "$deltatoken", "$levels", "$index", "$schemaversion", "$id" }
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// O3/O4 (docs/plans/rls-subquery-in-strategy.md): a system query option is either applied or rejected. Ignoring
    /// $filter would hand the client unfiltered rows it believes to be filtered. Custom options (without '$') pass.
    /// </summary>
    internal static IResult? ValidateSystemQueryOptions(IQueryCollection query)
    {
        foreach (var (key, values) in query)
        {
            if (!key.StartsWith('$'))
            {
                continue;
            }

            var shownKey = key.Length > 64 ? key[..64] : key;
            if (NotImplementedSystemQueryOptions.Contains(key))
            {
                return ODataError(StatusCodes.Status501NotImplemented, "NotImplemented", $"The query option '{shownKey}' is not supported by this service.");
            }
            if (!SupportedSystemQueryOptions.Contains(key))
            {
                return ODataError(StatusCodes.Status400BadRequest, "InvalidQueryOption", $"The query option '{shownKey}' is unknown.");
            }
            if (values.Count > 1)
            {
                return ODataError(StatusCodes.Status400BadRequest, "InvalidQueryOption", $"The query option '{shownKey}' must not be specified more than once.");
            }
            if (string.Equals(key, "$format", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(values.ToString(), "json", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(values.ToString(), "parquet", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(values.ToString(), "application/vnd.apache.parquet", StringComparison.OrdinalIgnoreCase) &&
                !values.ToString().StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
            {
                return ODataError(StatusCodes.Status400BadRequest, "InvalidQueryOption", "The query option '$format' supports 'json' and 'parquet' only.");
            }
        }

        return null;
    }

    private static IResult ODataError(int statusCode, string code, string message) =>
        Results.Json(
            new { error = new { code, message } },
            statusCode: statusCode,
            contentType: "application/json;odata.metadata=minimal;charset=utf-8");

    /// <summary>
    /// Extracts the governed rows ("value") of an OData entity set payload without @odata annotations.
    /// </summary>
    internal static IReadOnlyList<IReadOnlyDictionary<string, object?>> ExtractEntitySetRows(object? payload)
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        if (payload is null)
        {
            return rows;
        }

        if (payload is IReadOnlyDictionary<string, object?> dictionary &&
            dictionary.TryGetValue("value", out var value) &&
            value is IEnumerable<IReadOnlyDictionary<string, object?>> typedRows)
        {
            foreach (var row in typedRows)
            {
                rows.Add(StripODataAnnotations(row));
            }

            return rows;
        }

        // Fallback for other payload shapes: read "value" from the JSON representation the JSON path would send.
        var element = JsonSerializer.SerializeToElement(payload, payload.GetType());
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty("value", out var valueElement) &&
            valueElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in valueElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var row = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var property in item.EnumerateObject())
                {
                    if (!IsODataAnnotation(property.Name))
                    {
                        row[property.Name] = property.Value;
                    }
                }
                rows.Add(row);
            }
        }

        return rows;
    }

    private static IReadOnlyDictionary<string, object?> StripODataAnnotations(IReadOnlyDictionary<string, object?> row)
    {
        var hasAnnotation = false;
        foreach (var key in row.Keys)
        {
            if (IsODataAnnotation(key))
            {
                hasAnnotation = true;
                break;
            }
        }

        if (!hasAnnotation)
        {
            return row;
        }

        var stripped = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, val) in row)
        {
            if (!IsODataAnnotation(key))
            {
                stripped[key] = val;
            }
        }

        return stripped;
    }

    private static bool IsODataAnnotation(string key) => key.Contains("@odata.", StringComparison.Ordinal);

    private const string SwaggerUiVersion = "5.18.2";

    private static readonly Dictionary<string, string> SwaggerUiAssets = new(StringComparer.Ordinal)
    {
        ["swagger-ui.css"] = "text/css; charset=utf-8",
        ["swagger-ui-bundle.js"] = "text/javascript; charset=utf-8",
        ["swagger-ui-standalone-preset.js"] = "text/javascript; charset=utf-8",
        ["favicon-32x32.png"] = "image/png",
    };

    private static string GetSwaggerUiHtml(string nonce) => $$"""
    <!DOCTYPE html>
    <html lang="en">
    <head>
      <meta charset="utf-8" />
      <meta name="viewport" content="width=device-width, initial-scale=1" />
      <title>Autheris - OpenAPI 3.1 & OData Explorer</title>
      <link rel="icon" type="image/png" href="/ui/swagger/assets/favicon-32x32.png" />
      <link rel="stylesheet" href="/ui/swagger/assets/swagger-ui.css?v={{SwaggerUiVersion}}" />
      <style>
        .swagger-ui .topbar { background-color: #1e293b; padding: 10px 0; }
        .swagger-ui .topbar .download-url-wrapper { display: flex; align-items: center; gap: 8px; }
        .swagger-ui .topbar .download-url-wrapper input[type=text] { border-radius: 4px; padding: 6px 10px; }
      </style>
    </head>
    <body>
    <div id="swagger-ui"></div>
    <script src="/ui/swagger/assets/swagger-ui-bundle.js?v={{SwaggerUiVersion}}"></script>
    <script src="/ui/swagger/assets/swagger-ui-standalone-preset.js?v={{SwaggerUiVersion}}"></script>
    <script nonce="{{nonce}}">
      window.onload = async () => {
        const params = new URLSearchParams(window.location.search);
        const targetDomain = params.get('domain');
        const customUrl = params.get('url');

        let specUrls = [
          { url: '/odata/v4/$openapi', name: 'All Domains (Monolithic)' },
          { url: '/odata/v4/$openapi?mode=modular', name: 'All Domains (Modular $ref)' },
          { url: '/api/v1/queries/openapi.json', name: 'Declarative SQL Queries' }
        ];
        let primaryName = 'All Domains (Monolithic)';

        try {
          const res = await fetch('/odata/v4/$openapi/index', { credentials: 'same-origin' });
          if (res.ok) {
            const data = await res.json();
            if (data && Array.isArray(data.apis)) {
              specUrls = data.apis.map(a => ({ url: a.url, name: a.name }));
            }
          }
        } catch (e) {
          // Graceful fallback to default URLs if unauthenticated or network error
        }

        if (targetDomain) {
          const match = specUrls.find(s => s.name.toLowerCase().includes(targetDomain.toLowerCase()) || s.url.toLowerCase().includes(`/${targetDomain.toLowerCase()}/`));
          if (match) {
            primaryName = match.name;
          }
        } else if (customUrl) {
          const match = specUrls.find(s => s.url === customUrl);
          if (match) {
            primaryName = match.name;
          } else {
            specUrls.unshift({ url: customUrl, name: 'Custom Specification' });
            primaryName = 'Custom Specification';
          }
        }

        window.ui = SwaggerUIBundle({
          urls: specUrls,
          "urls.primaryName": primaryName,
          dom_id: '#swagger-ui',
          presets: [
            SwaggerUIBundle.presets.apis,
            SwaggerUIStandalonePreset
          ],
          layout: "StandaloneLayout",
          deepLinking: true,
          displayRequestDuration: true
        });
      };
    </script>
    </body>
    </html>
    """;
}

