namespace Autheris.Api.Endpoints;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Extensions;
using Autheris.Application.Data.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Universal Governed REST Data API Endpoints (/api/v1/data/*).
/// Exposes governed datasets and virtual system entities with high-performance Utf8JsonWriter streaming serialization.
/// </summary>
public static class DatasetDataEndpoints
{
    public static IEndpointRouteBuilder MapDatasetDataEndpoints(this IEndpointRouteBuilder app)
    {
        // 1. GET /api/v1/data/{domain}/{schema}/{table}
        app.MapGet("/api/v1/data/{domain}/{schema}/{table}", HandleQueryThreePartAsync)
            .WithName("QueryDatasetThreePart")
            .RequireAuthorization()
            .WithAudit(AuditLevel.Full, AuditEventTypes.WebSqlQuery);

        // 2. GET /api/v1/data/{domain}/{schema}/{table}/{id}
        app.MapGet("/api/v1/data/{domain}/{schema}/{table}/{id}", HandleGetByIdAsync)
            .WithName("GetDatasetRecordById")
            .RequireAuthorization()
            .WithAudit(AuditLevel.Full, AuditEventTypes.WebSqlQuery);

        // 3. GET /api/v1/data/{datasetId} (e.g. /api/v1/data/sales.dbo.customers)
        app.MapGet("/api/v1/data/{datasetId}", HandleQueryDatasetIdAsync)
            .WithName("QueryDatasetByIdentifier")
            .RequireAuthorization()
            .WithAudit(AuditLevel.Full, AuditEventTypes.WebSqlQuery);

        return app;
    }

    internal static async Task HandleQueryThreePartAsync(
        string domain,
        string schema,
        string table,
        HttpContext httpContext,
        IGovernedDataQueryService queryService,
        ILoggerFactory loggerFactory,
        CancellationToken ct,
        string? select = null,
        string? filter = null,
        string? orderBy = null,
        int limit = 50,
        int offset = 0)
    {
        if (string.IsNullOrWhiteSpace(domain) || string.IsNullOrWhiteSpace(schema) || string.IsNullOrWhiteSpace(table))
        {
            httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            await httpContext.Response.WriteAsJsonAsync(new { error = "Domain, schema, and table must be specified." }, ct).ConfigureAwait(false);
            return;
        }

        var tableId = new TableIdentifier(domain, schema, table);
        await ExecuteAndStreamQueryAsync(
            tableId,
            select,
            filter,
            orderBy,
            limit,
            offset,
            httpContext,
            queryService,
            loggerFactory.CreateLogger(typeof(DatasetDataEndpoints)),
            ct).ConfigureAwait(false);
    }

    internal static async Task HandleQueryDatasetIdAsync(
        string datasetId,
        HttpContext httpContext,
        IGovernedDataQueryService queryService,
        ILoggerFactory loggerFactory,
        CancellationToken ct,
        string? select = null,
        string? filter = null,
        string? orderBy = null,
        int limit = 50,
        int offset = 0)
    {
        if (!TableIdentifier.TryParse(datasetId, out var tableId))
        {
            httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            await httpContext.Response.WriteAsJsonAsync(new { error = $"Invalid dataset identifier format: '{datasetId}'. Expected domain.schema.table or schema.table." }, ct).ConfigureAwait(false);
            return;
        }

        await ExecuteAndStreamQueryAsync(
            tableId,
            select,
            filter,
            orderBy,
            limit,
            offset,
            httpContext,
            queryService,
            loggerFactory.CreateLogger(typeof(DatasetDataEndpoints)),
            ct).ConfigureAwait(false);
    }

    internal static async Task HandleGetByIdAsync(
        string domain,
        string schema,
        string table,
        string id,
        HttpContext httpContext,
        IGovernedDataQueryService queryService,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(domain) || string.IsNullOrWhiteSpace(schema) || string.IsNullOrWhiteSpace(table) || string.IsNullOrWhiteSpace(id))
        {
            httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            await httpContext.Response.WriteAsJsonAsync(new { error = "Domain, schema, table, and id must be specified." }, ct).ConfigureAwait(false);
            return;
        }

        var tableId = new TableIdentifier(domain, schema, table);
        var escapedId = id.Replace("'", "''");
        var filter = $"id eq '{escapedId}'";

        await ExecuteAndStreamQueryAsync(
            tableId,
            select: null,
            filter: filter,
            orderBy: null,
            limit: 1,
            offset: 0,
            httpContext,
            queryService,
            loggerFactory.CreateLogger(typeof(DatasetDataEndpoints)),
            ct).ConfigureAwait(false);
    }

    private static async Task ExecuteAndStreamQueryAsync(
        TableIdentifier tableId,
        string? select,
        string? filter,
        string? orderBy,
        int limit,
        int offset,
        HttpContext httpContext,
        IGovernedDataQueryService queryService,
        ILogger logger,
        CancellationToken ct)
    {
        var tenant = EndpointSecurity.GetRequestTenant(httpContext);
        var clientIp = httpContext.Connection.RemoteIpAddress?.ToString();
        var requestContext = new RequestContext(httpContext.User, tenant, clientIp);

        IReadOnlyList<string>? selectCols = null;
        if (!string.IsNullOrWhiteSpace(select))
        {
            selectCols = select.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        var request = new DatasetQueryRequest(
            Table: tableId,
            SelectColumns: selectCols,
            FilterExpression: filter,
            OrderBy: orderBy,
            Limit: limit,
            Offset: offset);

        try
        {
            var envelope = await queryService.ExecuteQueryAsync(request, requestContext, ct).ConfigureAwait(false);
            await StreamDatasetEnvelopeAsync(httpContext.Response, envelope, ct).ConfigureAwait(false);
        }
        catch (TableNotFoundException ex)
        {
            logger.LogInformation("Table not found: {Table}", ex.Table);
            httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
            await httpContext.Response.WriteAsJsonAsync(new { error = ex.Message }, ct).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.LogWarning("Access forbidden to {Table}: {Message}", tableId, ex.Message);
            httpContext.Response.StatusCode = StatusCodes.Status403Forbidden;
            await httpContext.Response.WriteAsJsonAsync(new { error = ex.Message }, ct).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning("Invalid query parameters for {Table}: {Message}", tableId, ex.Message);
            httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            await httpContext.Response.WriteAsJsonAsync(new { error = ex.Message }, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error executing governed data query for {Table}", tableId);
            httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await httpContext.Response.WriteAsJsonAsync(new { error = "An internal error occurred executing the governed query." }, ct).ConfigureAwait(false);
        }
    }

    private static async Task StreamDatasetEnvelopeAsync(
        HttpResponse response,
        DatasetQueryEnvelope envelope,
        CancellationToken ct)
    {
        response.ContentType = "application/json; charset=utf-8";
        response.StatusCode = StatusCodes.Status200OK;

        await using var writer = new Utf8JsonWriter(response.Body);

        writer.WriteStartObject();
        writer.WriteString("dataset", envelope.Dataset);
        writer.WriteNumber("count", envelope.Count);
        writer.WriteNumber("offset", envelope.Offset);
        writer.WriteNumber("limit", envelope.Limit);
        writer.WriteBoolean("hasMore", envelope.HasMore);

        writer.WriteStartArray("columns");
        foreach (var col in envelope.Columns)
        {
            writer.WriteStartObject();
            writer.WriteString("name", col.Name);
            writer.WriteString("type", col.Type);
            writer.WriteBoolean("masked", col.Masked);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        writer.WriteStartArray("data");
        foreach (var row in envelope.Data)
        {
            writer.WriteStartObject();
            foreach (var (k, v) in row)
            {
                writer.WritePropertyName(k);
                WriteValue(writer, v);
            }
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        writer.WriteEndObject();
        await writer.FlushAsync(ct).ConfigureAwait(false);
    }

    private static void WriteValue(Utf8JsonWriter writer, object? val)
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
