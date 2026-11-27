namespace Autheris.Application.Sql.Services;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common;
using Autheris.Application.Common.Quota;
using Autheris.Application.Security;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Internal;
using Autheris.Application.Sql.Policy;
using Autheris.Application.Sql.ResultCache;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using TrinoSqlEngine.Ast.Emit;

/// <summary>
/// The WebSQL result cache in the governed executor. The cache only replaces the source execution: the request is authenticated,
/// rewritten, authorized, admitted and audited exactly as without a cache, and a hit writes the same required ALLOW record before the
/// first row. Every uncertainty (key, epochs, eligibility, store) means "run uncached".
/// </summary>
public sealed partial class GovernedSqlExecutor
{
    private readonly IWebSqlResultCache? _resultCache;

    /// <summary>The cache decision of one request, made right after the rewrite. <see cref="Key"/> is null when the request is ineligible.</summary>
    internal sealed class ResultCachePlan(WebSqlResultCacheRequest request, TimeSpan ttl)
    {
        public WebSqlResultCacheRequest Request { get; } = request;

        public TimeSpan Ttl { get; } = ttl;

        public WebSqlResultCacheKey? Key { get; set; }

        public string? IneligibleReason { get; set; }

        /// <summary>The full statement behind a paged request; null for a plain request.</summary>
        public GovernedRewrite? FullRewrite { get; set; }

        public QueryCostAdmission? Admission { get; set; }

        public int Offset { get; set; }

        public int WindowSize { get; set; }

        public bool Paged => FullRewrite is not null;

        public bool Bypass => Request.Mode == WebSqlResultCacheMode.Bypass;
    }

    /// <summary>The lookup of one request together with the recording state of a fill leader. Dispose releases the flight and the reservation.</summary>
    internal sealed class ResultCacheFill(ResultCachePlan plan, WebSqlResultCacheLookup lookup) : IDisposable
    {
        public ResultCachePlan Plan { get; } = plan;

        public WebSqlResultCacheLookup Lookup { get; } = lookup;

        public bool IsLeader => Lookup.IsFillLeader && Lookup.Hit is null;

        public IResultCacheReservation? Reservation { get; set; }

        /// <summary>Set before the writer runs on a hit, so a failing writer still settles the cost as served from the cache.</summary>
        public bool Served { get; set; }

        public ResultCacheRecordingReader? Recorder { get; set; }

        public PagedResultCollector? Collector { get; set; }

        public DbDataReader? Window { get; set; }

        public GovernedResultInfo? Info { get; set; }

        /// <summary>Whether rows exist after the served page of a paged fill; null when no page window was cut.</summary>
        public bool? WindowHasMoreRows => Window is IRowLimitProbe probe ? probe.HasMoreRows : null;

        public void Dispose()
        {
            Reservation?.Dispose();
            Lookup.Dispose();
        }
    }

