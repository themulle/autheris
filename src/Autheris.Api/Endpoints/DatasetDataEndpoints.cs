namespace Autheris.Api.Endpoints;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Errors;
using Autheris.Api.Extensions;
using Autheris.Application.Sql.Errors;
using Autheris.Application.Data.Interfaces;
using Autheris.Application.Sql.Interfaces;
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
    private const string GenericForbiddenMessage = "The request was rejected by the gateway security policy.";

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
        string? orderBy = null)
    {
        if (string.IsNullOrWhiteSpace(domain) || string.IsNullOrWhiteSpace(schema) || string.IsNullOrWhiteSpace(table))
        {
            await WriteProblemAsync(httpContext, StatusCodes.Status400BadRequest, ApiErrorCodes.BadRequest, "Domain, schema, and table must be specified.").ConfigureAwait(false);
            return;
        }

        var tableId = new TableIdentifier(domain, schema, table);
        await ExecuteAndStreamQueryAsync(
            tableId,
            select,
            filter,
            orderBy,
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
        string? orderBy = null)
    {
        if (!TableIdentifier.TryParse(datasetId, out var tableId))
        {
            await WriteProblemAsync(httpContext, StatusCodes.Status400BadRequest, ApiErrorCodes.BadRequest, "Invalid dataset identifier format. Expected domain.schema.table or schema.table.").ConfigureAwait(false);
            return;
        }

        await ExecuteAndStreamQueryAsync(
            tableId,
            select,
            filter,
            orderBy,
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
            await WriteProblemAsync(httpContext, StatusCodes.Status400BadRequest, ApiErrorCodes.BadRequest, "Domain, schema, table, and id must be specified.").ConfigureAwait(false);
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
            httpContext,
            queryService,
            loggerFactory.CreateLogger(typeof(DatasetDataEndpoints)),
            ct,
            limit: 1,
            offset: 0).ConfigureAwait(false);
    }

    private static async Task ExecuteAndStreamQueryAsync(
        TableIdentifier tableId,
        string? select,
        string? filter,
        string? orderBy,
        HttpContext httpContext,
        IGovernedDataQueryService queryService,
        ILogger logger,
        CancellationToken ct,
        int? limit = null,
        int? offset = null)
    {
        if (limit is null || offset is null)
        {
            if (!TryParsePaging(httpContext.Request, out var parsedLimit, out var parsedOffset))
            {
                await WriteProblemAsync(httpContext, StatusCodes.Status400BadRequest, ApiErrorCodes.InvalidQuery, InvalidQueryMessage).ConfigureAwait(false);
                return;
            }

            limit = parsedLimit;
            offset = parsedOffset;
        }

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
            Limit: limit.Value,
            Offset: offset.Value,
            Transport: SqlTransports.Rest);

        SqlErrorMapping mapped = default;
        try
        {
            var envelope = await queryService.ExecuteQueryAsync(request, requestContext, ct).ConfigureAwait(false);
            await StreamDatasetEnvelopeAsync(httpContext.Response, envelope, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (SqlCompilerErrorMapper.TryMap(ex, out mapped) && mapped.Code != SyntaxErrorCode)
        {
            // Phase 5 M4: governed SQL errors (compiler, policy, DML, source unavailable, timeout) answer with their stable code and the
            // fixed message of that code, exactly as WebSQL does. Nothing of the exception reaches the response.
            await SqlErrorResponse.WriteAsync(httpContext, ex, mapped, logger, "Dataset query", ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (SqlCompilerErrorMapper.TryMap(ex, out var syntax) && syntax.Code == SyntaxErrorCode)
        {
            // #329 (SEC-RG-14): REST builds the SQL from quoted select and orderBy parts, so the filter is the only free text that reaches the
            // parser: a parser failure is a malformed request, answered like every other bad parameter. Only the code is logged (no filter text).
            logger.LogWarning("Malformed filter for {Table}: governed SQL error {Code}", tableId, syntax.Code);
            await WriteProblemAsync(httpContext, StatusCodes.Status400BadRequest, ApiErrorCodes.InvalidQuery, InvalidQueryMessage).ConfigureAwait(false);
        }
        catch (TableNotFoundException ex)
        {
            logger.LogInformation("Table not found: {Table}", ex.Table);
            await WriteProblemAsync(httpContext, StatusCodes.Status404NotFound, ApiErrorCodes.ResourceNotFound, "The requested table was not found.").ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.LogWarning("Access forbidden to {Table}: {Message}", tableId, ex.Message);
            await WriteProblemAsync(httpContext, StatusCodes.Status403Forbidden, ApiErrorCodes.AccessDenied, GenericForbiddenMessage).ConfigureAwait(false);
        }
        catch (RequiredFilterColumnMissingException ex)
        {
            logger.LogWarning("Required filter column missing for {Table}: {Message}", tableId, ex.Message);
            await WriteProblemAsync(httpContext, StatusCodes.Status400BadRequest, ApiErrorCodes.RequiredFilterColumnMissing, "A required filter column was not constrained by the query.").ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            // SEC-PG-14: ArgumentException messages name internal parameters; they stay in the log.
            logger.LogWarning("Invalid query parameters for {Table}: {Message}", tableId, ex.Message);
            await WriteProblemAsync(httpContext, StatusCodes.Status400BadRequest, ApiErrorCodes.InvalidQuery, InvalidQueryMessage).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is GatewaySecurityException or System.Security.SecurityException)
        {
            // Policy denials from the SQL/federation layer (e.g. cross-source disabled, incomplete HTTP response,
            // WebSQL policy) are fail-closed 403s, never 500s. Details stay in the log, not in the response.
            logger.LogWarning("Governed data query denied for {Table}: {Message}", tableId, ex.Message);
            await WriteProblemAsync(httpContext, StatusCodes.Status403Forbidden, ApiErrorCodes.AccessDenied, GenericForbiddenMessage).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error executing governed data query for {Table}", tableId);
            await WriteProblemAsync(httpContext, StatusCodes.Status500InternalServerError, ApiErrorCodes.InternalError, "An internal error occurred executing the governed query.").ConfigureAwait(false);
        }
    }

    private const string SyntaxErrorCode = "SQL_SYNTAX_ERROR";
    private const string InvalidQueryMessage = "The query parameters are invalid.";
    private const int DefaultLimit = 50;
    private const int MaxParamDigits = 10;

    /// <summary>
    /// #313 / #301, SEC-PG-14: strict parse of <c>limit</c> and <c>offset</c>. Each must occur at most once and consist of ASCII
    /// digits only (no sign, whitespace, exponent or fraction), at most <see cref="MaxParamDigits"/> long before parsing, and fit an int.
    /// </summary>
    internal static bool TryParsePaging(HttpRequest request, out int limit, out int offset)
    {
        limit = DefaultLimit;
        offset = 0;
        return TryParseNonNegative(request.Query["limit"], DefaultLimit, out limit)
            && TryParseNonNegative(request.Query["offset"], 0, out offset);
    }

    private static bool TryParseNonNegative(Microsoft.Extensions.Primitives.StringValues values, int fallback, out int result)
    {
        result = fallback;
        if (values.Count == 0)
        {
            return true;
        }

        if (values.Count != 1)
        {
            return false;
        }

        var text = values[0];
        if (string.IsNullOrEmpty(text) || text.Length > MaxParamDigits)
        {
            return false;
        }

        foreach (var c in text)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out result);
    }

    private static Task WriteProblemAsync(HttpContext httpContext, int status, string code, string detail)
    {
        httpContext.Response.StatusCode = status;
        return httpContext.Response.WriteAsJsonAsync(
            ApiProblems.CreateProblemDetails(status, code, detail, httpContext),
            (JsonSerializerOptions?)null,
            "application/problem+json");
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
