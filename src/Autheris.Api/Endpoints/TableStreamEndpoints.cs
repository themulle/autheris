namespace Autheris.Api.Endpoints;

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Apache.Arrow.Ipc;
using Autheris.Api.Extensions;
using Autheris.Application.Caching.Interfaces;
using Autheris.Application.Data;
using Autheris.Application.Data.Interfaces;
using Autheris.Application.Data.Services;
using Autheris.Application.Interfaces;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Serialization;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// PLAN-DATA-178: Dedicated Governed Single-Table Bulk Keyset Streaming Endpoint.
/// Strictly scoped to a single table, authorized for read-only callers, with monotonic keyset cursor seek,
/// bounded zero-allocation chunked streaming, and two-phase token bucket FinOps metering.
/// </summary>
public static class TableStreamEndpoints
{
    private static readonly byte[] NewLineBytes = [(byte)'\n'];
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static MaskingRule ScopeRuleForTenant(MaskingRule rule, string? tenant, string? defaultKeyId = null) =>
        !string.IsNullOrWhiteSpace(tenant) && rule.IsHmac ? MaskingRule.CreateTenantScopedHmacRule(rule, tenant, defaultKeyId) : rule;

    public static IEndpointRouteBuilder MapTableStreamEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Canonical: POST /api/v1/data/{domain}/{schema}/{table}/stream
        app.MapPost("/api/v1/data/{domain}/{schema}/{table}/stream", HandleBulkStreamAsync)
            .WithName("StreamDatasetBulkKeyset")
            .RequireAuthorization()
            .WithAudit(AuditLevel.Full, AuditEventTypes.TableQuery);

        // Alias: POST /api/v1/data/{domain}/{schema}/{table}/bulk
        app.MapPost("/api/v1/data/{domain}/{schema}/{table}/bulk", HandleBulkStreamAsync)
            .WithName("StreamDatasetBulkKeysetAlias")
            .RequireAuthorization()
            .WithAudit(AuditLevel.Full, AuditEventTypes.TableQuery);