    internal async Task<ResultCachePlan?> PlanResultCacheAsync(
        GovernedSqlQueryRequest request,
        ClaimsPrincipal user,
        TenantId tenantId,
        GovernedRewrite rewrite,
        DmlAuditContext dmlContext,
        IReadOnlyDictionary<string, object?> clientValues,
        string sqlForRewrite,
        string? requestedDs,
        ClientListShapes shapes,
        CancellationToken ct,
        ClientNullShapes? nullShapes = null)
    {
        var cache = _resultCache;
        if (request.ResultCache is not { } participation || cache is null || !cache.Limits.Enabled)
        {
            return null;
        }

        var connOptions = _dataSourceResolver.ResolveConnectionOptions(rewrite.DataSourceName);
        var ttlSeconds = Math.Clamp(connOptions?.ResultCacheTtlSeconds ?? 0, 0, WebSqlOptions.ResultCacheHardMaxTtlSeconds);
        if (connOptions is null || ttlSeconds <= 0)
        {
            return null;
        }

        var plan = new ResultCachePlan(participation, TimeSpan.FromSeconds(ttlSeconds));
        try
        {
            return await BuildResultCachePlanAsync(plan, cache, connOptions, request, user, tenantId, rewrite, dmlContext, clientValues, sqlForRewrite, requestedDs, shapes, nullShapes, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            _logger?.LogDebug("WebSQL result cache planning failed ({ExceptionType}); the request runs uncached.", ex.GetType().Name);
            return Ineligible(plan, rewrite.DataSourceName, WebSqlResultCacheReasons.UnkeyableValue);
        }
    }

    private async Task<ResultCachePlan> BuildResultCachePlanAsync(
        ResultCachePlan plan,
        IWebSqlResultCache cache,
        DataSourceConnectionOptions connOptions,
        GovernedSqlQueryRequest request,
        ClaimsPrincipal user,
        TenantId tenantId,
        GovernedRewrite rewrite,
        DmlAuditContext dmlContext,
        IReadOnlyDictionary<string, object?> clientValues,
        string sqlForRewrite,
        string? requestedDs,
        ClientListShapes shapes,
        ClientNullShapes? nullShapes,
        CancellationToken ct)
    {
        string dsName = rewrite.DataSourceName;
        var paged = request.Page is not null;
        if (paged && (rewrite.PagingUnavailable is not null || !rewrite.BindPageOffset || request.Page!.Offset > int.MaxValue))
        {
            return Ineligible(plan, dsName, WebSqlResultCacheReasons.PagingUnavailable);
        }

        if (EvaluateResultCacheEligibility(plan, connOptions, rewrite, dmlContext.IsDml, user, ignorePageOffset: paged) is { } reason)
        {
            return Ineligible(plan, dsName, reason);
        }

        var keyRewrite = rewrite;
        GovernedRewrite? full = null;
        SqlRowLimit? fullLimit = null;
        if (paged)
        {
            var webSql = _options.Value.WebSql;
            long maxOffset = webSql.Paging?.MaxOffset ?? 10_000;
            long rows = Math.Max(cache.Limits.MaxRowsPerEntry + 1L, maxOffset + Math.Max(1L, webSql.MaxAllowedRows) + 1L);
            fullLimit = new SqlRowLimit(rows, rows);
            try
            {
                // The full statement is a second, complete rewrite: it passes consent, row policies and the function allowlist like any other.
                full = await _rewriter.RewriteCoreAsync(
                    sqlForRewrite, user, tenantId, requestedDs, new DmlAuditContext(), ct, fullLimit, probeExtraRow: true,
                    plannedTableRead: request.PlannedTableRead,
                    gatewayLikePatternParameters: GovernedSqlExecutionService.GatewayParameterAliases(request.GatewayParameters),
                    clientLists: shapes, clientNulls: nullShapes, page: null, readOnlyRequired: request.ReadOnlyRequired, transport: request.Transport).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                _logger?.LogDebug("The full statement of a paged WebSQL request was refused ({ExceptionType}); the page runs uncached.", ex.GetType().Name);
                return Ineligible(plan, dsName, WebSqlResultCacheReasons.PagingUnavailable);
            }

            if (!string.Equals(full.DataSourceName, dsName, StringComparison.OrdinalIgnoreCase)
                || EvaluateResultCacheEligibility(plan, connOptions, full, false, user, ignorePageOffset: false) is not null)
            {
                return Ineligible(plan, dsName, WebSqlResultCacheReasons.PagingUnavailable);
            }

            keyRewrite = full;
        }

        if (!TryBuildResultCacheKey(plan, request, user, tenantId, keyRewrite, clientValues, connOptions, paged, out var key))
        {
            return Ineligible(plan, dsName, WebSqlResultCacheReasons.UnkeyableValue);
        }

        if (paged)
        {
            if (cache.IsMarkedTooLarge(key.Hash))
            {
                return Ineligible(plan, dsName, WebSqlResultCacheReasons.TooLarge);
            }

            try
            {
                plan.Admission = await AdmitCostAsync(request with { RowLimit = fullLimit }, user, full!.Metadata, rewrite.IndexHints, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                return Ineligible(plan, dsName, WebSqlResultCacheReasons.PagingUnavailable);
            }

            var page = request.Page!;
            plan.Offset = (int)page.Offset;
            plan.WindowSize = ResultCacheWindowSize(page.Size, rewrite.DeliveredRowLimit);
            plan.FullRewrite = full;
        }

        plan.Key = key;
        return plan;
    }

    /// <summary>The rows of a cached page: the page size, never more than the row limit the uncached page statement would have delivered.</summary>
    internal static int ResultCacheWindowSize(int pageSize, long firstDeliveredRowLimit) =>
        (int)Math.Min(pageSize, firstDeliveredRowLimit > 0 ? firstDeliveredRowLimit : pageSize);

    private ResultCachePlan Ineligible(ResultCachePlan plan, string dsName, string reason)
    {
        plan.IneligibleReason = reason;
        plan.Key = null;
        plan.FullRewrite = null;
        ReportResultCache(plan, dsName, WebSqlResultCacheStatus.Ineligible, null, reason, recordMetric: true);
        return plan;
    }

    private string? EvaluateResultCacheEligibility(
        ResultCachePlan plan,
        DataSourceConnectionOptions connOptions,
        GovernedRewrite rewrite,
        bool isDml,
        ClaimsPrincipal user,
        bool ignorePageOffset)
    {
        var compiled = rewrite.Compiled;
        var hasConnection = _connectionFactory is not null
            && (!string.IsNullOrWhiteSpace(connOptions.ConnectionString) || DataSourceProvider.Is(connOptions.Provider, DatabaseDialect.Databricks));
        var input = new WebSqlResultCacheEligibilityInput(
            GlobalMaxMegabytes: 1,
            SourceTtlSeconds: (int)plan.Ttl.TotalSeconds,
            EnforcementMode: connOptions.EnforcementMode,
            UsesPerUserCredentials: connOptions.Databricks?.Auth.Mode == DatabricksAuthMode.OAuthObo,
            HasConnection: hasConnection,
            IsDml: isDml,
            // Federated statements never reach this executor path, so a cached statement always reads a single source.
            IsCrossSource: false,
            IsSelectStatement: compiled.StatementClass == SqlStatementClass.Select,
            HasRecursiveCte: compiled.HasRecursiveCte,
            HasPageOffsetParameter: !ignorePageOffset && compiled.Parameters.Any(static p => p.Origin == ParameterOrigin.PageOffset),
            CompiledSql: compiled.Sql,
            Dialect: compiled.Dialect,
            FunctionCalls: rewrite.Metadata?.FunctionCalls,
            AccessedTables: rewrite.AccessedTables,
            AllowedTables: connOptions.ResultCacheTables,
            UserSid: user.GetUserSid()?.Value,
            PrincipalKey: GovernancePolicyFactory.ComputePrincipalCacheKey(user));
        return WebSqlResultCacheEligibility.Evaluate(in input);
    }

    private bool TryBuildResultCacheKey(
        ResultCachePlan plan,
        GovernedSqlQueryRequest request,
        ClaimsPrincipal user,
        TenantId tenantId,
        GovernedRewrite rewrite,
        IReadOnlyDictionary<string, object?> clientValues,
        DataSourceConnectionOptions connOptions,
        bool paged,
        out WebSqlResultCacheKey key)
    {
        key = null!;
        try
        {
            var connectionKey = _dataSourceResolver.ResolveConnectionKey(rewrite.DataSourceName);
            if (connectionKey is null)
            {
                return false;
            }

            var compiled = rewrite.Compiled;
            var input = new WebSqlResultCacheKeyInput(
                tenantId,
                user.GetUserSid()?.Value,
                GovernancePolicyFactory.ComputePrincipalCacheKey(user),
                rewrite.DataSourceName,
                rewrite.DataSourceName,
                connectionKey,
                ResultCacheConnectionFingerprint(connOptions),
                compiled.Dialect,
                compiled.CompilerVersion,
                compiled.Sql,
                paged ? WebSqlResultCacheKeyMode.PagedFull : WebSqlResultCacheKeyMode.Plain,
                rewrite.DeliveredRowLimit,
                plan.Request.Format,
                request.Transport,
                compiled.OutputColumnMasks,
                compiled.Parameters,
                clientValues,
                rewrite.MetricViewMinGroupSize,
                rewrite.MetricViewHiddenCountAlias,
                Purpose: null,
                rewrite.AccessedTables);
            return WebSqlResultCacheKeyBuilder.TryBuild(input, out key);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            return false;
        }
    }

    /// <summary>A digest of the connection settings that decide which data a statement sees. It is part of the key and never logged.</summary>
    private static string ResultCacheConnectionFingerprint(DataSourceConnectionOptions connOptions)
    {
        var db = connOptions.Databricks;
        var text = string.Join(
            '\u001f',
            connOptions.Provider,
            connOptions.ConnectionString,
            connOptions.ServerName,
            connOptions.ReadUncommitted ? "1" : "0",
            db?.Host,
            db?.WarehouseId,
            db?.Catalog,
            db?.Auth.Mode.ToString(),
            db?.Auth.ClientId,
            WindowsAuthFingerprint(connOptions));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private static string WindowsAuthFingerprint(DataSourceConnectionOptions connOptions) =>
        connOptions.WindowsAuth is { } w ? JsonSerializer.Serialize(w) : string.Empty;

    private async Task<ResultCacheFill?> LookupResultCacheAsync(ResultCachePlan? plan, string dsName, CancellationToken ct)
    {
        if (plan?.Key is not { } key || _resultCache is not { } cache)
        {
            return null;
        }

        WebSqlResultCacheLookup lookup;
        try
        {
            var followerWait = TimeSpan.FromSeconds(Math.Clamp(_options.Value.WebSql.ExecutionTimeoutSeconds, 1, 30));
            lookup = await cache.LookupAsync(key, plan.Bypass, followerWait, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            _logger?.LogDebug("WebSQL result cache lookup failed ({ExceptionType}); the request runs uncached.", ex.GetType().Name);
            lookup = WebSqlResultCacheLookup.ForUncached(WebSqlResultCacheStatus.Ineligible, WebSqlResultCacheReasons.EpochUnavailable);
        }

        var fill = new ResultCacheFill(plan, lookup);
        if (lookup.Hit is { } hit)
        {
            ReportResultCache(plan, dsName, WebSqlResultCacheStatus.Hit, hit.AgeSeconds, null, recordMetric: true);
        }
        else if (lookup.Status == WebSqlResultCacheStatus.Ineligible)
        {
            ReportResultCache(plan, dsName, WebSqlResultCacheStatus.Ineligible, null, lookup.Reason, recordMetric: true);
        }
        else
        {
            ReportResultCache(plan, dsName, plan.Bypass ? WebSqlResultCacheStatus.Bypass : WebSqlResultCacheStatus.Miss, null, null, recordMetric: true);
        }

        return fill;
    }

    /// <summary>The statement to execute or to audit: the full statement for a paged request that is served from, or fills, the cache.</summary>
    private static GovernedRewrite ResultCacheExecutionRewrite(GovernedRewrite rewrite, ResultCachePlan? plan, ResultCacheFill? fill) =>
        plan?.FullRewrite is { } full && fill is { Lookup: { } lookup } && (lookup.Hit is not null || lookup.IsFillLeader) ? full : rewrite;

    private static void AddResultCacheAuditFields(Dictionary<string, object?> fields, ResultCachePlan? plan, ResultCacheFill? fill)
    {
        if (plan is null)
        {
            return;
        }

        if (fill?.Lookup is not { } lookup)
        {
            fields["resultCache"] = "ineligible";
            fields["resultCacheReason"] = plan.IneligibleReason;
            return;
        }

        if (lookup.Hit is { } hit)
        {
            fields["resultCache"] = "hit";
            fields["resultCacheAgeSeconds"] = hit.AgeSeconds;
            fields["resultCacheStoredTraceId"] = hit.StoredTraceId;
            return;
        }

        fields["resultCache"] = lookup.Status switch
        {
            WebSqlResultCacheStatus.Ineligible => "ineligible",
            WebSqlResultCacheStatus.Bypass => "bypass",
            _ => plan.Bypass ? "bypass" : "miss",
        };
        if (lookup.Reason is not null)
        {
            fields["resultCacheReason"] = lookup.Reason;
        }
    }

    private async Task<bool> TryServeResultCacheAsync(
        GovernedSqlQueryRequest request,
        ClaimsPrincipal user,
        TenantId tenantId,
        GovernedRewrite rewrite,
        ResultCachePlan? plan,
        ResultCacheFill? fill,
        Func<DbDataReader, GovernedResultInfo, CancellationToken, Task> rowWriter,
        CancellationToken ct)
    {
        if (plan is null || fill?.Lookup.Hit is not { } hit)
        {
            return false;
        }

        // A hit skips the connection and the provider guard only; the per-user and per-table read limit applies like on a miss.
        var leases = await AcquireReadLeasesAsync(user, tenantId, rewrite, ct).ConfigureAwait(false);
        try
        {
            fill.Served = true;
            using var reader = plan.Paged
                ? new CachedResultDataReader(hit.Result, plan.Offset, plan.WindowSize)
                : new CachedResultDataReader(hit.Result);
            await rowWriter(reader, hit.Result.Info, ct).ConfigureAwait(false);
            NotifyPageOutcome(request, user, tenantId, rewrite, reader.HasMoreRows);
            return true;
        }
        finally
        {
            DisposeLeases(leases);
        }
    }

    /// <summary>Takes the per-user and per-table read leases, or throws the throttled error when one is exhausted. Null when no limit applies.</summary>
    private async ValueTask<List<IDisposable>?> AcquireReadLeasesAsync(ClaimsPrincipal user, TenantId tenantId, GovernedRewrite rewrite, CancellationToken ct)
    {
        var maxConcurrentReads = _options.Value.DataSources?.MaxConcurrentReadsPerUserAndTable ?? 0;
        if (_concurrencyGate == null || maxConcurrentReads <= 0 || rewrite.AccessedTables.Count == 0)
        {
            return null;
        }

        var userSid = user.FindFirst(ClaimTypes.PrimarySid)?.Value
                      ?? user.FindFirst("sub")?.Value
                      ?? "anonymous";
        var keys = new List<string>(rewrite.AccessedTables.Count);
        foreach (var table in rewrite.AccessedTables)
        {
            keys.Add($"{tenantId.Value}|{userSid}|{table.ToQualifiedName()}".ToLowerInvariant());
        }

        var wait = TimeSpan.FromMilliseconds(_options.Value.DataSources?.TableReadWaitTimeoutMs ?? 0);
        var lease = await _concurrencyGate.EnterAsync(keys, maxConcurrentReads, wait, ct).ConfigureAwait(false)
                    ?? throw new GatewayThrottledException(ThrottledRetryAfterSeconds);
        return [lease];
    }

    private static void DisposeLeases(List<IDisposable>? leases)
    {
        if (leases is null)
        {
            return;
        }

        foreach (var lease in leases)
        {
            lease.Dispose();
        }
    }

    /// <summary>
    /// Wraps the outermost reader of a fill leader (after the metric-view filter and the row limit), so the recorded rows are exactly the
    /// rows the client receives. A paged fill reads the full statement into the collector and hands the writer the requested window.
    /// </summary>
    private Task<DbDataReader> PrepareResultCacheReaderAsync(ResultCacheFill? fill, DbDataReader output, GovernedResultInfo info, CancellationToken ct) =>
        fill is not { IsLeader: true } || _resultCache is null || fill.Plan.Key is null
            ? Task.FromResult(output)
            : PrepareResultCacheLeaderReaderAsync(fill, output, info, ct);

    private async Task<DbDataReader> PrepareResultCacheLeaderReaderAsync(ResultCacheFill fill, DbDataReader output, GovernedResultInfo info, CancellationToken ct)
    {
        var cache = _resultCache!;
        var key = fill.Plan.Key!;

        var limits = cache.Limits;
        fill.Info = info;
        fill.Reservation = cache.TryReserve(key);
        if (fill.Reservation is null)
        {
            // Nothing is reserved, so nothing is recorded: the response streams without a collector or recorder.
            return output;
        }

        if (!fill.Plan.Paged)
        {
            fill.Recorder = new ResultCacheRecordingReader(output, limits.MaxRowsPerEntry, limits.MaxEntryBytes, fill.Reservation);
            return fill.Recorder;
        }

        fill.Collector = new PagedResultCollector(fill.Plan.Offset, fill.Plan.WindowSize, limits.MaxRowsPerEntry, limits.MaxEntryBytes, fill.Reservation);
        await fill.Collector.CollectAsync(output, ct).ConfigureAwait(false);
        fill.Window = fill.Collector.CreateWindowReader(info);
        return fill.Window;
    }

    /// <summary>Stores a complete result after the provider guard returned normally. Anything else (error, cancel, early stop, overflow) stores nothing.</summary>
    private async Task CommitResultCacheAsync(ResultCacheFill? fill, string dsName, CancellationToken ct)
    {
        if (fill is not { IsLeader: true, Reservation: not null } || fill.Info is not { } info || _resultCache is not { } cache || ct.IsCancellationRequested)
        {
            return;
        }

        var plan = fill.Plan;
        CachedWebSqlResult? result = null;
        string? reason = null;
        if (fill.Collector is { } collector)
        {
            if (!collector.Complete || !collector.TryBuildFullResult(info, out result))
            {
                reason = collector.OverflowReason ?? WebSqlResultCacheReasons.Incomplete;
                if (reason is WebSqlResultCacheReasons.TooLarge or WebSqlResultCacheReasons.UnsupportedValueType)
                {
                    cache.MarkTooLarge(fill.Lookup, plan.Ttl);
                }
            }
        }
        else if (fill.Recorder is { } recorder && !recorder.TryBuildResult(info, out result, out reason))
        {
            result = null;
        }

        if (result is null)
        {
            ReportResultCache(plan, dsName, plan.Bypass ? WebSqlResultCacheStatus.Bypass : WebSqlResultCacheStatus.Miss, null, reason, recordMetric: false);
            return;
        }

        if (await cache.TryCommitAsync(fill.Lookup, result, plan.Ttl, dsName, TraceContextResolver.GetCurrentTraceId(), ct, fill.Reservation).ConfigureAwait(false))
        {
            if (!plan.Bypass)
            {
                ReportResultCache(plan, dsName, WebSqlResultCacheStatus.Store, null, null, recordMetric: false);
            }

            return;
        }

        var failure = fill.Lookup.Reason;
        if (failure == WebSqlResultCacheReasons.GovernanceChanged)
        {
            ReportResultCache(plan, dsName, WebSqlResultCacheStatus.Ineligible, null, failure, recordMetric: true);
            return;
        }

        ReportResultCache(plan, dsName, plan.Bypass ? WebSqlResultCacheStatus.Bypass : WebSqlResultCacheStatus.Miss, null, failure, recordMetric: false);
    }

    /// <summary>Counts the outcome (reason codes only) and tells the caller. A failing observer never fails the request.</summary>
    private static void ReportResultCache(ResultCachePlan plan, string dsName, WebSqlResultCacheStatus status, int? ageSeconds, string? reason, bool recordMetric)
    {
        if (recordMetric)
        {
            WebSqlResultCacheMetrics.RecordRequest(dsName, status, reason);
        }

        try
        {
            plan.Request.OnOutcome?.Invoke(new WebSqlResultCacheOutcome(status, ageSeconds, reason));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The observer belongs to the caller; its failure must not change the response or the store.
        }
    }
}
