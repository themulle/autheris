namespace Autheris.Api.Endpoints;

using Autheris.Application.Common;
using System;
using System.Collections.Generic;
using System.IO;
using System.Security;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Errors;
using Autheris.Api.Serialization;
using Autheris.Application.Common.Serialization;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.ResultCache;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Options;
using Autheris.Domain.Options.DataSources;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public static partial class WebSqlEndpoints
{
    /// <summary>#293: fixed texts; never the JSON parser message, a property path or a.NET type name.</summary>
    internal const string InvalidRequestBodyMessage = "The request body must be a JSON object with a string 'sql' and an optional 'parameters' object of scalar values.";
    internal const string MissingSqlMessage = "A non-empty SQL statement is required.";

    private const string InvalidResultCacheOptionMessage = "The 'resultCache' option must be 'default' or 'bypass'.";

    private const string DuplicateParameterNameMessage = "A parameter name is repeated; names are compared without case and without a leading @.";

    /// <summary>True when two keys name the same parameter after the leading @ is dropped and case is ignored.</summary>
    internal static bool HasCaseOnlyDuplicateParameterNames(IReadOnlyDictionary<string, object?>? parameters) =>
        WebSqlParameterValues.HasCaseOnlyDuplicateNames(parameters);

    /// <summary>Parses the optional result cache request option; anything but default or bypass is rejected.</summary>
    internal static bool TryParseResultCacheMode(string? value, out WebSqlResultCacheMode mode)
    {
        mode = WebSqlResultCacheMode.Default;
        if (value is null || string.Equals(value, "default", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(value, "bypass", StringComparison.OrdinalIgnoreCase))
        {
            mode = WebSqlResultCacheMode.Bypass;
            return true;
        }

        return false;
    }

    private static void WriteResultCacheField(Utf8JsonWriter writer, WebSqlResultCacheOutcome outcome)
    {
        writer.WriteStartObject("resultCache");
        writer.WriteString("status", outcome.Status switch
        {
            WebSqlResultCacheStatus.Hit => "hit",
            WebSqlResultCacheStatus.Miss => "miss",
            WebSqlResultCacheStatus.Store => "store",
            WebSqlResultCacheStatus.Bypass => "bypass",
            _ => "ineligible"
        });
        if (outcome.AgeSeconds is { } age)
        {
            writer.WriteNumber("ageSeconds", age);
        }

        if (!string.IsNullOrEmpty(outcome.Reason))
        {
            writer.WriteString("reason", outcome.Reason);
        }

        writer.WriteEndObject();
    }

    private static async Task WriteRequestValidationErrorAsync(HttpContext httpContext, string code, string message, CancellationToken ct)
    {
        await ApiProblems.WriteAsync(httpContext, StatusCodes.Status400BadRequest, code, message, ct);
    }

    /// <summary>unknown and not-owned statements answer the same fixed bytes (NL-SC-22/28); the statement id is not echoed.</summary>
    private static Task StatementNotFoundAsync(HttpContext httpContext) =>
        ApiProblems.Indistinguishable(StatusCodes.Status404NotFound, ApiErrorCodes.ResourceNotFound, "Statement not found or expired.").ExecuteAsync(httpContext);

    internal static DataSourceTransportFlags ResolveIngress(HttpContext httpContext)
    {
        var ingressContext = httpContext.RequestServices?.GetService<Autheris.Application.DataSources.IIngressTransportContext>();
        if (ingressContext != null && ingressContext.Current != DataSourceTransportFlags.None)
        {
            return ingressContext.Current;
        }

        var endpoint = httpContext.GetEndpoint();
        if (endpoint != null)
        {
            var metadata = endpoint.Metadata.GetMetadata<IngressTransportMetadata>();
            if (metadata != null)
            {
                return metadata.Transport;
            }
        }

        if (ingressContext == null)
        {
            return DataSourceTransportFlags.WebSql;
        }

        return DataSourceTransportFlags.None;
    }

    internal static async Task HandleWebSqlRequest(
        HttpContext httpContext,
        IGovernedSqlExecutionService sqlService,
        IOptions<GatewayOptions> gatewayOptions,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("Autheris.Api.WebSql");
        var ct = httpContext.RequestAborted;
        var statementManager = httpContext.RequestServices?.GetService<IWebSqlStatementManager>();

        var ingress = ResolveIngress(httpContext);
        var transport = SqlTransports.ForIngress(ingress);
        if (transport == null)
        {
            await WriteWebSqlErrorAsync(httpContext, new Autheris.Domain.Exceptions.TransportNotAllowedException(null, ingress), logger, ct);
            return;
        }

        var (rawBody, bodyError) = await EndpointSecurity.TryReadBodyAsync(
            httpContext.Request, 2 * 1024 * 1024, "Request body exceeds maximum size limit (2 MB).", ct).ConfigureAwait(false);
        if (bodyError != null)
        {
            await bodyError.ExecuteAsync(httpContext).ConfigureAwait(false);
            return;
        }

        string? sql = null;
        Dictionary<string, object?>? parameters = null;
        string? dataSource = null;

        WebSqlRequestDto? bodyDto = null;
        var contentType = httpContext.Request.ContentType ?? string.Empty;
        if (contentType.Contains("text/plain", StringComparison.OrdinalIgnoreCase) ||
            contentType.Contains("application/sql", StringComparison.OrdinalIgnoreCase))
        {
            sql = rawBody;
        }
        else
        {
            try
            {
                bodyDto = JsonSerializer.Deserialize<WebSqlRequestDto>(rawBody!, JsonOptions);

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

        if (HasCaseOnlyDuplicateParameterNames(parameters))
        {
            // Two spellings of one name are ambiguous; the names are not echoed.
            await WriteRequestValidationErrorAsync(httpContext, ApiErrorCodes.BadRequest, DuplicateParameterNameMessage, ct);
            return;
        }

        // An optional structured view narrows and sorts the result of the fixed statement. It is compiled to a wrapper around the statement
        // (the statement still decides what can be read); a refused view never reaches the governed pipeline.
        Autheris.Application.Sql.View.WebSqlViewPlan? viewPlan = null;
        if (bodyDto?.View is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } viewElement)
        {
            try
            {
                viewPlan = Autheris.Application.Sql.View.WebSqlViewComposer.Plan(sql, viewElement, ct);
            }
            catch (Autheris.Application.Sql.View.WebSqlViewException ex)
            {
                await WriteRequestValidationErrorAsync(httpContext, Autheris.Application.Sql.View.WebSqlViewException.ErrorCode, ex.UserMessage, ct);
                return;
            }

            sql = viewPlan.Sql;
        }

        // The option value is never echoed back.
        string? resultCacheOption = bodyDto?.ResultCache;
        if (!TryParseResultCacheMode(resultCacheOption, out var resultCacheMode))
        {
            await WriteRequestValidationErrorAsync(httpContext, ApiErrorCodes.BadRequest, InvalidResultCacheOptionMessage, ct);
            return;
        }

        // Resolve Tenant canonically
        var user = httpContext.User;
        var securityContext = EndpointSecurity.GetSecurityContext(httpContext);
        var tenantId = securityContext?.TenantId ?? EndpointSecurity.GetRequestTenant(httpContext);

        // Check if array format requested (?format=arrays)
        bool formatArrays = httpContext.Request.Query.TryGetValue("format", out var formatVal) &&
                            string.Equals(formatVal.ToString(), "arrays", StringComparison.OrdinalIgnoreCase);

        TimeSpan? waitTimeout = null;
        if (httpContext.Request.Query.TryGetValue("wait_timeout", out var queryVal) && !string.IsNullOrWhiteSpace(queryVal))
        {
            waitTimeout = ParseDuration(queryVal.ToString().Trim());
        }

        var webSqlOptions = gatewayOptions.Value.WebSql ?? new WebSqlOptions();
        var (dmlHandled, idempotencyLease) = await TryGuardDmlRequestAsync(
            httpContext,
            sql,
            dataSource,
            parameters,
            gatewayOptions.Value.IsWebSqlDmlAllowed,
            tenantId,
            webSqlOptions.WriteIdempotencyTtlSeconds,
            logger,
            ct);
        if (dmlHandled)
        {
            return;
        }

        var rowLimit = SqlRowLimit.For(webSqlOptions);

        var pageReq = bodyDto?.Page;
        var nextTok = bodyDto?.Next;
        bool isPagingRequested = pageReq != null || nextTok != null;

        if (isPagingRequested)
        {
            if (waitTimeout != null)
            {
                await WriteWebSqlErrorAsync(httpContext, new TrinoSqlEngine.Ast.Emit.SqlPagingUnsupportedException(), logger, ct);
                return;
            }

            var maxAllowedRows = SqlRowLimit.For(webSqlOptions).MaxAllowedRows;
            if (pageReq != null)
            {
                if (pageReq.Size is null or < 1 || pageReq.Size > maxAllowedRows)
                {
                    await WriteRequestValidationErrorAsync(httpContext, ApiErrorCodes.BadRequest, $"Page size must be between 1 and {maxAllowedRows}.", ct);
                    return;
                }
            }

            if (nextTok != null && nextTok.Length > 512)
            {
                await WriteWebSqlErrorAsync(httpContext, new TrinoSqlEngine.Ast.Emit.SqlPagingTokenInvalidException(), logger, ct);
                return;
            }
        }

        SqlPageWindow? pageWindow = null;
        if (pageReq?.Size != null)
        {
            pageWindow = new SqlPageWindow(0, pageReq.Size.Value, 1);
        }

        IReadOnlyList<Autheris.Domain.Model.WebSqlIndexHint>? indexHints = null;
        GovernedSqlPageOutcome? pageOutcome = null;
        var governedRequest = new GovernedSqlQueryRequest(
            sql,
            parameters,
            dataSource,
            Transport: transport,
            RowLimit: rowLimit,
            Page: pageWindow,
            NextToken: nextTok,
            OnPageOutcome: isPagingRequested ? outcome => pageOutcome = outcome : null,
            OnIndexHints: hints => indexHints = hints);

        if (viewPlan != null)
        {
            if (viewPlan.CountSql != null && (waitTimeout != null || ParquetContentNegotiation.IsParquetRequested(httpContext.Request) ||
                string.Equals(httpContext.Request.Query["format"], "parquet", StringComparison.OrdinalIgnoreCase)))
            {
                await WriteRequestValidationErrorAsync(httpContext, Autheris.Application.Sql.View.WebSqlViewException.ErrorCode,
                    "view.totalCount is only available for a plain JSON response without wait_timeout.", ct);
                return;
            }

            governedRequest = viewPlan.Apply(governedRequest);
        }

        if (waitTimeout != null)
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

            try
            {
                var status = await statementManager.SubmitOrWaitAsync(governedRequest, user, owner, tenantId, waitTimeout.Value, ingress, ct);
                if (idempotencyLease != null && string.Equals(status.State, "FINISHED", StringComparison.Ordinal))
                {
                    var originalBody = httpContext.Response.Body;
                    using var capture = new MemoryStream();
                    httpContext.Response.Body = capture;
                    try
                    {
                        await WriteWebSqlStatementResponseAsync(httpContext, status, ct);
                    }
                    finally
                    {
                        httpContext.Response.Body = originalBody;
                    }

                    string body = Encoding.UTF8.GetString(capture.GetBuffer(), 0, (int)capture.Length);
                    await idempotencyLease.SaveOutcomeAsync(
                        httpContext.Response.StatusCode,
                        httpContext.Response.ContentType ?? "application/json; charset=utf-8",
                        body,
                        CancellationToken.None);
                    capture.Position = 0;
                    await capture.CopyToAsync(originalBody, ct);
                    return;
                }

                await WriteWebSqlStatementResponseAsync(httpContext, status, ct);
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
            if (isPagingRequested)
            {
                await WriteWebSqlErrorAsync(httpContext, new TrinoSqlEngine.Ast.Emit.SqlPagingUnsupportedException(), logger, ct);
                return;
            }
            await HandleParquetWebSqlRequestAsync(
                httpContext, sqlService, gatewayOptions, logger, governedRequest, user, tenantId, idempotencyLease == null ? resultCacheMode : null, ct);
            return;
        }

        // Only plain JSON reads take part in the result cache: statement, poll, cancel and write paths never do.
        bool resultCacheOn = webSqlOptions.ResultCacheMaxMegabytes > 0 && idempotencyLease == null;
        WebSqlResultCacheOutcome? cacheOutcome = null;
        if (resultCacheOn)
        {
            governedRequest = governedRequest with
            {
                ResultCache = new WebSqlResultCacheRequest(resultCacheMode, "json", o => cacheOutcome = o)
            };
        }

        // SEC M-10: The JSON writer is created lazily when the first result arrives. Policy/parse errors raised while the
        // statement is governed therefore never start the response, so the 4xx/5xx status and the curated body can still be sent.
        Utf8JsonWriter? writer = null;
        MemoryStream? idempotencyBuffer = idempotencyLease != null ? new MemoryStream() : null;
        try
        {
            // The count runs first, as its own governed statement over the same fixed statement and filter (never the page window).
            long? totalCount = null;
            if (viewPlan?.ApplyCount(governedRequest) is { } countRequest)
            {
                var countResult = await sqlService.ExecuteQueryBufferedAsync(countRequest, user, tenantId, ct);
                totalCount = countResult.Rows.Count == 1 && countResult.Rows[0].Count == 1
                    ? Convert.ToInt64(countResult.Rows[0].Values.First(), System.Globalization.CultureInfo.InvariantCulture)
                    : throw new InvalidOperationException("The total count statement returned no single value.");
            }

            int rowCount = 0;
            bool truncated = false;
            string[] columnNames = Array.Empty<string>();

            await sqlService.ExecuteGovernedQueryAsync(
                governedRequest,
                user,
                tenantId,
                async (reader, info, token) =>
                {
                    httpContext.Response.StatusCode = StatusCodes.Status200OK;
                    httpContext.Response.ContentType = "application/json; charset=utf-8";

                    var w = new Utf8JsonWriter(idempotencyBuffer ?? httpContext.Response.Body);
                    writer = w;
                    w.WriteStartObject();

                    int fieldCount = reader.FieldCount;
                    // WebSQL findings 2.3: unnamed and duplicate columns get unique names (_colN).
                    columnNames = SqlResultColumns.UniqueNames(reader);

                    // Write "columns": [...]
                    w.WriteStartArray("columns");
                    for (int i = 0; i < fieldCount; i++)
                    {
                        var col = info.Columns[i];
                        bool masked = i < info.MaskedColumns.Length && info.MaskedColumns[i];
                        bool nullable = masked ? true : (col.AllowDbNull ?? true);
                        string typeName = TrinoColumnTypes.Map(col, Array.Empty<object?>()).Name;
                        w.WriteStartObject();
                        w.WriteString("name", columnNames[i]);
                        w.WriteString("type", typeName);
                        w.WriteBoolean("nullable", nullable);
                        w.WriteBoolean("masked", masked);
                        w.WriteEndObject();
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
                    truncated = reader is IRowLimitProbe { HasMoreRows: true };
                },
                ct);

            if (writer == null)
            {
                // No result set was produced: emit an empty, well-formed result
                httpContext.Response.StatusCode = StatusCodes.Status200OK;
                httpContext.Response.ContentType = "application/json; charset=utf-8";
                writer = new Utf8JsonWriter(idempotencyBuffer ?? httpContext.Response.Body);
                writer.WriteStartObject();
                writer.WriteStartArray("columns");
                writer.WriteEndArray();
                writer.WriteStartArray("rows");
                writer.WriteEndArray();
            }

            writer.WriteNumber("rowCount", rowCount);
            writer.WriteBoolean("truncated", truncated);
            if (totalCount is { } total)
            {
                writer.WriteNumber("totalCount", total);
            }

            if (pageOutcome?.Page != null)
            {
                writer.WriteStartObject("page");
                writer.WriteNumber("number", pageOutcome.Page.Number);
                writer.WriteNumber("offset", pageOutcome.Page.Offset);
                writer.WriteNumber("size", pageOutcome.Page.Size);
                writer.WriteEndObject();
            }

            if (!string.IsNullOrEmpty(pageOutcome?.NextToken))
            {
                writer.WriteString("next", pageOutcome.NextToken);
            }

            if (!string.IsNullOrEmpty(pageOutcome?.PagingUnavailable))
            {
                writer.WriteString("pagingUnavailable", pageOutcome.PagingUnavailable);
            }

            if (resultCacheOn && cacheOutcome is { } reportedCacheOutcome)
            {
                WriteResultCacheField(writer, reportedCacheOutcome);
            }

            if (indexHints is { Count: > 0 })
            {
                writer.WriteStartArray("indexHints");
                foreach (var hint in indexHints)
                {
                    writer.WriteStartObject();
                    writer.WriteString("table", hint.Table);
                    writer.WriteString("reason", hint.Reason);
                    writer.WriteStartArray("helpfulIndexColumns");
                    foreach (var col in hint.HelpfulIndexColumns)
                    {
                        writer.WriteStringValue(col);
                    }
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }

            writer.WriteEndObject();
            if (truncated && !httpContext.Response.HasStarted)
            {
                httpContext.Response.Headers["X-Autheris-Truncated"] = "true";
            }
            await writer.FlushAsync(ct);
            if (idempotencyBuffer != null && idempotencyLease != null)
            {
                string capturedBody = Encoding.UTF8.GetString(idempotencyBuffer.GetBuffer(), 0, (int)idempotencyBuffer.Length);
                await idempotencyLease.SaveOutcomeAsync(
                    httpContext.Response.StatusCode,
                    httpContext.Response.ContentType ?? "application/json; charset=utf-8",
                    capturedBody,
                    CancellationToken.None);
                idempotencyBuffer.Position = 0;
                await idempotencyBuffer.CopyToAsync(httpContext.Response.Body, ct);
            }
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
            idempotencyBuffer?.Dispose();
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
        WebSqlResultCacheMode? resultCacheMode,
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
        if (resultCacheMode is { } cacheMode && (gatewayOptions.Value.WebSql?.ResultCacheMaxMegabytes ?? 0) > 0)
        {
            governedRequest = governedRequest with { ResultCache = new WebSqlResultCacheRequest(cacheMode, "parquet", null) };
        }

        bool rowLimitCut = false;

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        string[] columnNames = Array.Empty<string>();

        try
        {
            await sqlService.ExecuteGovernedQueryAsync(
                governedRequest,
                user,
                tenantId,
                async (reader, _, token) =>
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
                    rowLimitCut = reader is IRowLimitProbe { HasMoreRows: true };
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

    internal static async Task HandleWebSqlStatementPollRequest(
        string statementId,
        HttpContext httpContext,
        IWebSqlStatementManager statementManager,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("Autheris.Api.WebSql");
        var ct = httpContext.RequestAborted;

        var ingress = ResolveIngress(httpContext);
        var transport = SqlTransports.ForIngress(ingress);
        if (transport == null)
        {
            await WriteWebSqlErrorAsync(httpContext, new Autheris.Domain.Exceptions.TransportNotAllowedException(null, ingress), logger, ct);
            return;
        }

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

        TimeSpan waitTimeout = TimeSpan.FromSeconds(5);
        if (httpContext.Request.Query.TryGetValue("wait_timeout", out var queryVal) && !string.IsNullOrWhiteSpace(queryVal))
        {
            waitTimeout = ParseDuration(queryVal.ToString().Trim());
        }

        try
        {
            var status = await statementManager.GetStatusOrWaitAsync(
                statementId, user, owner, tenantId, waitTimeout,
                ingress,
                ct);
            await WriteWebSqlStatementResponseAsync(httpContext, status, ct);
        }
        catch (KeyNotFoundException)
        {
            await StatementNotFoundAsync(httpContext);
        }
        catch (SecurityException ex)
        {
            // Uniform 404 response to eliminate the enumeration oracle
            logger.Warning(ex, "Unauthorized statement access attempt for {StatementId}", statementId);
            await StatementNotFoundAsync(httpContext);
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Failed to poll statement {StatementId}", statementId);
            await ApiProblems.WriteAsync(httpContext, StatusCodes.Status500InternalServerError, ApiErrorCodes.InternalError, GenericServerErrorMessage, ct);
        }
    }

    internal static async Task HandleWebSqlStatementCancelRequest(
        string statementId,
        HttpContext httpContext,
        IWebSqlStatementManager statementManager)
    {
        var ct = httpContext.RequestAborted;

        var ingress = ResolveIngress(httpContext);
        var transport = SqlTransports.ForIngress(ingress);
        if (transport == null)
        {
            var logger = httpContext.RequestServices?.GetService<ILoggerFactory>()?.CreateLogger("Autheris.Api.WebSql") ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
            await WriteWebSqlErrorAsync(httpContext, new Autheris.Domain.Exceptions.TransportNotAllowedException(null, ingress), logger, ct);
            return;
        }

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
            // Uniform 404 response to eliminate the enumeration oracle
            httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
        }
    }
}
