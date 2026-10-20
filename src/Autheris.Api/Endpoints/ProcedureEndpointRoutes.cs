namespace Autheris.Api.Endpoints;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Data.Common;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Extensions;
using Autheris.Application.Procedures.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
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

        group.MapGet("/", HandleList).WithName("ListProcedureEndpoints").RequireAuthorization().WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

        // OpenSchema (documentation mode) keeps the document reachable anonymously, but it then lists no procedure: visibility
        // depends on the caller's identity and consent.
        var openApi = group.MapGet("/openapi.json",
                (HttpContext http, Autheris.Application.Procedures.Interfaces.IProcedureRegistry registry, IProcedureExecutionService service,
                    Microsoft.Extensions.Options.IOptions<GatewayOptions> options) =>
                    HandleOpenApi(http, registry, service, options))
            .WithName("GetProcedureEndpointsOpenApiSpec")
            .WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);
        if (gatewayOptions.IsOpenSchemaAllowed != true)
        {
            openApi.RequireAuthorization();
        }
        else
        {
            openApi.AllowAnonymous();
        }

        group.MapGet("/{name}", HandleGet).WithName("ExecuteProcedureGet").RequireAuthorization().WithAudit(AuditLevel.Full, AuditEventTypes.TableQuery);
        group.MapPost("/{name}", HandlePost).WithName("ExecuteProcedurePost").RequireAuthorization().WithAudit(AuditLevel.Full, AuditEventTypes.TableQuery);
        return app;
    }

    internal static async Task<IResult> HandleList(
        HttpContext http,
        Autheris.Application.Procedures.Interfaces.IProcedureRegistry registry,
        IProcedureExecutionService service)
    {
        // Only active procedures the caller may see (identity, role gate and consent on every table: IsVisibleAsync).
        var items = new List<object>();
        foreach (var r in await VisibleAsync(http, registry, service).ConfigureAwait(false))
        {
            items.Add(new
            {
                name = r.Definition.Name,
                summary = r.Definition.Summary,
                mode = r.Definition.Mode.ToString().ToLowerInvariant(),
                parameters = r.Definition.Parameters.Select(p => new { name = p.Name, type = p.SqlType, required = p.IsRequired, description = p.Description }).ToList()
            });
        }

        return Results.Ok(items);
    }

    /// <summary>
    /// SEC-PROC152-10: the visible Active procedures. An evaluation failure for one procedure omits only that procedure.
    /// </summary>
    private static async Task<List<RegisteredProcedure>> VisibleAsync(
        HttpContext http,
        Autheris.Application.Procedures.Interfaces.IProcedureRegistry registry,
        IProcedureExecutionService service)
    {
        var visible = new List<RegisteredProcedure>();
        var tenant = EndpointSecurity.GetRequestTenant(http);
        foreach (var reg in registry.GetAll().Where(r => r.State == ProcedureState.Active))
        {
            try
            {
                if (await service.IsVisibleAsync(reg, http.User, tenant, http.RequestAborted).ConfigureAwait(false))
                {
                    visible.Add(reg);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // omitted: no detail in the response
            }
        }

        return visible;
    }

    internal static async Task<IResult> HandleOpenApi(
        HttpContext http,
        Autheris.Application.Procedures.Interfaces.IProcedureRegistry registry,
        IProcedureExecutionService service,
        Microsoft.Extensions.Options.IOptions<GatewayOptions>? options = null)
    {
        int maxRows = options?.Value.SqlEndpoints.Procedures.MaxRows ?? new ProcedureEndpointsOptions().MaxRows;

        // Open-schema documentation mode no longer documents procedures for anonymous callers (OQ-12): visibility needs identity.
        var paths = new Dictionary<string, object>();
        var anonymous = http.User.Identity?.IsAuthenticated != true;
        var visible = anonymous ? new List<RegisteredProcedure>() : await VisibleAsync(http, registry, service).ConfigureAwait(false);
        foreach (var reg in visible)
        {
            var def = reg.Definition;
            var queryParams = def.Parameters.Select(p => new Dictionary<string, object?>
            {
                ["name"] = p.Name,
                ["in"] = "query",
                ["required"] = p.IsRequired,
                ["description"] = p.Description ?? $"SQL type {p.SqlType}",
                ["schema"] = SchemaFor(p)
            }).ToList();

            // SEC-PROC152I2-15: the reserved row-limit input; the maximum is the effective MaxRows (no scan-cap value is published).
            queryParams.Add(new Dictionary<string, object?>
            {
                ["name"] = "limit",
                ["in"] = "query",
                ["required"] = false,
                ["description"] = "Maximum number of rows to return (clamped to the configured maximum).",
                ["schema"] = new Dictionary<string, object> { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = maxRows }
            });

            var columns = reg.Validation?.ResultColumns ?? [];
            var properties = columns
                .Where(c => def.ResultTable != null || def.ClearedResultColumns.Contains(c, StringComparer.OrdinalIgnoreCase))
                .ToDictionary(c => c, c =>
                {
                    var column = new Dictionary<string, object> { ["description"] = "Governed column (may be masked or removed per consent)" };
                    if (def.DeclaredOutputTypes.TryGetValue(c, out var sqlType))
                    {
                        foreach (var (key, value) in SchemaForSqlType(sqlType))
                        {
                            column[key] = value;
                        }
                    }

                    return (object)column;
                });

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
                    ["404"] = new Dictionary<string, object> { ["description"] = "Procedure not found or not visible to the caller" },
                    ["422"] = new Dictionary<string, object> { ["description"] = "Business error raised by the procedure" },
                    ["429"] = new Dictionary<string, object> { ["description"] = "Too many concurrent requests" },
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

    /// <summary>JSON schema fragment for a declared result column type (documentation only).</summary>
    private static Dictionary<string, object> SchemaForSqlType(string sqlType)
    {
        string baseType = sqlType.Contains('(') ? sqlType[..sqlType.IndexOf('(')] : sqlType;
        var schema = new Dictionary<string, object> { ["x-sql-type"] = sqlType };
        switch (baseType)
        {
            case "int" or "bigint" or "smallint" or "tinyint":
                schema["type"] = "integer";
                break;
            case "decimal" or "numeric" or "float" or "real" or "money" or "smallmoney":
                schema["type"] = "number";
                break;
            case "bit":
                schema["type"] = "boolean";
                break;
            case "date":
                schema["type"] = "string";
                schema["format"] = "date";
                break;
            case "datetime" or "datetime2" or "smalldatetime" or "datetimeoffset":
                schema["type"] = "string";
                schema["format"] = "date-time";
                break;
            case "uniqueidentifier":
                schema["type"] = "string";
                schema["format"] = "uuid";
                break;
            default:
                schema["type"] = "string";
                break;
        }

        return schema;
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

    internal static async Task HandleGet(string name, HttpContext http, IProcedureExecutionService service, ILoggerFactory loggerFactory)
    {
        var inputs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in http.Request.Query)
        {
            inputs[k] = v.ToString();
        }

        await ExecuteAsync(name, inputs, http, service, loggerFactory).ConfigureAwait(false);
    }

    internal static async Task HandlePost(string name, HttpContext http, IProcedureExecutionService service, ILoggerFactory loggerFactory)
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
        catch (GatewayThrottledException ex)
        {
            http.Response.Headers.RetryAfter = ex.RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            await http.Response.WriteAsJsonAsync(new { error = "Too many concurrent requests. Retry later.", code = "TOO_MANY_REQUESTS", retryAfterSeconds = ex.RetryAfterSeconds, traceId = http.TraceIdentifier }, ct).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            await WriteErrorAsync(http, StatusCodes.Status400BadRequest, ex.Message, ct).ConfigureAwait(false);
        }
        catch (ProcedureBusinessException ex)
        {
            // Fixed or operator-declared text only; number, SQLSTATE and the database text never reach the response (SEC-PROC152-08).
            http.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
            await http.Response.WriteAsJsonAsync(new { error = ex.MappedMessage ?? ProcedureBusinessException.FixedMessage, code = ex.MappedCode ?? "PROCEDURE_REJECTED", traceId = ex.TraceId }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // client disconnected
        }
        catch (DbException ex)
        {
            // Driver text may contain data values: log the type only (SEC-PROC152-08).
            logger.LogError("Database failure executing a procedure endpoint ({ExceptionType}).", ex.GetType().Name);
            await WriteErrorAsync(http, StatusCodes.Status500InternalServerError, "An internal error occurred.", ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Neither the (caller supplied) name nor any parameter value is logged (SEC-PROC152-11).
            // SEC-PROC152-08: exception type only; the message and inner exceptions may carry values.
            logger.LogError("Unexpected error executing a procedure endpoint ({ExceptionType}).", ex.GetType().Name);
            await WriteErrorAsync(http, StatusCodes.Status500InternalServerError, "An internal error occurred.", ct).ConfigureAwait(false);
        }
    }

    private static Task WriteErrorAsync(HttpContext http, int status, string message, CancellationToken ct)
    {
        http.Response.StatusCode = status;
        return http.Response.WriteAsJsonAsync(new { error = message }, ct);
    }
}
