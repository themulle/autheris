namespace Autheris.Api.Endpoints;

using Autheris.Application.Common;
using System;
using System.Collections.Generic;
using System.IO;
using System.Security;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Errors;
using Autheris.Api.Serialization;
using Autheris.Application.Serialization;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public static partial class WebSqlEndpoints
{
    /// <summary>#293 (SEC-PG-11): fixed texts; never the JSON parser message, a property path or a .NET type name.</summary>
    internal const string InvalidRequestBodyMessage = "The request body must be a JSON object with a string 'sql' and an optional 'parameters' object of scalar values.";
    internal const string MissingSqlMessage = "A non-empty SQL statement is required.";

    private static async Task WriteRequestValidationErrorAsync(HttpContext httpContext, string code, string message, CancellationToken ct)
    {
        await ApiProblems.WriteAsync(httpContext, StatusCodes.Status400BadRequest, code, message, ct);
    }

    /// <summary>SEC-12H-06: unknown and not-owned statements answer the same fixed bytes (NL-SC-22/28); the statement id is not echoed.</summary>
    private static Task StatementNotFoundAsync(HttpContext httpContext) =>
        ApiProblems.Indistinguishable(StatusCodes.Status404NotFound, ApiErrorCodes.ResourceNotFound, "Statement not found or expired.").ExecuteAsync(httpContext);

    internal static async Task HandleWebSqlRequest(
        HttpContext httpContext,
        IGovernedSqlExecutionService sqlService,
        IOptions<GatewayOptions> gatewayOptions,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("Autheris.Api.WebSql");
        var ct = httpContext.RequestAborted;
        var statementManager = httpContext.RequestServices?.GetService<IWebSqlStatementManager>();

        // Content Length validation
        var maxBodySizeFeature = httpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
        if (maxBodySizeFeature != null && !maxBodySizeFeature.IsReadOnly)
        {
            maxBodySizeFeature.MaxRequestBodySize = 2 * 1024 * 1024;
        }

        if (httpContext.Request.ContentLength > 2 * 1024 * 1024)
        {
            await ApiProblems.WriteAsync(httpContext, StatusCodes.Status400BadRequest, ApiErrorCodes.BadRequest, "Request body exceeds maximum size limit (2 MB).", ct);
            return;
        }

        if (httpContext.Request.Path.StartsWithSegments("/v1/statement") &&
            !httpContext.Request.Headers.ContainsKey("X-Trino-User"))
        {
            await ApiProblems.WriteAsync(httpContext, StatusCodes.Status400BadRequest, ApiErrorCodes.BadRequest, "Trino protocol violation: Missing required 'X-Trino-User' header.", ct);
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
                logger.Warning(ex, "WebSQL request body is not valid JSON. TraceId={TraceId}", httpContext.TraceIdentifier);
                await WriteRequestValidationErrorAsync(httpContext, Errors.ApiErrorCodes.InvalidJson, InvalidRequestBodyMessage, ct);
                return;
            }
        }

        if (string.IsNullOrWhiteSpace(sql))
        {
            await WriteRequestValidationErrorAsync(httpContext, Errors.ApiErrorCodes.MissingRequiredField, MissingSqlMessage, ct);
            return;
        }

        // Resolve Tenant canonically
        var user = httpContext.User;
        var securityContext = EndpointSecurity.GetSecurityContext(httpContext);
        var tenantId = securityContext?.TenantId ?? EndpointSecurity.GetRequestTenant(httpContext);

        // Check if array format requested (?format=arrays)
        bool formatArrays = httpContext.Request.Query.TryGetValue("format", out var formatVal) &&
                            string.Equals(formatVal.ToString(), "arrays", StringComparison.OrdinalIgnoreCase);

        // Check if Trino protocol or wait_timeout requested
        bool isTrinoRoute = httpContext.Request.Path.StartsWithSegments("/v1/statement", StringComparison.OrdinalIgnoreCase);
        var trinoWaitTimeout = TryParseWaitTimeout(httpContext.Request);

        // Check for X-Trino-Catalog if dataSource was not explicitly passed in body
        if (string.IsNullOrWhiteSpace(dataSource) &&
            httpContext.Request.Headers.TryGetValue("X-Trino-Catalog", out var trinoCatalogHeader) &&
            !string.IsNullOrWhiteSpace(trinoCatalogHeader))
        {
            dataSource = trinoCatalogHeader.ToString().Trim();
        }

        var governedRequest = new GovernedSqlQueryRequest(sql, parameters, dataSource, Transport: isTrinoRoute ? SqlTransports.Trino : SqlTransports.WebSql);

        if (isTrinoRoute || trinoWaitTimeout != null)
        {
            if (statementManager == null)
            {
                throw new GatewayNotImplementedException("WebSQL statement manager is not configured.");
            }

            var callerIdentity = EndpointSecurity.GetCallerIdentity(httpContext);
            if (string.IsNullOrWhiteSpace(callerIdentity))
            {
                await ApiProblems.WriteAsync(httpContext, StatusCodes.Status401Unauthorized, ApiErrorCodes.Unauthorized, "A canonical user identity is required.", ct);
                return;
            }
            var owner = new Sid(callerIdentity);

            var timeout = trinoWaitTimeout ?? TimeSpan.FromSeconds(5);
            try
            {
                var trinoRequest = governedRequest with
                {
                    RowLimit = SqlRowLimit.For(gatewayOptions.Value.WebSql ?? new WebSqlOptions(), gatewayOptions.Value.RowLimits?.Trino),
                    Transport = "Trino"
                };
                var status = await statementManager.SubmitOrWaitAsync(trinoRequest, user, owner, tenantId, timeout, ct);
                await WriteTrinoStatementResponseAsync(httpContext, status, ct);
                return;
            }
            catch (Exception ex)
            {
                await WriteWebSqlErrorAsync(httpContext, ex, logger, ct);
                return;
            }
        }

        // F-DATA-01 / Befund 1.2: Parquet output (Accept: application/vnd.apache.parquet or ?format=parquet) of the fully governed result set
        bool isParquet = ParquetContentNegotiation.IsParquetRequested(httpContext.Request) ||
            string.Equals(httpContext.Request.Query["format"], "parquet", StringComparison.OrdinalIgnoreCase);

        // Befund 1.2: Strict Content Negotiation. If Accept header requests non-supported formats (CSV, NDJSON, etc.), reject with 406.
        if (httpContext.Request.Headers.Accept.Count > 0)
        {
            var acceptEval = ParquetContentNegotiation.Evaluate(httpContext.Request);
            if (!acceptEval.ParquetPreferred && !acceptEval.HasJsonAlternative && !isParquet)
            {
                httpContext.Response.StatusCode = StatusCodes.Status406NotAcceptable;
                return;
            }
        }

        if (isParquet)
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
            bool truncated = false;
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
                    // WebSQL findings 2.3: unnamed and duplicate columns get unique names (_colN).
                    columnNames = SqlResultColumns.UniqueNames(reader);

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
                                WebSqlValueWriter.Write(w, reader.IsDBNull(i) ? null : reader.GetValue(i));
                            }
                            w.WriteEndArray();
                        }
                        else
                        {
                            w.WriteStartObject();
                            for (int i = 0; i < fieldCount; i++)
                            {
                                w.WritePropertyName(columnNames[i]);
                                WebSqlValueWriter.Write(w, reader.IsDBNull(i) ? null : reader.GetValue(i));
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

                    // WebSQL findings 2.4: rows were cut only when the probe row beyond the limit existed.
                    truncated = reader is RowLimitedDataReader { HasMoreRows: true };
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
            writer.WriteBoolean("truncated", truncated);
            writer.WriteEndObject();
            if (truncated && !httpContext.Response.HasStarted)
            {
                httpContext.Response.Headers["X-Autheris-Truncated"] = "true";
            }
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

        // Row limits of the Parquet transport (Gateway:RowLimits:Parquet, falling back to WebSql).
        var rowLimit = SqlRowLimit.For(gatewayOptions.Value.WebSql ?? new WebSqlOptions(), gatewayOptions.Value.RowLimits?.Parquet);
        governedRequest = governedRequest with { RowLimit = rowLimit };
        bool rowLimitCut = false;

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
                    columnNames = SqlResultColumns.UniqueNames(reader);

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

                    // WebSQL findings 2.4/4.2: the row limit cut the result (probe row existed) before the file limit.
                    rowLimitCut = reader is RowLimitedDataReader { HasMoreRows: true };
                },
                ct);

            // An empty result (or no result set) is a Parquet file with zero rows and the result columns.
            await ParquetResponseWriter.WriteAsync(httpContext, parquetService!, "websql", rows, columnNames, ct, rowLimitCut);
        }
        catch (Exception ex)
        {
            await WriteWebSqlErrorAsync(httpContext, ex, logger, ct);
        }
    }

    internal static async Task HandleTrinoQueuedStatementRequest(
        string statementId,
        HttpContext httpContext,
        IWebSqlStatementManager statementManager,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("Autheris.Api.WebSql");
        var ct = httpContext.RequestAborted;

        var user = httpContext.User;
        var securityContext = EndpointSecurity.GetSecurityContext(httpContext);

        var callerIdentity = EndpointSecurity.GetCallerIdentity(httpContext);
        if (string.IsNullOrWhiteSpace(callerIdentity))
        {
            await ApiProblems.WriteAsync(httpContext, StatusCodes.Status401Unauthorized, ApiErrorCodes.Unauthorized, "A canonical user identity is required.", ct);
            return;
        }
        var owner = new Sid(callerIdentity);

        // DT-A-3: the tenant is read after the identity check (an anonymous caller is 401, never a tenant error); no tenant, no default.
        var tenantId = securityContext?.TenantId ?? EndpointSecurity.GetRequestTenant(httpContext);

        var waitTimeout = TryParseWaitTimeout(httpContext.Request) ?? TimeSpan.FromSeconds(5);

        try
        {
            var status = await statementManager.GetStatusOrWaitAsync(statementId, user, owner, tenantId, waitTimeout, ct);
            await WriteTrinoStatementResponseAsync(httpContext, status, ct);
        }
        catch (KeyNotFoundException)
        {
            await StatementNotFoundAsync(httpContext);
        }
        catch (SecurityException ex)
        {
            // SEC-12H-06: Uniform 404 response to eliminate the enumeration oracle
            logger.Warning(ex, "Unauthorized statement access attempt for {StatementId}", statementId);
            await StatementNotFoundAsync(httpContext);
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Failed to poll statement {StatementId}", statementId);
            await ApiProblems.WriteAsync(httpContext, StatusCodes.Status500InternalServerError, ApiErrorCodes.InternalError, GenericServerErrorMessage, ct);
        }
    }

    internal static async Task HandleTrinoCancelStatementRequest(
        string statementId,
        HttpContext httpContext,
        IWebSqlStatementManager statementManager)
    {
        var ct = httpContext.RequestAborted;
        var user = httpContext.User;
        var securityContext = EndpointSecurity.GetSecurityContext(httpContext);

        var callerIdentity = EndpointSecurity.GetCallerIdentity(httpContext);
        if (string.IsNullOrWhiteSpace(callerIdentity))
        {
            await ApiProblems.WriteAsync(httpContext, StatusCodes.Status401Unauthorized, ApiErrorCodes.Unauthorized, "A canonical user identity is required.", ct);
            return;
        }
        var owner = new Sid(callerIdentity);

        // DT-A-3: the tenant is read after the identity check (an anonymous caller is 401, never a tenant error); no tenant, no default.
        var tenantId = securityContext?.TenantId ?? EndpointSecurity.GetRequestTenant(httpContext);

        if (httpContext.Request.Path.StartsWithSegments("/v1/statement") &&
            !httpContext.Request.Headers.ContainsKey("X-Trino-User"))
        {
            await ApiProblems.WriteAsync(httpContext, StatusCodes.Status400BadRequest, ApiErrorCodes.BadRequest, "Trino protocol violation: Missing required 'X-Trino-User' header.", ct);
            return;
        }

        try
        {
            bool canceled = await statementManager.CancelStatementAsync(statementId, user, owner, tenantId, ct);
            if (!canceled)
            {
                httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            httpContext.Response.StatusCode = StatusCodes.Status204NoContent;
        }
        catch (SecurityException)
        {
            // SEC-12H-06: Uniform 404 response to eliminate the enumeration oracle
            httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
        }
    }
}
