namespace Autheris.Api.Endpoints;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Serialization;
using Autheris.Application.Serialization;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.SqlEndpoints.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public static class SqlEndpointRoutes
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static IEndpointRouteBuilder MapSqlEndpoints(this IEndpointRouteBuilder app, GatewayOptions? gatewayOptions = null)
    {
        var group = app.MapGroup("/api/v1/queries");

        group.MapGet("/", HandleListEndpoints)
             .WithName("ListSqlEndpoints")
             .RequireAuthorization();

        var openApiEndpoint = group.MapGet("/openapi.json", HandleOpenApiSpec)
             .WithName("GetSqlEndpointsOpenApiSpec");

        if (gatewayOptions?.IsOpenSchemaAllowed != true)
        {
            openApiEndpoint.RequireAuthorization();
        }
        else
        {
            // SEC M-03: explicit opt-out of the authenticated-user fallback policy (OpenSchema docs only)
            openApiEndpoint.AllowAnonymous();
        }

        group.MapGet("/{name}", HandleGetEndpoint)
             .WithName("ExecuteSqlEndpointGet")
             .WithMetadata(new ParquetOutputSupportedMetadata())
             .RequireAuthorization();

        group.MapPost("/{name}", HandlePostEndpoint)
             .WithName("ExecuteSqlEndpointPost")
             .WithMetadata(new ParquetOutputSupportedMetadata())
             .RequireAuthorization();

        return app;
    }

    private static IResult HandleListEndpoints(ISqlEndpointRegistry registry, HttpContext context)
    {
        var endpoints = registry.GetAll();
        if (IsListAdmin(context.User))
        {
            return Results.Ok(endpoints);
        }

        return Results.Ok(ToPublicListing(endpoints));
    }

    // G3: RawSql, DataSource and ReferencedTables are internal schema details; only admins see them.
    internal static bool IsListAdmin(System.Security.Claims.ClaimsPrincipal? user)
        => EndpointSecurity.IsCanonicalClusterAdmin(user) || EndpointSecurity.IsGlobalGovernanceAdmin(user);

    internal static IReadOnlyList<object> ToPublicListing(IEnumerable<Autheris.Domain.Model.SqlEndpointDefinition> endpoints)
        => endpoints.Select(e => (object)new
        {
            name = e.Name,
            summary = e.Summary,
            parameters = e.Parameters.Select(p => new
            {
                name = p.Name,
                type = p.ClrType.Name,
                isRequired = p.IsRequired,
                description = p.Description
            }).ToList()
        }).ToList();

    private static IResult HandleOpenApiSpec(ISqlEndpointRegistry registry)
    {
        var endpoints = registry.GetAll();
        var paths = new Dictionary<string, object>();

        foreach (var ep in endpoints)
        {
            var queryParams = new List<object>();
            foreach (var p in ep.Parameters)
            {
                var schema = new Dictionary<string, object>
                {
                    ["type"] = GetJsonType(p.ClrType)
                };
                if (p.DefaultValue != null)
                {
                    schema["default"] = p.DefaultValue;
                }
                if (p.ClrType == typeof(DateTimeOffset) || p.ClrType == typeof(DateTime))
                {
                    schema["format"] = "date-time";
                }

                queryParams.Add(new Dictionary<string, object?>
                {
                    ["name"] = p.Name,
                    ["in"] = "query",
                    ["required"] = p.IsRequired,
                    ["description"] = p.Description ?? $"Parameter {p.Name}",
                    ["schema"] = schema
                });
            }

            var properties = new Dictionary<string, object>();
            foreach (var proj in ep.Projections)
            {
                properties[proj.ColumnName] = new Dictionary<string, object>
                {
                    ["type"] = "string"
                };
            }

            var operation = new Dictionary<string, object>
            {
                ["summary"] = ep.Summary,
                ["description"] = !string.IsNullOrWhiteSpace(ep.Summary) ? ep.Summary : $"Executes declarative SQL query '{ep.Name}'",
                ["operationId"] = ep.Name,
                ["parameters"] = queryParams,
                ["responses"] = new Dictionary<string, object>
                {
                    ["200"] = new Dictionary<string, object>
                    {
                        ["description"] = "Successful query response",
                        ["content"] = new Dictionary<string, object>
                        {
                            ["application/json"] = new Dictionary<string, object>
                            {
                                ["schema"] = new Dictionary<string, object>
                                {
                                    ["type"] = "array",
                                    ["items"] = new Dictionary<string, object>
                                    {
                                        ["type"] = "object",
                                        ["properties"] = properties
                                    }
                                }
                            }
                        }
                    },
                    ["400"] = new Dictionary<string, object> { ["description"] = "Invalid parameters" },
                    ["403"] = new Dictionary<string, object> { ["description"] = "Access forbidden by ABAC policy" },
                    ["404"] = new Dictionary<string, object> { ["description"] = "SQL query not found" }
                }
            };

            paths[$"/api/v1/queries/{ep.Name}"] = new Dictionary<string, object>
            {
                ["get"] = operation
            };
        }

        var openApiDoc = new Dictionary<string, object>
        {
            ["openapi"] = "3.0.1",
            ["info"] = new Dictionary<string, object>
            {
                ["title"] = "Autheris Declarative SQL Endpoints API",
                ["version"] = "v1",
                ["description"] = "Auto-generated OpenAPI / Swagger documentation from declarative SQL endpoints with AST-inferred parameter types and zero-trust governance."
            },
            ["paths"] = paths
        };

        return Results.Ok(openApiDoc);
    }

    private static string GetJsonType(Type clrType)
    {
        if (clrType == typeof(int) || clrType == typeof(long) || clrType == typeof(short)) return "integer";
        if (clrType == typeof(decimal) || clrType == typeof(double) || clrType == typeof(float)) return "number";
        if (clrType == typeof(bool)) return "boolean";
        return "string";
    }

    internal static async Task HandleGetEndpoint(
        string name,
        HttpContext httpContext,
        ISqlEndpointExecutionService executionService,
        IOptions<GatewayOptions> gatewayOptions,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("Autheris.Api.SqlEndpoints");
        var ct = httpContext.RequestAborted;

        // F-DATA-01: reject a Parquet request that cannot be served before any query is executed
        var parquet = ResolveParquetService(httpContext);
        if (parquet.Requested && await ParquetResponseWriter.TryRejectUnavailableAsync(httpContext, parquet.Service, ct).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            var rawInputs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in httpContext.Request.Query)
            {
                rawInputs[k] = v.ToString();
            }

            var tenantId = EndpointSecurity.GetRequestTenant(httpContext);
            var result = await executionService.ExecuteEndpointAsync(
                name,
                rawInputs,
                httpContext.User,
                tenantId,
                ct).ConfigureAwait(false);

            await WriteResultAsync(httpContext, name, result, parquet, ct).ConfigureAwait(false);
        }
        catch (KeyNotFoundException knf)
        {
            httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
            await httpContext.Response.WriteAsJsonAsync(new { error = knf.Message }, ct).ConfigureAwait(false);
        }
        catch (ArgumentException argEx)
        {
            httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            await httpContext.Response.WriteAsJsonAsync(new { error = argEx.Message }, ct).ConfigureAwait(false);
        }
        catch (SecurityException secEx)
        {
            logger.LogWarning(secEx, "Security policy violation when executing endpoint '{EndpointName}'.", name);
            httpContext.Response.StatusCode = StatusCodes.Status403Forbidden;
            await httpContext.Response.WriteAsJsonAsync(new { error = secEx.Message }, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error executing SQL endpoint '{EndpointName}'.", name);
            httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await httpContext.Response.WriteAsJsonAsync(new { error = "An internal error occurred during query execution." }, ct).ConfigureAwait(false);
        }
    }

    internal static async Task HandlePostEndpoint(
        string name,
        HttpContext httpContext,
        ISqlEndpointExecutionService executionService,
        IOptions<GatewayOptions> gatewayOptions,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("Autheris.Api.SqlEndpoints");
        var ct = httpContext.RequestAborted;

        if (httpContext.Request.ContentLength > 2 * 1024 * 1024)
        {
            httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            await httpContext.Response.WriteAsJsonAsync(new { error = "Request body exceeds maximum size limit (2 MB)." }, ct).ConfigureAwait(false);
            return;
        }

        // F-DATA-01: reject a Parquet request that cannot be served before any query is executed
        var parquet = ResolveParquetService(httpContext);
        if (parquet.Requested && await ParquetResponseWriter.TryRejectUnavailableAsync(httpContext, parquet.Service, ct).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            Dictionary<string, object?>? rawInputs = null;
            if (httpContext.Request.ContentLength > 0)
            {
                using var reader = new StreamReader(httpContext.Request.Body);
                string body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(body))
                {
                    rawInputs = JsonSerializer.Deserialize<Dictionary<string, object?>>(body, JsonOptions);
                }
            }

            var tenantId = EndpointSecurity.GetRequestTenant(httpContext);
            var result = await executionService.ExecuteEndpointAsync(
                name,
                rawInputs,
                httpContext.User,
                tenantId,
                ct).ConfigureAwait(false);

            await WriteResultAsync(httpContext, name, result, parquet, ct).ConfigureAwait(false);
        }
        catch (KeyNotFoundException knf)
        {
            httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
            await httpContext.Response.WriteAsJsonAsync(new { error = knf.Message }, ct).ConfigureAwait(false);
        }
        catch (ArgumentException argEx)
        {
            httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            await httpContext.Response.WriteAsJsonAsync(new { error = argEx.Message }, ct).ConfigureAwait(false);
        }
        catch (SecurityException secEx)
        {
            logger.LogWarning(secEx, "Security policy violation when executing endpoint '{EndpointName}'.", name);
            httpContext.Response.StatusCode = StatusCodes.Status403Forbidden;
            await httpContext.Response.WriteAsJsonAsync(new { error = secEx.Message }, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error executing SQL endpoint '{EndpointName}'.", name);
            httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await httpContext.Response.WriteAsJsonAsync(new { error = "An internal error occurred during query execution." }, ct).ConfigureAwait(false);
        }
    }

    private static (bool Requested, IParquetExportService? Service) ResolveParquetService(HttpContext httpContext)
    {
        if (!ParquetContentNegotiation.IsParquetRequested(httpContext.Request))
        {
            return (false, null);
        }

        return (true, httpContext.RequestServices?.GetService<IParquetExportService>());
    }

    private static async Task WriteResultAsync(
        HttpContext httpContext,
        string endpointName,
        GovernedSqlResult result,
        (bool Requested, IParquetExportService? Service) parquet,
        CancellationToken ct)
    {
        if (parquet.Requested && parquet.Service != null)
        {
            // F-DATA-01: the governed (masked, row-filtered) result rows are written as Apache Parquet
            await ParquetResponseWriter.WriteAsync(httpContext, parquet.Service, endpointName, result.Rows, result.Columns, ct).ConfigureAwait(false);
            return;
        }

        bool rawRows = httpContext.Request.Query.TryGetValue("format", out var formatVal) &&
                       (string.Equals(formatVal.ToString(), "rows", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(formatVal.ToString(), "raw", StringComparison.OrdinalIgnoreCase));

        httpContext.Response.ContentType = "application/json; charset=utf-8";
        httpContext.Response.StatusCode = StatusCodes.Status200OK;

        if (result.Truncated && !httpContext.Response.HasStarted)
        {
            httpContext.Response.Headers["X-Autheris-Truncated"] = "true";
        }

        if (rawRows)
        {
            await JsonSerializer.SerializeAsync(httpContext.Response.Body, result.Rows, JsonOptions, ct).ConfigureAwait(false);
        }
        else
        {
            var responseObj = new
            {
                columns = result.Columns,
                rows = result.Rows,
                rowCount = result.RowCount,
                truncated = result.Truncated
            };
            await JsonSerializer.SerializeAsync(httpContext.Response.Body, responseObj, JsonOptions, ct).ConfigureAwait(false);
        }
    }
}
