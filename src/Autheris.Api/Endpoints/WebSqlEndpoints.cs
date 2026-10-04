namespace Autheris.Api.Endpoints;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Security;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Extensions;
using Autheris.Api.Serialization;
using Autheris.Application.Serialization;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public static class WebSqlEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public const string GenericForbiddenMessage = "The SQL statement was rejected by the gateway security policy.";
    public const string GenericBadRequestMessage = "The SQL request is invalid (syntax error, empty statement or size limit exceeded).";
    public const string GenericServerErrorMessage = "The SQL statement could not be executed. Contact support with the trace id.";

    public sealed record WebSqlRequestDto(
        string? Sql,
        Dictionary<string, object?>? Parameters,
        string? DataSource);

    public static IEndpointRouteBuilder MapWebSqlEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/sql", HandleWebSqlRequest)
           .WithName("ExecuteGovernedWebSqlV1")
           .WithMetadata(new ParquetOutputSupportedMetadata())
           .WithRequestBodyLimit(2 * 1024 * 1024)
           .RequireAuthorization();

        app.MapPost("/api/sql", HandleWebSqlRequest)
           .WithName("ExecuteGovernedWebSql")
           .WithMetadata(new ParquetOutputSupportedMetadata())
           .WithRequestBodyLimit(2 * 1024 * 1024)
           .RequireAuthorization();

        return app;
    }

    internal static async Task HandleWebSqlRequest(
        HttpContext httpContext,
        IGovernedSqlExecutionService sqlService,
        IOptions<GatewayOptions> gatewayOptions,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("Autheris.Api.WebSql");
        var ct = httpContext.RequestAborted;

        // Content Length validation
        var maxBodySizeFeature = httpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
        if (maxBodySizeFeature != null && !maxBodySizeFeature.IsReadOnly)
        {
            maxBodySizeFeature.MaxRequestBodySize = 2 * 1024 * 1024;
        }

        if (httpContext.Request.ContentLength > 2 * 1024 * 1024)
        {
            httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            await httpContext.Response.WriteAsJsonAsync(new { error = "Request body exceeds maximum size limit (2 MB)." }, ct);
            return;
        }

        string? sql = null;
        Dictionary<string, object?>? parameters = null;
        string? dataSource = null;

        var contentType = httpContext.Request.ContentType ?? string.Empty;
        if (contentType.Contains("text/plain", StringComparison.OrdinalIgnoreCase) ||
            contentType.Contains("application/sql", StringComparison.OrdinalIgnoreCase))
        {
            using var reader = new StreamReader(httpContext.Request.Body);
            sql = await reader.ReadToEndAsync(ct);
        }
        else
        {
            try
            {
                var bodyDto = await JsonSerializer.DeserializeAsync<WebSqlRequestDto>(
                    httpContext.Request.Body,
                    JsonOptions,
                    ct);

                sql = bodyDto?.Sql;
                parameters = bodyDto?.Parameters;
                dataSource = bodyDto?.DataSource;
            }
            catch (JsonException ex)
            {
                // SEC M-10: Parser details stay in the server log
                logger.LogWarning(ex, "WebSQL request body is not valid JSON. TraceId={TraceId}", httpContext.TraceIdentifier);
                httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
                await httpContext.Response.WriteAsJsonAsync(new { error = "Invalid JSON payload.", traceId = httpContext.TraceIdentifier }, ct);
                return;
            }
        }

        if (string.IsNullOrWhiteSpace(sql))
        {
            httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            await httpContext.Response.WriteAsJsonAsync(new { error = "Missing 'sql' query parameter in request body." }, ct);
            return;
        }

        // Resolve Tenant canonically
        var user = httpContext.User;
        var securityContext = EndpointSecurity.GetSecurityContext(httpContext);
        var tenantId = securityContext?.TenantId ?? EndpointSecurity.GetRequestTenant(httpContext);

        // Check if array format requested (?format=arrays)
        bool formatArrays = httpContext.Request.Query.TryGetValue("format", out var formatVal) &&
                            string.Equals(formatVal.ToString(), "arrays", StringComparison.OrdinalIgnoreCase);

        var governedRequest = new GovernedSqlQueryRequest(sql, parameters, dataSource);

        // F-DATA-01: Parquet output (Accept: application/vnd.apache.parquet) of the fully governed result set
        if (ParquetContentNegotiation.IsParquetRequested(httpContext.Request))
        {
            await HandleParquetWebSqlRequestAsync(httpContext, sqlService, gatewayOptions, logger, governedRequest, user, tenantId, ct);
            return;
        }

        // SEC M-10: The JSON writer is created lazily when the first result arrives. Policy/parse errors raised while the
        // statement is governed therefore never start the response, so the 4xx/5xx status and the curated body can still be sent.
        Utf8JsonWriter? writer = null;
        try
        {
            int rowCount = 0;
            string[] columnNames = Array.Empty<string>();

            await sqlService.ExecuteGovernedQueryAsync(
                governedRequest,
                user,
                tenantId,
                async (reader, token) =>
                {
                    httpContext.Response.StatusCode = StatusCodes.Status200OK;
                    httpContext.Response.ContentType = "application/json; charset=utf-8";

                    var w = new Utf8JsonWriter(httpContext.Response.Body);
                    writer = w;
                    w.WriteStartObject();

                    int fieldCount = reader.FieldCount;
                    columnNames = new string[fieldCount];
                    for (int i = 0; i < fieldCount; i++)
                    {
                        columnNames[i] = reader.GetName(i);
                    }

                    // Write "columns": [...]
                    w.WriteStartArray("columns");
                    for (int i = 0; i < fieldCount; i++)
                    {
                        w.WriteStringValue(columnNames[i]);
                    }
                    w.WriteEndArray();

                    // Write "rows": [...]
                    w.WriteStartArray("rows");

                    while (await reader.ReadAsync(token))
                    {
                        rowCount++;
                        if (formatArrays)
                        {
                            w.WriteStartArray();
                            for (int i = 0; i < fieldCount; i++)
                            {
                                WriteDbValue(w, reader.IsDBNull(i) ? null : reader.GetValue(i));
                            }
                            w.WriteEndArray();
                        }
                        else
                        {
                            w.WriteStartObject();
                            for (int i = 0; i < fieldCount; i++)
                            {
                                w.WritePropertyName(columnNames[i]);
                                WriteDbValue(w, reader.IsDBNull(i) ? null : reader.GetValue(i));
                            }
                            w.WriteEndObject();
                        }

                        // Flush writer periodically to maintain streaming response for large row sets
                        if (rowCount % 250 == 0)
                        {
                            await w.FlushAsync(token);
                        }
                    }

                    w.WriteEndArray();
                },
                ct);

            if (writer == null)
            {
                // No result set was produced: emit an empty, well-formed result
                httpContext.Response.StatusCode = StatusCodes.Status200OK;
                httpContext.Response.ContentType = "application/json; charset=utf-8";
                writer = new Utf8JsonWriter(httpContext.Response.Body);
                writer.WriteStartObject();
                writer.WriteStartArray("columns");
                writer.WriteEndArray();
                writer.WriteStartArray("rows");
                writer.WriteEndArray();
            }

            writer.WriteNumber("rowCount", rowCount);
            writer.WriteEndObject();
            await writer.FlushAsync(ct);
            await httpContext.Response.Body.FlushAsync(ct);
        }
        catch (Exception ex)
        {
            await WriteWebSqlErrorAsync(httpContext, ex, logger, ct);
        }
        finally
        {
            if (writer != null)
            {
                await writer.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// F-DATA-01: Executes the governed statement and returns the result set as Apache Parquet. The rows are taken from the
    /// same governed reader as the JSON path (RLS, masking, consent applied in the rewritten SQL); the conversion is a pure
    /// output transformation. SEC M-10: nothing is written before the result arrives, so policy errors keep their status.
    /// </summary>
    private static async Task HandleParquetWebSqlRequestAsync(
        HttpContext httpContext,
        IGovernedSqlExecutionService sqlService,
        IOptions<GatewayOptions> gatewayOptions,
        ILogger logger,
        GovernedSqlQueryRequest governedRequest,
        ClaimsPrincipal user,
        TenantId tenantId,
        CancellationToken ct)
    {
        var parquetService = httpContext.RequestServices?.GetService<IParquetExportService>();
        if (await ParquetResponseWriter.TryRejectUnavailableAsync(httpContext, parquetService, ct))
        {
            return;
        }

        var configuredMaxRows = gatewayOptions.Value.ParquetEgress.MaxRowsPerFile;
        var maxRows = configuredMaxRows > 0 ? configuredMaxRows : 100000;

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        string[] columnNames = Array.Empty<string>();

        try
        {
            await sqlService.ExecuteGovernedQueryAsync(
                governedRequest,
                user,
                tenantId,
                async (reader, token) =>
                {
                    int fieldCount = reader.FieldCount;
                    columnNames = BuildUniqueColumnNames(reader);

                    // At most MaxRowsPerFile + 1 rows are read: the extra row only marks the export as truncated
                    // (X-Export-Truncated: true); the remaining result set is never materialized.
                    while (rows.Count <= maxRows && await reader.ReadAsync(token))
                    {
                        var row = new Dictionary<string, object?>(fieldCount, StringComparer.Ordinal);
                        for (int i = 0; i < fieldCount; i++)
                        {
                            row[columnNames[i]] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                        }
                        rows.Add(row);
                    }
                },
                ct);

            // An empty result (or no result set) is a Parquet file with zero rows and the result columns.
            await ParquetResponseWriter.WriteAsync(httpContext, parquetService!, "websql", rows, columnNames, ct);
        }
        catch (Exception ex)
        {
            await WriteWebSqlErrorAsync(httpContext, ex, logger, ct);
        }
    }

    private static async Task WriteWebSqlErrorAsync(
        HttpContext httpContext,
        Exception ex,
        ILogger logger,
        CancellationToken ct)
    {
        if (httpContext.Response.HasStarted)
        {
            return;
        }

        switch (ex)
        {
            case SecurityException secEx:
                logger.LogWarning(secEx, "WebSQL Security Violation. TraceId={TraceId}", httpContext.TraceIdentifier);
                httpContext.Response.StatusCode = StatusCodes.Status403Forbidden;
                httpContext.Response.ContentType = "application/json; charset=utf-8";
                await httpContext.Response.WriteAsJsonAsync(new
                {
                    error = "Forbidden",
                    message = secEx is WebSqlPolicyException ? secEx.Message : GenericForbiddenMessage,
                    traceId = httpContext.TraceIdentifier
                }, ct);
                break;

            case ArgumentException argEx:
                logger.LogWarning(argEx, "WebSQL Bad Request. TraceId={TraceId}", httpContext.TraceIdentifier);
                httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
                httpContext.Response.ContentType = "application/json; charset=utf-8";
                await httpContext.Response.WriteAsJsonAsync(new
                {
                    error = "BadRequest",
                    message = GenericBadRequestMessage,
                    traceId = httpContext.TraceIdentifier
                }, ct);
                break;

            default:
                // SEC M-10: Never echo exception/database messages to the client
                logger.LogError(ex, "WebSQL Execution Failed. TraceId={TraceId}", httpContext.TraceIdentifier);
                httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
                httpContext.Response.ContentType = "application/json; charset=utf-8";
                await httpContext.Response.WriteAsJsonAsync(new
                {
                    error = "InternalServerError",
                    message = GenericServerErrorMessage,
                    traceId = httpContext.TraceIdentifier
                }, ct);
                break;
        }
    }

    private static string[] BuildUniqueColumnNames(DbDataReader reader)
    {
        var names = new string[reader.FieldCount];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < names.Length; i++)
        {
            var baseName = reader.GetName(i);
            if (string.IsNullOrWhiteSpace(baseName))
            {
                baseName = "column" + (i + 1);
            }

            var name = baseName;
            var suffix = 1;
            while (!seen.Add(name))
            {
                name = baseName + "_" + suffix;
                suffix++;
            }

            names[i] = name;
        }

        return names;
    }

    private static void WriteDbValue(Utf8JsonWriter writer, object? val)
    {
        if (val is null or DBNull)
        {
            writer.WriteNullValue();
            return;
        }

        switch (val)
        {
            case bool b:
                writer.WriteBooleanValue(b);
                break;
            case byte by:
                writer.WriteNumberValue(by);
                break;
            case short s:
                writer.WriteNumberValue(s);
                break;
            case int i:
                writer.WriteNumberValue(i);
                break;
            case long l:
                writer.WriteNumberValue(l);
                break;
            case float f:
                writer.WriteNumberValue(f);
                break;
            case double d:
                writer.WriteNumberValue(d);
                break;
            case decimal dec:
                writer.WriteNumberValue(dec);
                break;
            case DateTime dt:
                writer.WriteStringValue(dt.ToString("o"));
                break;
            case DateTimeOffset dto:
                writer.WriteStringValue(dto.ToString("o"));
                break;
            case Guid g:
                writer.WriteStringValue(g.ToString());
                break;
            case byte[] bytes:
                writer.WriteBase64StringValue(bytes);
                break;
            default:
                writer.WriteStringValue(val.ToString());
                break;
        }
    }
}
