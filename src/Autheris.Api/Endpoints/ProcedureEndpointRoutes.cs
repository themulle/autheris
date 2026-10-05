namespace Autheris.Api.Endpoints;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Procedures.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

/// <summary>
/// F-SQL-02: REST surface for governed stored procedures (phase 1: read-only). Responses are never cacheable.
/// </summary>
public static class ProcedureEndpointRoutes
{
    private const int MaxBodyBytes = 256 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static IEndpointRouteBuilder MapProcedureEndpoints(this IEndpointRouteBuilder app, GatewayOptions? gatewayOptions = null)
    {
        if (gatewayOptions?.SqlEndpoints.Procedures.Enabled != true)
        {
            return app;
        }

        var group = app.MapGroup("/api/v1/procedures");

        group.MapGet("/", HandleList).WithName("ListProcedureEndpoints").RequireAuthorization();

        var openApi = group.MapGet("/openapi.json", HandleOpenApi).WithName("GetProcedureEndpointsOpenApiSpec");
        if (gatewayOptions.IsOpenSchemaAllowed != true)
        {
            openApi.RequireAuthorization();
        }
        else
        {
            openApi.AllowAnonymous();
        }

        group.MapGet("/{name}", HandleGet).WithName("ExecuteProcedureGet").RequireAuthorization();
        group.MapPost("/{name}", HandlePost).WithName("ExecuteProcedurePost").RequireAuthorization();
        return app;
    }

    private static IResult HandleList(HttpContext http, Autheris.Application.Procedures.Interfaces.IProcedureRegistry registry)
    {
        // Only active endpoints the caller may see (role gate); consent is evaluated per call.
        var items = registry.GetAll()
            .Where(r => r.State == ProcedureState.Active && IsVisibleTo(http, r.Definition))
            .Select(r => new
            {
                name = r.Definition.Name,
                summary = r.Definition.Summary,
                mode = r.Definition.Mode.ToString().ToLowerInvariant(),
                parameters = r.Definition.Parameters.Select(p => new { name = p.Name, type = p.SqlType, required = p.IsRequired, description = p.Description })
            });
        return Results.Ok(items);
    }

    private static IResult HandleOpenApi(HttpContext http, Autheris.Application.Procedures.Interfaces.IProcedureRegistry registry)
    {
        var paths = new Dictionary<string, object>();
        foreach (var reg in registry.GetAll().Where(r => r.State == ProcedureState.Active))
        {
            var def = reg.Definition;
            if (!IsVisibleTo(http, def))
            {
                continue;
            }

            var queryParams = def.Parameters.Select(p => new Dictionary<string, object?>
            {
                ["name"] = p.Name,
                ["in"] = "query",
                ["required"] = p.IsRequired,
                ["description"] = p.Description ?? $"SQL type {p.SqlType}",
                ["schema"] = SchemaFor(p)
            }).ToList();

            var columns = reg.Validation?.ResultColumns ?? [];
            var properties = columns
                .Where(c => def.ResultTable != null || def.ClearedResultColumns.Contains(c, StringComparer.OrdinalIgnoreCase))
                .ToDictionary(c => c, _ => (object)new Dictionary<string, object> { ["description"] = "Governed column (may be masked or removed per consent)" });

            var operation = new Dictionary<string, object>
            {
                ["summary"] = def.Summary,
                ["operationId"] = "procedure_" + def.Name,
                ["x-autheris-mode"] = def.Mode.ToString().ToLowerInvariant(),
                ["parameters"] = queryParams,
                ["responses"] = new Dictionary<string, object>
                {
                    ["200"] = new Dictionary<string, object>
                    {
                        ["description"] = "Governed result set (first result set only)",
                        ["content"] = new Dictionary<string, object>
                        {
                            ["application/json"] = new Dictionary<string, object>
                            {
                                ["schema"] = new Dictionary<string, object>
                                {
                                    ["type"] = "object",
                                    ["properties"] = new Dictionary<string, object>
                                    {
                                        ["columns"] = new Dictionary<string, object> { ["type"] = "array", ["items"] = new Dictionary<string, object> { ["type"] = "string" } },
                                        ["rows"] = new Dictionary<string, object>
                                        {
                                            ["type"] = "array",
                                            ["items"] = new Dictionary<string, object> { ["type"] = "object", ["properties"] = properties }
                                        },
                                        ["rowCount"] = new Dictionary<string, object> { ["type"] = "integer" },
                                        ["truncated"] = new Dictionary<string, object> { ["type"] = "boolean" }
                                    }
                                }
                            }
                        }
                    },
                    ["403"] = new Dictionary<string, object> { ["description"] = "Access denied" },
                    ["422"] = new Dictionary<string, object> { ["description"] = "Business error raised by the procedure" },
                    ["503"] = new Dictionary<string, object> { ["description"] = "Endpoint disabled (validation failed)" }
                }
            };

            paths["/api/v1/procedures/" + def.Name] = new Dictionary<string, object> { ["get"] = operation };
        }

        return Results.Ok(new Dictionary<string, object>
        {
            ["openapi"] = "3.0.3",
            ["info"] = new Dictionary<string, object>
            {
                ["title"] = "Autheris Governed Stored Procedures",
                ["version"] = "1.0.0"
            },
            ["paths"] = paths
        });
    }