        return app;
    }

    internal static async Task HandleBulkStreamAsync(
        string domain,
        string schema,
        string table,
        HttpContext httpContext,
        ITableMetadataRepository metadataRepository,
        IUnifiedPolicyDecisionPoint pdp,
        IGovernedTableReadPlanner planner,
        IGovernedSqlExecutionService sqlExecutionService,
        IKeysetCursorEncoder cursorEncoder,
        IColumnMaskingProvider maskingProvider,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        var logger = loggerFactory.CreateLogger(typeof(TableStreamEndpoints));

        var readPathSwitches = httpContext.RequestServices?.GetService<IReadPathSwitches>();
        if (readPathSwitches != null && !readPathSwitches.BulkStreamEnabled)
        {
            httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
            await httpContext.Response.WriteAsJsonAsync(new { error = "Bulk streaming endpoint is disabled." }, ct).ConfigureAwait(false);
            return;
        }

        // 1. Validate route identifiers
        if (string.IsNullOrWhiteSpace(domain) || string.IsNullOrWhiteSpace(schema) || string.IsNullOrWhiteSpace(table) ||
            domain.Contains('/') || schema.Contains('/') || table.Contains('/') ||
            domain.Contains('\\') || schema.Contains('\\') || table.Contains('\\') ||
            domain.Contains(';') || schema.Contains(';') || table.Contains(';'))
        {
            httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            await httpContext.Response.WriteAsJsonAsync(new { error = "Domain, schema, and table must be valid single segment identifiers." }, ct).ConfigureAwait(false);
            return;
        }

        // SEC-DATA178-05: Reject OFFSET pagination
        if (httpContext.Request.Query.ContainsKey("offset"))
        {
            httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            await httpContext.Response.WriteAsJsonAsync(new
            {
                error = "OFFSET pagination is not supported on bulk keyset streaming endpoint. Use cursor-based keyset pagination instead.",
                code = "OFFSET_NOT_SUPPORTED"
            }, ct).ConfigureAwait(false);
            return;
        }

        // 2. Deserialize request payload
        BulkTableStreamRequest request;
        if (httpContext.Request.ContentLength > 0 || httpContext.Request.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
        {
            try
            {
                request = await JsonSerializer.DeserializeAsync<BulkTableStreamRequest>(
                    httpContext.Request.Body,
                    JsonOptions,
                    ct).ConfigureAwait(false) ?? new BulkTableStreamRequest();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to deserialize bulk stream request body");
                httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
                await httpContext.Response.WriteAsJsonAsync(new { error = "Invalid JSON request body." }, ct).ConfigureAwait(false);
                return;
            }
        }
        else
        {
            request = new BulkTableStreamRequest();
        }

        if (request.AdditionalProperties != null && request.AdditionalProperties.ContainsKey("offset"))
        {
            httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            await httpContext.Response.WriteAsJsonAsync(new
            {
                error = "OFFSET pagination is not supported on bulk keyset streaming endpoint. Use cursor-based keyset pagination instead.",
                code = "OFFSET_NOT_SUPPORTED"
            }, ct).ConfigureAwait(false);
            return;
        }

        var tableId = new TableIdentifier(domain, schema, table);

        // 3. Resolve TableMetadata & Enforce Visibility
        TableMetadata? metadata;
        try
        {
            metadata = await metadataRepository.GetTableMetadataAsync(tableId, ct).ConfigureAwait(false)
                ?? VirtualSystemTables.Get(tableId);
        }
        catch (TableMetadataUnavailableException)
        {
            metadata = null;
        }

        // SEC-DATA178-04: Non-existent or hidden tables return 404 anti-enumeration parity
        if (metadata == null || (!metadata.Table.IsActive && !httpContext.User.IsInRole("admin")))
        {
            httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
            await httpContext.Response.WriteAsJsonAsync(new { error = $"Table '{tableId.ToQualifiedName()}' not found." }, ct).ConfigureAwait(false);
            return;
        }

        if (metadata.Table.Visibility == DatasetVisibility.Hidden && !httpContext.User.IsInRole("admin"))
        {
            httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
            await httpContext.Response.WriteAsJsonAsync(new { error = $"Table '{tableId.ToQualifiedName()}' not found." }, ct).ConfigureAwait(false);
            return;
        }

        // 4. Governance Authorization Stage (PDP)
        var tenant = EndpointSecurity.GetRequestTenant(httpContext);
        var userSid = new Sid(EndpointSecurity.GetCallerIdentity(httpContext) ?? "anonymous");
        var callerSid = userSid.Value;
        var usageRecorder = httpContext.RequestServices?.GetService<IConsumerUsageRecorder>();
        var clientIp = httpContext.Connection.RemoteIpAddress?.ToString();

        TableAccessDecision decision;
        try
        {
            decision = await GovernedQueryStages.AuthorizeTableAsync(
                pdp,
                tableId,
                metadata,
                httpContext.User,
                tenant,
                clientIp,
                request.Select,
                ct).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException)
        {
            usageRecorder?.RecordUsage(tableId, tenant, userSid, ConsumerOutcome.DenyAccess, 0, false);
            // Anti-enumeration parity: unauthorized table returns 404
            httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
            await httpContext.Response.WriteAsJsonAsync(new { error = $"Table '{tableId.ToQualifiedName()}' not found." }, ct).ConfigureAwait(false);
            return;
        }

        // Anti-enumeration parity: unauthorized table returns 404
        if (!decision.IsAllowed)
        {
            httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
            await httpContext.Response.WriteAsJsonAsync(new { error = $"Table '{tableId.ToQualifiedName()}' not found." }, ct).ConfigureAwait(false);
            return;
        }

        // 5. Enforce Primary Key & Masking Gating (SEC-DATA178-04, SEC-DATA178-07)
        IReadOnlyList<string> pkCols = metadata.PrimaryKeyColumns;
        if (pkCols.Count == 0)
        {
            var uniqueIdx = metadata.Indexes.FirstOrDefault(i => i.IsUnique && i.Columns.Count > 0);
            if (uniqueIdx != null)
            {
                pkCols = uniqueIdx.Columns;
            }
            else
            {
                httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
                await httpContext.Response.WriteAsJsonAsync(new
                {
                    error = "Table requires a primary key or unique index for keyset streaming.",
                    code = "KEYSET_STREAMING_UNAVAILABLE"
                }, ct).ConfigureAwait(false);
                return;
            }
        }

        foreach (var pk in pkCols)
        {
            var access = decision.GetEffectiveColumnAccess(pk, metadata);
            if (access == ColumnAccessLevel.Mask || access == ColumnAccessLevel.Deny)
            {
                httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
                await httpContext.Response.WriteAsJsonAsync(new
                {
                    error = $"Primary key column '{pk}' is masked or denied for caller.",
                    code = "KEYSET_STREAMING_UNAVAILABLE"
                }, ct).ConfigureAwait(false);
                return;
            }
        }

        // 6. Plan the Keyset Stream Query
        PlannedTableRead planned;
        try
        {
            planned = planner.PlanKeysetStream(request, metadata, decision);
        }
        catch (InvalidCursorException ex)
        {
            logger.LogWarning(ex, "Invalid cursor in bulk stream request");
            httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            await httpContext.Response.WriteAsJsonAsync(new { error = "The supplied cursor is invalid.", code = InvalidCursorException.ErrorCode }, ct).ConfigureAwait(false);
            return;
        }
        catch (RequiredFilterColumnMissingException ex)
        {
            logger.LogWarning(ex, "Required filter column missing in bulk stream request");
            httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            await httpContext.Response.WriteAsJsonAsync(new { error = "A required filter column is missing.", code = RequiredFilterColumnMissingException.ErrorCode }, ct).ConfigureAwait(false);
            return;
        }
        catch (KeysetStreamingUnavailableException ex)
        {
            logger.LogWarning(ex, "Keyset streaming unavailable for bulk stream request");
            httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            await httpContext.Response.WriteAsJsonAsync(new { error = "Keyset streaming is unavailable for this query.", code = KeysetStreamingUnavailableException.ErrorCode }, ct).ConfigureAwait(false);
            return;
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning(ex, "Invalid argument in bulk stream request");
            httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            await httpContext.Response.WriteAsJsonAsync(new { error = "Invalid streaming argument." }, ct).ConfigureAwait(false);
            return;
        }

        // 7. Token Bucket Admission (Phase 1: Upfront Reservation)
        var rateLimiter = httpContext.RequestServices!.GetService<IRateLimiterService>();
        var tierResolver = httpContext.RequestServices!.GetService<IClientTierResolver>();
        int sliceLimit = request.Limit > 0 ? Math.Min(request.Limit, 10000) : 5000;
        int baseCost = 10;
        int costPerRow = 1;
        int reservedCost = baseCost + (sliceLimit * costPerRow);

        ClientQuotaPolicy policy;
        ClientQuotaContext? clientContext = null;
        string subjectId = EndpointSecurity.GetCallerIdentity(httpContext) ?? "anonymous";
        if (tierResolver != null)
        {
            string? apiKey = httpContext.Request.Headers.TryGetValue("X-API-Key", out var ak) ? ak.ToString() : null;
            clientContext = await tierResolver.ResolveAsync(httpContext.User, apiKey, clientIp, ct).ConfigureAwait(false);
            policy = clientContext.Policy;
            subjectId = clientContext.SubjectId;
        }
        else
        {
            policy = ClientQuotaPolicy.ForTier(ClientTier.Internal);
        }

        if (rateLimiter != null)
        {
            var reservation = await rateLimiter.ReserveCostQuotaAsync(subjectId, reservedCost, policy, ct).ConfigureAwait(false);
            if (!reservation.Allowed)
            {
                usageRecorder?.RecordUsage(tableId, tenant, userSid, ConsumerOutcome.DenyRateLimit, 0, false);
                httpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                httpContext.Response.Headers["Retry-After"] = reservation.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
                if (!string.IsNullOrWhiteSpace(clientContext?.ResolvedScope))
                {
                    httpContext.Response.Headers["X-RateLimit-Scope"] = clientContext.ResolvedScope;
                }
                await httpContext.Response.WriteAsJsonAsync(new
                {
                    error = "Rate limit exceeded. Wait until the next time window.",
                    code = "RATE_LIMIT_EXCEEDED",
                    retryAfterSeconds = reservation.RetryAfterSeconds,
                    scope = clientContext?.ResolvedScope
                }, ct).ConfigureAwait(false);
                return;
            }
        }

        // 8. Stream Execution (NDJSON or Arrow IPC)
        bool isArrow = string.Equals(request.Format, "arrow", StringComparison.OrdinalIgnoreCase) ||
            (httpContext.Request.Headers.Accept.ToString()?.Contains(IArrowExportService.ArrowStreamContentType, StringComparison.OrdinalIgnoreCase) == true);

        var queryRequest = new GovernedSqlQueryRequest(
            Sql: planned.Sql,
            Parameters: planned.Parameters,
            Transport: SqlTransports.Rest,
            PlannedTableRead: true);

        int rowsEmitted = 0;
        bool hasMore = false;
        string? nextCursor = null;
        string? truncatedReason = null;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);

        try
        {
            if (isArrow)
            {
                var arrowService = httpContext.RequestServices!.GetRequiredService<IArrowExportService>();
                httpContext.Response.ContentType = IArrowExportService.ArrowStreamContentType;
                httpContext.Response.StatusCode = StatusCodes.Status200OK;

                var microBatch = new List<IReadOnlyDictionary<string, object?>>(256);
                Dictionary<string, object?>? lastRowPk = null;

                await sqlExecutionService.ExecuteGovernedQueryAsync(queryRequest, httpContext.User, tenant, async (reader, token) =>
                {
                    int colCount = reader.FieldCount;
                    string[] colNames = new string[colCount];
                    for (int i = 0; i < colCount; i++) colNames[i] = reader.GetName(i);

                    var maskedCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var c in colNames)
                    {
                        if (decision.GetEffectiveColumnAccess(c, metadata) == ColumnAccessLevel.Mask)
                        {
                            maskedCols.Add(c);
                        }
                    }

                    int[] pkOrdinals = new int[pkCols.Count];
                    for (int i = 0; i < pkCols.Count; i++)
                    {
                        var canon = metadata.GetColumn(pkCols[i])!.ColumnName;
                        pkOrdinals[i] = reader.GetOrdinal(canon);
                    }

                    HashSet<string>? selectSet = request.Select is { Count: > 0 }
                        ? new HashSet<string>(request.Select, StringComparer.OrdinalIgnoreCase)
                        : null;

                    using var arrowWriter = new ArrowStreamWriter(httpContext.Response.Body, arrowService.BuildRecordBatch(new List<IReadOnlyDictionary<string, object?>> { new Dictionary<string, object?> { ["init"] = 1 } }).Schema);

                    while (await reader.ReadAsync(token).ConfigureAwait(false))
                    {
                        if (rowsEmitted >= sliceLimit)
                        {
                            hasMore = true;
                            break;
                        }

                        if (DateTimeOffset.UtcNow >= deadline)
                        {
                            truncatedReason = "timeout";
                            break;
                        }

                        if (httpContext.Items.ContainsKey("QuotaExhausted"))
                        {
                            truncatedReason = "quota_exhausted";
                            break;
                        }

                        var rowDict = new Dictionary<string, object?>(colCount, StringComparer.OrdinalIgnoreCase);
                        for (int i = 0; i < colCount; i++)
                        {
                            var name = colNames[i];
                            if (selectSet != null && !selectSet.Contains(name))
                            {
                                continue;
                            }

                            var val = reader.IsDBNull(i) ? null : reader.GetValue(i);
                            if (val != null && maskedCols.Contains(name))
                            {
                                var rule = metadata.ColumnMaskingRules.TryGetValue(name, out var r) ? r : new MaskingRule { RuleType = "REDACT" };
                                var scoped = ScopeRuleForTenant(rule, tenant.Value, null);
                                val = maskingProvider.MaskValue(name, val, scoped);
                            }
                            rowDict[name] = val;
                        }

                        lastRowPk = new Dictionary<string, object?>(pkCols.Count, StringComparer.OrdinalIgnoreCase);
                        for (int i = 0; i < pkCols.Count; i++)
                        {
                            var pkName = pkCols[i];
                            var pkVal = reader.IsDBNull(pkOrdinals[i]) ? null : reader.GetValue(pkOrdinals[i]);
                            lastRowPk[pkName] = pkVal;
                        }

                        microBatch.Add(rowDict);
                        rowsEmitted++;

                        if (microBatch.Count >= 256)
                        {
                            var batch = arrowService.BuildRecordBatch(microBatch);
                            await arrowWriter.WriteRecordBatchAsync(batch, token).ConfigureAwait(false);
                            await httpContext.Response.Body.FlushAsync(token).ConfigureAwait(false);
                            microBatch.Clear();

                            if (rateLimiter != null)
                            {
                                var quotaStatus = await rateLimiter.CheckCostQuotaAsync(subjectId, 0, policy, token).ConfigureAwait(false);
                                if (!quotaStatus.Allowed && quotaStatus.RemainingTokens <= 0)
                                {
                                    truncatedReason = "quota_exhausted";
                                    break;
                                }
                            }
                        }
                    }

                    if (microBatch.Count > 0)
                    {
                        var batch = arrowService.BuildRecordBatch(microBatch);
                        await arrowWriter.WriteRecordBatchAsync(batch, token).ConfigureAwait(false);
                        await httpContext.Response.Body.FlushAsync(token).ConfigureAwait(false);
                        microBatch.Clear();
                    }
                }, ct).ConfigureAwait(false);

                if (lastRowPk != null && (hasMore || truncatedReason != null))
                {
                    var cursorContext = new KeysetCursorContext(tenant.Value, domain, table, "asc", pkCols, callerSid);
                    nextCursor = cursorEncoder.EncodeCursor(lastRowPk, cursorContext);
                }
            }
            else
            {
                // Default: NDJSON (application/x-ndjson) directly to PipeWriter
                httpContext.Response.ContentType = "application/x-ndjson; charset=utf-8";
                httpContext.Response.StatusCode = StatusCodes.Status200OK;

                Dictionary<string, object?>? lastRowPk = null;

                await sqlExecutionService.ExecuteGovernedQueryAsync(queryRequest, httpContext.User, tenant, async (reader, token) =>
                {
                    int colCount = reader.FieldCount;
                    string[] colNames = new string[colCount];
                    for (int i = 0; i < colCount; i++) colNames[i] = reader.GetName(i);

                    var maskedCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var c in colNames)
                    {
                        if (decision.GetEffectiveColumnAccess(c, metadata) == ColumnAccessLevel.Mask)
                        {
                            maskedCols.Add(c);
                        }
                    }

                    int[] pkOrdinals = new int[pkCols.Count];
                    for (int i = 0; i < pkCols.Count; i++)
                    {
                        var canon = metadata.GetColumn(pkCols[i])!.ColumnName;
                        pkOrdinals[i] = reader.GetOrdinal(canon);
                    }

                    HashSet<string>? selectSet = request.Select is { Count: > 0 }
                        ? new HashSet<string>(request.Select, StringComparer.OrdinalIgnoreCase)
                        : null;

                    var bodyWriter = httpContext.Response.BodyWriter;
                    using var jsonWriter = new Utf8JsonWriter(bodyWriter);

                    while (await reader.ReadAsync(token).ConfigureAwait(false))
                    {
                        if (rowsEmitted >= sliceLimit)
                        {
                            hasMore = true;
                            break;
                        }

                        if (DateTimeOffset.UtcNow >= deadline)
                        {
                            truncatedReason = "timeout";
                            break;
                        }

                        if (httpContext.Items.ContainsKey("QuotaExhausted"))
                        {
                            truncatedReason = "quota_exhausted";
                            break;
                        }

                        jsonWriter.WriteStartObject();
                        for (int i = 0; i < colCount; i++)
                        {
                            var name = colNames[i];
                            if (selectSet != null && !selectSet.Contains(name))
                            {
                                continue;
                            }

                            var val = reader.IsDBNull(i) ? null : reader.GetValue(i);
                            if (val != null && maskedCols.Contains(name))
                            {
                                var rule = metadata.ColumnMaskingRules.TryGetValue(name, out var r) ? r : new MaskingRule { RuleType = "REDACT" };
                                var scoped = ScopeRuleForTenant(rule, tenant.Value, null);
                                val = maskingProvider.MaskValue(name, val, scoped);
                            }

                            jsonWriter.WritePropertyName(name);
                            WriteJsonValue(jsonWriter, val);
                        }
                        jsonWriter.WriteEndObject();
                        await jsonWriter.FlushAsync(token).ConfigureAwait(false);
                        bodyWriter.Write(NewLineBytes);
                        jsonWriter.Reset();

                        lastRowPk = new Dictionary<string, object?>(pkCols.Count, StringComparer.OrdinalIgnoreCase);
                        for (int i = 0; i < pkCols.Count; i++)
                        {
                            var pkName = pkCols[i];
                            var pkVal = reader.IsDBNull(pkOrdinals[i]) ? null : reader.GetValue(pkOrdinals[i]);
                            lastRowPk[pkName] = pkVal;
                        }

                        rowsEmitted++;

                        // TCP backpressure check every 256 rows
                        if (rowsEmitted % 256 == 0)
                        {
                            var flushResult = await bodyWriter.FlushAsync(token).ConfigureAwait(false);
                            if (flushResult.IsCanceled || flushResult.IsCompleted)
                            {
                                break;
                            }

                            if (rateLimiter != null)
                            {
                                var quotaStatus = await rateLimiter.CheckCostQuotaAsync(subjectId, 0, policy, token).ConfigureAwait(false);
                                if (!quotaStatus.Allowed && quotaStatus.RemainingTokens <= 0)
                                {
                                    truncatedReason = "quota_exhausted";
                                    break;
                                }
                            }
                        }
                    }

                    await bodyWriter.FlushAsync(token).ConfigureAwait(false);
                }, ct).ConfigureAwait(false);

                if (lastRowPk != null && (hasMore || truncatedReason != null))
                {
                    var cursorContext = new KeysetCursorContext(tenant.Value, domain, table, "asc", pkCols, callerSid);
                    nextCursor = cursorEncoder.EncodeCursor(lastRowPk, cursorContext);
                }

                if (hasMore || truncatedReason != null || request.IncludeMetadata)
                {
                    var metaObj = new
                    {
                        _meta = new
                        {
                            nextCursor,
                            hasMore,
                            truncatedReason,
                            rowsStreamed = rowsEmitted
                        }
                    };
                    var metaBytes = JsonSerializer.SerializeToUtf8Bytes(metaObj, JsonOptions);
                    var bodyWriter = httpContext.Response.BodyWriter;
                    bodyWriter.Write(metaBytes);
                    bodyWriter.Write(NewLineBytes);
                    await bodyWriter.FlushAsync(ct).ConfigureAwait(false);
                }
            }
        }
        catch (GatewayThrottledException ex) when (!httpContext.Response.HasStarted)
        {
            httpContext.Response.Clear();
            httpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            httpContext.Response.Headers.RetryAfter = ex.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
            await httpContext.Response.WriteAsJsonAsync(new
            {
                error = "Too many concurrent requests. Retry later.",
                code = "TOO_MANY_REQUESTS",
                retryAfterSeconds = ex.RetryAfterSeconds,
                traceId = httpContext.TraceIdentifier
            }, ct).ConfigureAwait(false);
            return;
        }
        finally
        {
            if (httpContext.Response.StatusCode != StatusCodes.Status429TooManyRequests)
            {
                // 9. Post-Stream Settlement & Response Metadata
                usageRecorder?.RecordUsage(tableId, tenant, userSid, ConsumerOutcome.Allow, rowsEmitted, rowsMeasured: true);
                SetResponseMetadata(httpContext.Response, "X-Rows-Streamed", rowsEmitted.ToString(CultureInfo.InvariantCulture));
                if (!string.IsNullOrEmpty(nextCursor))
                {
                    SetResponseMetadata(httpContext.Response, "X-Next-Cursor", nextCursor);
                }
                SetResponseMetadata(httpContext.Response, "X-Has-More", hasMore ? "true" : "false");
                SetResponseMetadata(httpContext.Response, "X-Stream-Truncated", truncatedReason ?? "false");

                if (rateLimiter != null)
                {
                    int actualCost = baseCost + (rowsEmitted * costPerRow);
                    var settlement = await rateLimiter.SettleCostQuotaAsync(subjectId, reservedCost, actualCost, policy, ct).ConfigureAwait(false);
                    SetResponseMetadata(httpContext.Response, "X-RateLimit-Remaining", settlement.RemainingTokens.ToString(CultureInfo.InvariantCulture));
                }
            }
        }
    }

    private static void SetResponseMetadata(HttpResponse response, string key, string value)
    {
        if (!response.HasStarted)
        {
            response.Headers[key] = value;
        }

        try
        {
            if (response.SupportsTrailers())
            {
                response.AppendTrailer(key, value);
            }
        }
        catch
        {
            // Ignore in environments where trailers are unsupported
        }
    }

    private static void WriteJsonValue(Utf8JsonWriter writer, object? val)
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
                writer.WriteStringValue(dt.ToString("o", CultureInfo.InvariantCulture));
                break;
            case DateTimeOffset dto:
                writer.WriteStringValue(dto.ToString("o", CultureInfo.InvariantCulture));
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
