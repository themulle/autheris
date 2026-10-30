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
using Autheris.Api.Errors;
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

    /// <summary>One byte-identical body for every table that is missing, hidden or not authorized (SEC-DATA178-04, NL-SC-22).</summary>
    private static IResult TableNotFound() =>
        ApiProblems.Indistinguishable(StatusCodes.Status404NotFound, ApiErrorCodes.ResourceNotFound, "The table was not found.");

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
            await ApiProblems.WriteAsync(httpContext, StatusCodes.Status404NotFound, ApiErrorCodes.ResourceNotFound, "The bulk streaming endpoint is not available.", ct).ConfigureAwait(false);
            return;
        }

        // 1. Validate route identifiers
        if (string.IsNullOrWhiteSpace(domain) || string.IsNullOrWhiteSpace(schema) || string.IsNullOrWhiteSpace(table) ||
            domain.Contains('/') || schema.Contains('/') || table.Contains('/') ||
            domain.Contains('\\') || schema.Contains('\\') || table.Contains('\\') ||
            domain.Contains(';') || schema.Contains(';') || table.Contains(';'))
        {
            await ApiProblems.WriteAsync(httpContext, StatusCodes.Status400BadRequest, ApiErrorCodes.BadRequest, "The domain, schema and table must be valid single segment identifiers.", ct).ConfigureAwait(false);
            return;
        }

        // SEC-DATA178-05: Reject OFFSET pagination
        if (httpContext.Request.Query.ContainsKey("offset"))
        {
            await ApiProblems.WriteAsync(httpContext, StatusCodes.Status400BadRequest, ApiErrorCodes.OffsetNotSupported, "OFFSET pagination is not supported on the bulk keyset streaming endpoint. Use cursor-based keyset pagination instead.", ct).ConfigureAwait(false);
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
                await ApiProblems.WriteAsync(httpContext, StatusCodes.Status400BadRequest, ApiErrorCodes.InvalidJson, "The request body is not valid JSON.", ct).ConfigureAwait(false);
                return;
            }
        }
        else
        {
            request = new BulkTableStreamRequest();
        }

        if (request.AdditionalProperties != null && request.AdditionalProperties.ContainsKey("offset"))
        {
            await ApiProblems.WriteAsync(httpContext, StatusCodes.Status400BadRequest, ApiErrorCodes.OffsetNotSupported, "OFFSET pagination is not supported on the bulk keyset streaming endpoint. Use cursor-based keyset pagination instead.", ct).ConfigureAwait(false);
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
            await TableNotFound().ExecuteAsync(httpContext).ConfigureAwait(false);
            return;
        }

        if (metadata.Table.Visibility == DatasetVisibility.Hidden && !httpContext.User.IsInRole("admin"))
        {
            await TableNotFound().ExecuteAsync(httpContext).ConfigureAwait(false);
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
            await TableNotFound().ExecuteAsync(httpContext).ConfigureAwait(false);
            return;
        }

        // Anti-enumeration parity: unauthorized table returns 404
        if (!decision.IsAllowed)
        {
            await TableNotFound().ExecuteAsync(httpContext).ConfigureAwait(false);
            return;
        }

        // 5. Enforce Primary Key & Masking Gating (SEC-DATA178-04, SEC-DATA178-07)
        var streamKey = BulkStreamKey.TryResolve(metadata);
        if (streamKey is null)
        {
            await ApiProblems.WriteAsync(httpContext, StatusCodes.Status400BadRequest, ApiErrorCodes.KeysetStreamingUnavailable, "The table requires a primary key or unique index for keyset streaming.", ct).ConfigureAwait(false);
            return;
        }

        IReadOnlyList<string> pkCols = streamKey.Columns;

        foreach (var pk in pkCols)
        {
            var access = decision.GetEffectiveColumnAccess(pk, metadata);
            if (access == ColumnAccessLevel.Mask || access == ColumnAccessLevel.Deny)
            {
                await ApiProblems.WriteAsync(httpContext, StatusCodes.Status400BadRequest, ApiErrorCodes.KeysetStreamingUnavailable, "A primary key column is masked or denied for the caller.", ct).ConfigureAwait(false);
                return;
            }
        }

        // 6. Plan the Keyset Stream Query
        PlannedTableRead planned;
        try
        {
            planned = planner.PlanKeysetStream(request, metadata, decision, tenant.Value, callerSid);
        }
        catch (InvalidCursorException ex)
        {
            logger.LogWarning(ex, "Invalid cursor in bulk stream request");
            await ApiProblems.WriteAsync(httpContext, StatusCodes.Status400BadRequest, ApiErrorCodes.InvalidCursor, "The supplied cursor is invalid.", ct).ConfigureAwait(false);
            return;
        }
        catch (RequiredFilterColumnMissingException ex)
        {
            logger.LogWarning(ex, "Required filter column missing in bulk stream request");
            await ApiProblems.WriteAsync(httpContext, StatusCodes.Status400BadRequest, ApiErrorCodes.RequiredFilterColumnMissing, "A required filter column is missing.", ct).ConfigureAwait(false);
            return;
        }
        catch (KeysetStreamingUnavailableException ex)
        {
            logger.LogWarning(ex, "Keyset streaming unavailable for bulk stream request");
            await ApiProblems.WriteAsync(httpContext, StatusCodes.Status400BadRequest, ApiErrorCodes.KeysetStreamingUnavailable, "Keyset streaming is unavailable for this query.", ct).ConfigureAwait(false);
            return;
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning(ex, "Invalid argument in bulk stream request");
            await ApiProblems.WriteAsync(httpContext, StatusCodes.Status400BadRequest, ApiErrorCodes.BadRequest, "The streaming arguments are invalid.", ct).ConfigureAwait(false);
            return;
        }

        // 7. Token Bucket Admission (Phase 1: Upfront Reservation)
        var rateLimiter = httpContext.RequestServices!.GetService<IRateLimiterService>();
        var tierResolver = httpContext.RequestServices!.GetService<IClientTierResolver>();
        const int baseCost = 10;
        const int costPerRow = 1;
        const int maxSliceLimit = 10000;
        const int defaultSliceLimit = 5000;

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

        // I418-SC-02/03: the slice must fit the caller's bucket (capacity - base cost). Long arithmetic avoids overflow;
        // a capacity at or below the base cost can never admit a slice and fails closed.
        long maxAdmissibleLimit = Math.Min((long)policy.MaxTokensCapacity - baseCost, maxSliceLimit);
        if (maxAdmissibleLimit < 1)
        {
            await ApiProblems.WriteAsync(httpContext, StatusCodes.Status400BadRequest, ApiErrorCodes.BadRequest, "The streaming request cannot be admitted for this caller.", ct).ConfigureAwait(false);
            return;
        }

        int sliceLimit;
        if (request.Limit is > 0)
        {
            int requested = Math.Min(request.Limit.Value, maxSliceLimit);
            if (requested > maxAdmissibleLimit)
            {
                await ApiProblems.WriteAsync(httpContext, StatusCodes.Status400BadRequest, ApiErrorCodes.BadRequest, $"The limit must not exceed {maxAdmissibleLimit.ToString(CultureInfo.InvariantCulture)} for this caller.", ct).ConfigureAwait(false);
                return;
            }

            sliceLimit = requested;
        }
        else
        {
            sliceLimit = (int)Math.Min(defaultSliceLimit, maxAdmissibleLimit);
        }

        int reservedCost = baseCost + (sliceLimit * costPerRow);

        if (rateLimiter != null)
        {
            var reservation = await rateLimiter.ReserveCostQuotaAsync(subjectId, reservedCost, policy, ct).ConfigureAwait(false);
            if (!reservation.Allowed)
            {
                usageRecorder?.RecordUsage(tableId, tenant, userSid, ConsumerOutcome.DenyRateLimit, 0, false);
                if (!string.IsNullOrWhiteSpace(clientContext?.ResolvedScope))
                {
                    httpContext.Response.Headers["X-RateLimit-Scope"] = clientContext.ResolvedScope;
                }
                await ApiProblems.WriteTooManyRequests(httpContext, ApiErrorCodes.TooManyRequests, "Rate limit exceeded. Wait until the next time window.", reservation.RetryAfterSeconds, ct).ConfigureAwait(false);
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
                                var scoped = ScopeRuleForTenant(rule, tenant.Value, maskingProvider.DefaultHmacKeyId);
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
                    // Same factory and encoder the planner verifies continuation cursors with.
                    var cursorContext = KeysetCursorContextFactory.ForBulkStream(tenant.Value, metadata, pkCols, callerSid, request, streamKey.Directions);
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
                                var scoped = ScopeRuleForTenant(rule, tenant.Value, maskingProvider.DefaultHmacKeyId);
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
                    // Same factory and encoder the planner verifies continuation cursors with.
                    var cursorContext = KeysetCursorContextFactory.ForBulkStream(tenant.Value, metadata, pkCols, callerSid, request, streamKey.Directions);
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
            await ApiProblems.WriteTooManyRequests(httpContext, ApiErrorCodes.TooManyRequests, "Too many concurrent requests. Retry later.", ex.RetryAfterSeconds, ct).ConfigureAwait(false);
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
                    // CancellationToken.None: on a client abort the request token is already cancelled; settling with it
                    // would throw from this finally block, mask the original exception and never refund the reservation.
                    var settlement = await rateLimiter.SettleCostQuotaAsync(subjectId, reservedCost, actualCost, policy, CancellationToken.None).ConfigureAwait(false);
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