    private static Dictionary<string, object> SchemaFor(ProcedureParameter p)
    {
        var schema = new Dictionary<string, object>();
        if (p.ClrType == typeof(int) || p.ClrType == typeof(long) || p.ClrType == typeof(short) || p.ClrType == typeof(byte))
        {
            schema["type"] = "integer";
        }
        else if (p.ClrType == typeof(decimal) || p.ClrType == typeof(double) || p.ClrType == typeof(float))
        {
            schema["type"] = "number";
        }
        else if (p.ClrType == typeof(bool))
        {
            schema["type"] = "boolean";
        }
        else
        {
            schema["type"] = "string";
            if (p.ClrType == typeof(DateTime) || p.ClrType == typeof(DateTimeOffset))
            {
                schema["format"] = "date-time";
            }
            else if (p.ClrType == typeof(Guid))
            {
                schema["format"] = "uuid";
            }
            else if (p.MaxLength.HasValue)
            {
                schema["maxLength"] = p.MaxLength.Value;
            }
        }

        return schema;
    }

    private static bool IsVisibleTo(HttpContext http, ProcedureDefinition def)
    {
        if (def.RequiredRoles.Count == 0)
        {
            return http.User.Identity?.IsAuthenticated == true;
        }

        var roles = http.User.GetUserRoles();
        return def.RequiredRoles.Any(r => http.User.IsInRole(r) || roles.Contains(r));
    }

    private static async Task HandleGet(string name, HttpContext http, IProcedureExecutionService service, ILoggerFactory loggerFactory)
    {
        var inputs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in http.Request.Query)
        {
            inputs[k] = v.ToString();
        }

        await ExecuteAsync(name, inputs, http, service, loggerFactory).ConfigureAwait(false);
    }

    private static async Task HandlePost(string name, HttpContext http, IProcedureExecutionService service, ILoggerFactory loggerFactory)
    {
        var ct = http.RequestAborted;
        if (http.Request.ContentLength > MaxBodyBytes)
        {
            http.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            await http.Response.WriteAsJsonAsync(new { error = "Request body too large." }, ct).ConfigureAwait(false);
            return;
        }

        Dictionary<string, object?>? inputs = null;
        try
        {
            using var reader = new StreamReader(http.Request.Body);
            string body = await ReadLimitedAsync(reader, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(body))
            {
                inputs = JsonSerializer.Deserialize<Dictionary<string, object?>>(body, JsonOptions);
            }
        }
        catch (JsonException)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            await http.Response.WriteAsJsonAsync(new { error = "Invalid JSON body." }, ct).ConfigureAwait(false);
            return;
        }
        catch (InvalidDataException)
        {
            http.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            await http.Response.WriteAsJsonAsync(new { error = "Request body too large." }, ct).ConfigureAwait(false);
            return;
        }

        await ExecuteAsync(name, inputs, http, service, loggerFactory).ConfigureAwait(false);
    }

    private static async Task<string> ReadLimitedAsync(StreamReader reader, CancellationToken ct)
    {
        var buffer = new char[4096];
        var sb = new System.Text.StringBuilder();
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            sb.Append(buffer, 0, read);
            if (sb.Length > MaxBodyBytes)
            {
                throw new InvalidDataException("Body too large.");
            }
        }

        return sb.ToString();
    }

    private static async Task ExecuteAsync(
        string name,
        IReadOnlyDictionary<string, object?>? inputs,
        HttpContext http,
        IProcedureExecutionService service,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("Autheris.Api.ProcedureEndpoints");
        var ct = http.RequestAborted;
        http.Response.Headers.CacheControl = "no-store";

        try
        {
            var result = await service.ExecuteAsync(name, inputs, http.User, EndpointSecurity.GetRequestTenant(http), ct).ConfigureAwait(false);

            if (result.Truncated)
            {
                http.Response.Headers["X-Autheris-Truncated"] = "true";
            }

            http.Response.StatusCode = StatusCodes.Status200OK;
            await http.Response.WriteAsJsonAsync(
                new { columns = result.Columns, rows = result.Rows, rowCount = result.RowCount, truncated = result.Truncated },
                ct).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            await WriteErrorAsync(http, StatusCodes.Status404NotFound, "Procedure endpoint not found.", ct).ConfigureAwait(false);
        }
        catch (ProcedureUnavailableException)
        {
            await WriteErrorAsync(http, StatusCodes.Status503ServiceUnavailable, "Procedure endpoint is currently unavailable.", ct).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            await WriteErrorAsync(http, StatusCodes.Status400BadRequest, ex.Message, ct).ConfigureAwait(false);
        }
        catch (SecurityException)
        {
            await WriteErrorAsync(http, StatusCodes.Status403Forbidden, "Access to the procedure is denied.", ct).ConfigureAwait(false);
        }
        catch (ProcedureBusinessException ex)
        {
            await WriteErrorAsync(http, StatusCodes.Status422UnprocessableEntity, ex.Message, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // client disconnected
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error executing procedure endpoint '{Endpoint}'.", name);
            await WriteErrorAsync(http, StatusCodes.Status500InternalServerError, "An internal error occurred.", ct).ConfigureAwait(false);
        }
    }

    private static Task WriteErrorAsync(HttpContext http, int status, string message, CancellationToken ct)
    {
        http.Response.StatusCode = status;
        return http.Response.WriteAsJsonAsync(new { error = message }, ct);
    }
}
