namespace Autheris.Application.Olap;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Connectors;
using Autheris.Application.Connectors.CrossDomain;
using Autheris.Application.Federation;
using Autheris.Application.Interfaces;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Connectors;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TrinoSqlEngine;
using TrinoSqlEngine.Analysis;

/// <summary>
/// Default <see cref="IOlapStagingService"/>. Audit failures reject the request (fail closed, 503).
/// Request flow (PLAN-SEC-OLAP-01): the SQL text is validated first (DuckDB parser), then every requested table is resolved and
/// authorized (no source is touched), the request is classified, a cross-source request passes the federation gate and the SQL
/// guardrails, and only then are admission, the governed reads, the required audits and the query executed.
/// </summary>
public sealed class OlapStagingService(
    IDuckDbOlapEngine olapEngine,
    ITableMetadataRepository metadataRepository,
    IAutherisConnectorRegistry connectorRegistry,
    ICrossDomainAccessResolver accessResolver,
    IColumnMaskingProvider maskingProvider,
    IOptions<GatewayOptions> gatewayOptions,
    IAuditWriter auditWriter,
    ILogger<OlapStagingService> logger,
    IFederationGate federationGate,
    ISqlEngine sqlEngine,
    IOlapRequestLimiter requestLimiter) : IOlapStagingService
{
    // Throttle for capacity-refusal audit records: one per tenant per minute (a refusal flood must not flood the audit store).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> LastCapacityAudit = new(StringComparer.OrdinalIgnoreCase);

    // Same token guards as the SQL federation path; comments, escape strings and non-ASCII identifiers are not accepted.
    private static readonly SqlTokenSecurityOptions AnalysisTokenOptions = new()
    {
        RejectComments = true,
        RejectBackslashInStrings = true,
        RejectEscapedStringLiterals = true,
        RejectNonAsciiIdentifiers = true,
        RejectDotsInQuotedIdentifiers = true,
        RejectTimeTravelQueries = true
    };

    private sealed record ResolvedTable(string RequestedName, TableMetadata Meta, TableAccessDecision Decision, IAutherisConnector Connector);

    private sealed record Denial(OlapStagingOutcome Outcome);

    public async Task<OlapStagingOutcome> ExecuteAsync(
        ClaimsPrincipal user,
        SecurityPrincipalContext caller,
        OlapQueryCommand command,
        bool isDevelopment,
        string traceId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(command);

        var options = gatewayOptions.Value.DuckDbOlap ?? new DuckDbOlapOptions();
        if (string.IsNullOrWhiteSpace(caller.TenantId.Value))
        {
            return new OlapStagingOutcome(403, "Forbidden: Missing tenant identity.");
        }

        // BC-OLAP-09b: the request timeout starts before authorization and covers the source reads and the query.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (options.QueryTimeoutSeconds > 0)
        {
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(options.QueryTimeoutSeconds));
        }

        try
        {
            return await RunAsync(user, caller, command, isDevelopment, traceId, options, timeoutCts.Token, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeoutCts.IsCancellationRequested)
        {
            logger.LogWarning("OLAP request timed out.");
            return new OlapStagingOutcome(504, "The analytical request timed out.", ErrorCode: "OLAP_TIMEOUT");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // BC-OLAP-12: nothing escapes ExecuteAsync; the mapping never exposes exception text outside Development.
            return MapException(ex, isDevelopment);
        }
    }

    private OlapStagingOutcome MapException(Exception ex, bool isDev)
    {
        switch (ex)
        {
            case OlapSqlRejectedException rejected:
                logger.LogWarning("OLAP SQL rejected: {Code}", rejected.Code);
                return new OlapStagingOutcome(
                    rejected.Code == OlapSqlRejectedException.ColumnPolicy ? 403 : 400,
                    rejected.Message,
                    isDev ? rejected.Message : null,
                    ErrorCode: rejected.Code);
            case GatewayForbiddenException:
                logger.LogWarning(ex, "OLAP read denied by the governed pipeline.");
                return new OlapStagingOutcome(403, "Access denied.", ErrorCode: "OLAP_ACCESS_DENIED");
            case GatewaySecurityException security when string.Equals(security.ErrorCode, "RESPONSE_TOO_LARGE", StringComparison.Ordinal):
                logger.LogWarning(ex, "OLAP staging byte budget exceeded.");
                return new OlapStagingOutcome(400, "The requested tables exceed the maximum allowed staging size.", ErrorCode: "OLAP_STAGING_TOO_LARGE");
            case GatewaySecurityException:
                logger.LogWarning(ex, "OLAP read denied by the governed pipeline.");
                return new OlapStagingOutcome(403, "Access denied.", ErrorCode: "OLAP_ACCESS_DENIED");
            default:
                logger.LogError(ex, "OLAP request failed.");
                return new OlapStagingOutcome(502, "The analytical request could not be completed.", isDev ? ex.Message : null, ErrorCode: "OLAP_FAILED");
        }
    }

    private async Task<OlapStagingOutcome> RunAsync(
        ClaimsPrincipal user,
        SecurityPrincipalContext caller,
        OlapQueryCommand command,
        bool isDev,
        string traceId,
        DuckDbOlapOptions options,
        CancellationToken token,
        CancellationToken ct)
    {
        var tenantId = caller.TenantId;
        var actorSid = caller.UserSid;
        var crossOptions = gatewayOptions.Value.WebSql?.CrossSource ?? new CrossSourceOptions();

        var sqlHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(command.Sql)));
        int sqlLength = command.Sql.Length;

        // SEC D-4: every staged table and every query is audited BEFORE data is returned; an audit failure fails the request closed.
        // A denial record is required too, but a failed denial write never turns the denial into an allow.
        async Task<bool> TryAuditAsync(string eventType, string targetTable, string decision, object details)
        {
            try
            {
                await auditWriter.RecordRequiredAsync(new AuditLogEntry
                {
                    TenantId = tenantId,
                    EventType = eventType,
                    ActorSid = actorSid,
                    TargetTable = targetTable,
                    Decision = decision,
                    TraceId = traceId,
                    DetailsJson = JsonSerializer.Serialize(details)
                }, ct).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "OLAP audit write failed (event {EventType}).", eventType);
                return false;
            }
        }

        static OlapStagingOutcome AuditFailure() =>
            new(503, "Audit unavailable; the analytical request was rejected.");

        // BC-OLAP-11: one required audit record per denial after authorization.
        async Task<OlapStagingOutcome> DenyAsync(
            int status, string code, string message, string reason, bool crossSource, int tableCount, string targetTable, string? detail = null)
        {
            await TryAuditAsync("OLAP_DENIED", targetTable, "DENY", new
            {
                tenant = tenantId.Value,
                sid = actorSid.Value,
                reason,
                code,
                crossSource,
                tableCount,
                sqlSha256 = sqlHash,
                sqlLength,
                traceId
            }).ConfigureAwait(false);
            return new OlapStagingOutcome(status, message, isDev ? detail : null, ErrorCode: code);
        }

        // A refusal because of the concurrency cap is audited at most once per tenant and minute.
        async Task<OlapStagingOutcome> CapacityRefusalAsync(bool crossSourceRequest, int tableCount, string target)
        {
            logger.LogWarning("OLAP request refused: concurrency limit reached.");
            long now = Environment.TickCount64;
            if (LastCapacityAudit.Count > 1000)
            {
                LastCapacityAudit.Clear();
            }

            bool audit = false;
            LastCapacityAudit.AddOrUpdate(
                tenantId.Value,
                _ => { audit = true; return now; },
                (_, last) =>
                {
                    if (now - last >= 60_000)
                    {
                        audit = true;
                        return now;
                    }

                    return last;
                });
            if (audit)
            {
                return await DenyAsync(
                    429, "OLAP_CAPACITY", "OLAP capacity limit reached. Please retry later.", "CAPACITY",
                    crossSourceRequest, tableCount, target).ConfigureAwait(false);
            }

            return new OlapStagingOutcome(429, "OLAP capacity limit reached. Please retry later.", ErrorCode: "OLAP_CAPACITY");
        }

        // BC-OLAP-01 / BC-OLAP-15: the SQL text is bounded and parsed by DuckDB before any table is resolved.
        OlapSqlInspection inspection;
        try
        {
            OlapSqlInspector.EnforceTextFloor(command.Sql, options.MaxSqlLength);
            inspection = OlapSqlInspector.Inspect(command.Sql);
            OlapSqlPolicy.EnforceAll(inspection);
        }
        catch (OlapSqlRejectedException ex)
        {
            logger.LogWarning("OLAP SQL rejected before table resolution: {Code}", ex.Code);
            return new OlapStagingOutcome(400, ex.Message, isDev ? ex.Message : null, ErrorCode: ex.Code);
        }

        var sources = new List<OlapTableSource>();
        var stagedCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        bool crossSource = false;
        long totalStagedBytes = 0;
        IDisposable? lease = null;

        try
        {
            if (command.TableNames != null && command.TableNames.Count > 0)
            {
                int maxTableCount = options.MaxTableCount;
                var requested = command.TableNames
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (requested.Count > maxTableCount)
                {
                    logger.LogWarning("OLAP request exceeded maximum table count: {Count} > {Max}", requested.Count, maxTableCount);
                    return new OlapStagingOutcome(400, $"OLAP request exceeds maximum allowed distinct table count of {maxTableCount}.");
                }

                // Phase A: resolve and authorize EVERY table before the first denial is returned and before any source is read.
                // The denial of the first failing table (request order) is returned with today's text.
                var resolved = new List<ResolvedTable>(requested.Count);
                Denial? firstDenial = null;
                IReadOnlyList<TableMetadata>? catalog = null;
                foreach (var rawName in requested)
                {
                    var (table, denial) = await ResolveAsync(
                        user, tenantId, rawName, isDev, catalog, token).ConfigureAwait(false);
                    if (denial == null && table == null)
                    {
                        // bare name: the catalog was loaded for the first time
                        catalog = await metadataRepository.GetAllTablesAsync(token).ConfigureAwait(false);
                        (table, denial) = await ResolveAsync(user, tenantId, rawName, isDev, catalog, token).ConfigureAwait(false);
                    }

                    if (denial != null)
                    {
                        firstDenial ??= denial;
                    }
                    else if (table != null)
                    {
                        resolved.Add(table);
                    }
                }

                if (firstDenial != null)
                {
                    return firstDenial.Outcome;
                }

                // BC-OLAP-05: the resolved identity, not the request spelling, is what is counted and staged.
                var distinct = new List<ResolvedTable>(resolved.Count);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var table in resolved)
                {
                    if (seen.Add(table.Meta.Identifier.ToString()))
                    {
                        distinct.Add(table);
                    }
                }

                var targetList = string.Join(",", distinct.Select(t => t.Meta.Identifier.ToString()));
                crossSource = CrossSourceClassifier.IsCrossSource(distinct.Select(t => ResolvedSource.From(t.Connector.ConnectorId, t.Meta)));

                // Staged names (table and domain view) must be unique after sanitizing, or a reference could hit the wrong table.
                var stagedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var table in distinct)
                {
                    foreach (var name in OlapStagedNames.For(new OlapTableSource(table.Meta.Identifier, [], table.Meta)))
                    {
                        if (!stagedNames.Add(name))
                        {
                            logger.LogWarning("OLAP request denied: staged table names collide.");
                            return await DenyAsync(
                                403, "OLAP_TABLE_DENIED", $"Access denied to table '{table.RequestedName}'.", "STAGING_NAME_COLLISION",
                                crossSource, distinct.Count, targetList).ConfigureAwait(false);
                        }
                    }
                }

                // Phase B: gate and guardrails for cross-source requests.
                if (crossSource)
                {
                    var phaseB = EnforceCrossSource(distinct, inspection, command.Sql, crossOptions, token);
                    if (phaseB is { } denied)
                    {
                        return await DenyAsync(
                            denied.Status, denied.Code, denied.Message, denied.Reason, true, distinct.Count, targetList, denied.Detail).ConfigureAwait(false);
                    }
                }

                // Phase C: admission, governed reads, required audits.
                lease = await requestLimiter.TryAcquireAsync(tenantId.Value, token).ConfigureAwait(false);
                if (lease == null)
                {
                    return await CapacityRefusalAsync(crossSource, distinct.Count, targetList).ConfigureAwait(false);
                }

                long maxBytesPerTable = Smaller(options.MaxStagedBytesPerTable, crossSource ? crossOptions.MaxStagedBytesPerTable : 0);
                long maxTotalBytes = Smaller(options.MaxTotalStagedBytes, crossSource ? crossOptions.MaxTotalStagedBytes : 0);
                int totalStagedRows = 0;
                int maxTotalStagedRows = options.MaxTotalStagedRows;

                foreach (var table in distinct)
                {
                    var meta = table.Meta;
                    var session = new ConnectorSessionContext(
                        Principal: user,
                        Tenant: tenantId,
                        AccessDecision: table.Decision,
                        ProjectedColumns: meta.Columns.Select(c => c.ColumnName).ToList(),
                        Arguments: new Dictionary<string, object?> { ["limit"] = options.MaxStagedRowsPerTable + 1 },
                        PushdownFilterSql: table.Decision.CombinedRowFilterSql,
                        Limit: options.MaxStagedRowsPerTable + 1,
                        Offset: 0);

                    // Architecture 2 / SQL2-5: the governed reader applies the row filter when the connector did not push it
                    // down, masks exactly once (SQL2-4) and checks the staging capacity before rows are kept (SQL2-2).
                    IReadOnlyList<IReadOnlyDictionary<string, object?>> maskedRows;
                    try
                    {
                        var read = await GovernedConnectorReader.ReadAsync(
                            table.Connector,
                            session,
                            meta,
                            new GovernedRowPolicy(
                                maskingProvider,
                                gatewayOptions.Value.DataMasking?.HmacKeyId,
                                MaskingDisabled: gatewayOptions.Value.IsColumnMaskingDisabled,
                                MaxRows: options.MaxStagedRowsPerTable,
                                MaxBytes: maxBytesPerTable),
                            token).ConfigureAwait(false);
                        maskedRows = read.Rows;
                    }
                    catch (ConnectorRowLimitExceededException)
                    {
                        return new OlapStagingOutcome(400, $"Table '{meta.Identifier}' exceeds maximum allowed staging rows ({options.MaxStagedRowsPerTable}).");
                    }

                    totalStagedRows += maskedRows.Count;
                    if (totalStagedRows > maxTotalStagedRows)
                    {
                        logger.LogWarning("OLAP request exceeded total staged rows limit across tables: {Total} > {Max}", totalStagedRows, maxTotalStagedRows);
                        return new OlapStagingOutcome(400, $"Total staged rows across all tables ({totalStagedRows}) exceeded maximum allowed limit ({maxTotalStagedRows}).");
                    }

                    totalStagedBytes += GovernedConnectorReader.EstimateBytes(maskedRows);
                    if (totalStagedBytes > maxTotalBytes)
                    {
                        logger.LogWarning("OLAP request exceeded total staged bytes limit: {Total} > {Max}", totalStagedBytes, maxTotalBytes);
                        return new OlapStagingOutcome(400, "The requested tables exceed the maximum allowed staging size.", ErrorCode: "OLAP_STAGING_TOO_LARGE");
                    }

                    if (!await TryAuditAsync("OLAP_TABLE_STAGED", meta.Identifier.ToQualifiedName(), "ALLOW", new
                    {
                        tenant = tenantId.Value,
                        sid = actorSid.Value,
                        table = meta.Identifier.ToQualifiedName(),
                        rowCount = maskedRows.Count,
                        sqlSha256 = sqlHash
                    }).ConfigureAwait(false))
                    {
                        return AuditFailure();
                    }

                    stagedCounts[meta.Identifier.ToString()] = maskedRows.Count;

                    // BC-OLAP-08: a denied column is not staged at all, so it behaves like a column that does not exist.
                    var visibleColumns = meta.Columns
                        .Where(c => table.Decision.GetColumnAccess(c.ColumnName) != ColumnAccessLevel.Deny)
                        .ToList();
                    sources.Add(new OlapTableSource(
                        meta.Identifier,
                        maskedRows,
                        meta with { Columns = visibleColumns },
                        MaskedColumns: MaskedColumnSet.For(meta, table.Decision)));
                }

                var sourceLabels = distinct
                    .Select(t => ResolvedSource.From(t.Connector.ConnectorId, t.Meta))
                    .ToList();

                return await ExecuteQueryAsync(
                    new QueryContext(
                        command, sources, crossSource, crossOptions.AllowNonEquiJoins, stagedCounts, totalStagedBytes, sourceLabels,
                        sqlHash, sqlLength, traceId, tenantId.Value, actorSid.Value, isDev, TryAuditAsync, AuditFailure),
                    token).ConfigureAwait(false);
            }

            // A request without tables takes the lease too: the engine work is bounded the same way.
            lease = await requestLimiter.TryAcquireAsync(tenantId.Value, token).ConfigureAwait(false);
            if (lease == null)
            {
                return await CapacityRefusalAsync(false, 0, string.Empty).ConfigureAwait(false);
            }

            return await ExecuteQueryAsync(
                new QueryContext(
                    command, sources, crossSource, crossOptions.AllowNonEquiJoins, stagedCounts, totalStagedBytes, [],
                    sqlHash, sqlLength, traceId, tenantId.Value, actorSid.Value, isDev, TryAuditAsync, AuditFailure),
                token).ConfigureAwait(false);
        }
        finally
        {
            lease?.Dispose();
        }
    }

    /// <summary>The state of one request that the query execution and its audit records need.</summary>
    private sealed record QueryContext(
        OlapQueryCommand Command,
        List<OlapTableSource> Sources,
        bool CrossSource,
        bool AllowNonEquiJoins,
        Dictionary<string, int> StagedCounts,
        long StagedBytes,
        List<ResolvedSource> SourceLabels,
        string SqlHash,
        int SqlLength,
        string TraceId,
        string Tenant,
        string Sid,
        bool IsDev,
        Func<string, string, string, object, Task<bool>> TryAudit,
        Func<OlapStagingOutcome> AuditFailure);

    private async Task<OlapStagingOutcome> ExecuteQueryAsync(QueryContext ctx, CancellationToken token)
    {
        var command = ctx.Command;
        var sources = ctx.Sources;
        var crossSource = ctx.CrossSource;
        var stagedCounts = ctx.StagedCounts;
        var stagedBytes = ctx.StagedBytes;
        var sourceLabels = ctx.SourceLabels;
        var sqlHash = ctx.SqlHash;
        var sqlLength = ctx.SqlLength;
        var traceId = ctx.TraceId;
        var tenant = ctx.Tenant;
        var sid = ctx.Sid;
        var isDev = ctx.IsDev;
        var tryAudit = ctx.TryAudit;
        var auditFailure = ctx.AuditFailure;
        try
        {
            var queryRequest = new OlapQueryRequest(
                command.Sql, sources, command.Limit, CrossSource: crossSource, AllowNonEquiJoins: ctx.AllowNonEquiJoins);
            var result = await olapEngine.ExecuteOlapQueryAsync(queryRequest, token).ConfigureAwait(false);

            if (!await tryAudit("OLAP_QUERY", string.Join(",", stagedCounts.Keys), "ALLOW", new
            {
                tenant,
                sid,
                tables = stagedCounts,
                stagedRowCount = stagedCounts.Values.Sum(),
                stagedBytes,
                resultRowCount = result.Rows.Count,
                totalRows = result.TotalRowCount,
                crossSource,
                transport = SqlTransports.Olap,
                sources = sourceLabels.Select(s => s.SourceLabel).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                connectors = sourceLabels.Select(s => s.ConnectorId).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                sqlSha256 = sqlHash,
                sqlLength,
                traceId
            }).ConfigureAwait(false))
            {
                return auditFailure();
            }

            return new OlapStagingOutcome(200, Result: result);
        }
        catch (OlapSqlRejectedException ex)
        {
            // Defence in depth: the engine re-validates the text; a rejection here means the pre-check and the engine disagreed.
            logger.LogWarning("OLAP engine rejected the SQL: {Code}", ex.Code);
            await tryAudit("OLAP_QUERY_FAILED", string.Join(",", stagedCounts.Keys), "DENY", FailureDetails(ex.Code)).ConfigureAwait(false);
            return new OlapStagingOutcome(
                ex.Code == OlapSqlRejectedException.ColumnPolicy ? 403 : 400, ex.Message, isDev ? ex.Message : null, ErrorCode: ex.Code);
        }
        catch (InvalidOperationException ex) when (HasCode(ex, DuckDbOlapEngine.CodeStagedRows))
        {
            logger.LogWarning(ex, "Max staged rows limit exceeded in OLAP query.");
            return new OlapStagingOutcome(400, ex.Message);
        }
        catch (SecurityException ex) when (HasCode(ex, DuckDbOlapEngine.CodeCapacity))
        {
            logger.LogWarning(ex, "OLAP engine concurrency limit reached.");
            return new OlapStagingOutcome(429, "OLAP capacity limit reached. Please retry later.");
        }
        catch (SecurityException ex) when (HasCode(ex, DuckDbOlapEngine.CodeResultSize))
        {
            logger.LogWarning(ex, "OLAP query result byte budget exceeded.");
            return new OlapStagingOutcome(400, isDev ? ex.Message : "OLAP query result exceeded maximum allowed memory size.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error executing DuckDB OLAP query.");
            if (stagedCounts.Count > 0)
            {
                await tryAudit("OLAP_QUERY_FAILED", string.Join(",", stagedCounts.Keys), "ALLOW", FailureDetails("EXECUTION_FAILED")).ConfigureAwait(false);
            }

            return isDev || gatewayOptions.Value.IsQuickstartProfile
                ? new OlapStagingOutcome(400, "Analytical query execution failed.", ex.Message)
                : new OlapStagingOutcome(400, "Analytical query execution failed.");
        }

        object FailureDetails(string reason) => new
        {
            tenant,
            sid,
            reason,
            tables = stagedCounts,
            crossSource,
            transport = SqlTransports.Olap,
            sqlSha256 = sqlHash,
            sqlLength,
            traceId
        };
    }

    private static bool HasCode(Exception ex, string code) =>
        ex.Data[DuckDbOlapEngine.ErrorCodeKey] is string actual && string.Equals(actual, code, StringComparison.Ordinal);

    // Options are validated at start-up (all limits > 0), so no fallback values are needed here.
    private static long Smaller(long configured, long crossSourceValue) =>
        crossSourceValue > 0 ? Math.Min(configured, crossSourceValue) : configured;

    /// <summary>
    /// Resolves and authorizes one requested table. Returns <c>(null, null)</c> when a bare name needs the catalog list first
    /// (<paramref name="catalog"/> is null); the caller loads it once and calls again.
    /// </summary>
    private async Task<(ResolvedTable? Table, Denial? Denial)> ResolveAsync(
        ClaimsPrincipal user,
        TenantId tenantId,
        string rawTableName,
        bool isDev,
        IReadOnlyList<TableMetadata>? catalog,
        CancellationToken ct)
    {
        TableMetadata? meta = null;
        if (TableIdentifier.TryParse(rawTableName, out var parsedId))
        {
            meta = await metadataRepository.GetTableMetadataAsync(parsedId, ct).ConfigureAwait(false);
        }
        else
        {
            if (catalog == null)
            {
                return (null, null);
            }

            // BC-OLAP-05: a bare name that matches more than one table is as good as unknown (never first-match).
            var matches = catalog.Where(t =>
                string.Equals(t.Identifier.TableName, rawTableName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t.Identifier.ToQualifiedName(), rawTableName, StringComparison.OrdinalIgnoreCase)).ToList();
            meta = matches.Count == 1 ? matches[0] : null;
        }

        if (meta == null || !meta.Table.IsActive)
        {
            logger.LogWarning("OLAP request table '{Table}' not found in metadata catalog.", rawTableName);
            return (null, new Denial(new OlapStagingOutcome(
                isDev ? 404 : 403,
                isDev ? $"Table '{rawTableName}' not found in metadata catalog." : $"Access denied to table '{rawTableName}'.")));
        }

        // SR-H-06: tenant/global data-source allowlists apply before any access decision or read.
        try
        {
            DataSourceAllowlistGuard.EnsureAllowed(
                gatewayOptions.Value.WebSql ?? new WebSqlOptions(), tenantId,
                meta.Identifier.Domain);
        }
        catch (WebSqlPolicyException)
        {
            logger.LogWarning("OLAP request denied: data source of table {Table} is not allowed for the tenant.", meta.Identifier);
            return (null, new Denial(new OlapStagingOutcome(403, $"Access denied to table '{rawTableName}'.")));
        }

        // SEC-OLAP-05 / RR-L3-04 / Architecture 1: one access decision (ReBAC whenever enabled, consents, Casbin, RLS)
        // without leaking internal policy details.
        var decision = await accessResolver.ResolveAccessAsync(user, meta.Identifier, meta, tenantId, ct).ConfigureAwait(false);
        if (!decision.IsAllowed)
        {
            logger.LogWarning("ABAC access denied for table {Table}: {Reasons}", meta.Identifier, string.Join("; ", decision.DeniedReasons));
            return (null, new Denial(new OlapStagingOutcome(
                403,
                isDev ? $"Access denied to table '{meta.Identifier}': {string.Join("; ", decision.DeniedReasons)}" : $"Access denied to table '{rawTableName}'.")));
        }

        bool canUseConnector = connectorRegistry.TryGetConnectorForTable(meta.Identifier, out var connector) &&
            connector != null &&
            (meta.Table.DataSourceType == DataSourceType.Sql ||
             (!string.Equals(connector.ConnectorId, "default-sql", StringComparison.OrdinalIgnoreCase) &&
              !string.Equals(connector.ConnectorId, "sql", StringComparison.OrdinalIgnoreCase)));

        if (!canUseConnector || connector == null)
        {
            logger.LogWarning("No active connector registered for table {Table}", meta.Identifier);
            return (null, new Denial(new OlapStagingOutcome(
                isDev ? 502 : 403,
                isDev ? $"No active connector registered for table '{meta.Identifier}'." : $"Access denied to table '{rawTableName}'.")));
        }

        return (new ResolvedTable(rawTableName, meta, decision, connector), null);
    }

    private sealed record PhaseBDenial(int Status, string Code, string Message, string Reason, string? Detail = null);

    /// <summary>
    /// Phase B (cross-source only): unsupported source types, the federation gate (switch and transport), the cross-source table budget
    /// and the SQL guardrails. Returns the denial, or null when the request may proceed. No source is read here.
    /// </summary>
    private PhaseBDenial? EnforceCrossSource(
        List<ResolvedTable> tables,
        OlapSqlInspection inspection,
        string sql,
        CrossSourceOptions crossOptions,
        CancellationToken ct)
    {
        if (tables.Any(t => t.Meta.Table.DataSourceType is DataSourceType.HttpPlugin or DataSourceType.LakehouseIceberg or DataSourceType.LakehouseDelta))
        {
            return new PhaseBDenial(
                403, "OLAP_SOURCE_UNSUPPORTED", "The requested data source type is not supported for cross-source queries.", "UNSUPPORTED_SOURCE_TYPE");
        }

        try
        {
            federationGate.EnsureAllowed(SqlTransports.Olap);
        }
        catch (WebSqlPolicyException ex)
        {
            logger.LogWarning("OLAP cross-source request denied by the federation gate ({TableCount} tables).", tables.Count);
            return new PhaseBDenial(
                403, "OLAP_CROSS_SOURCE_DENIED", FederationGate.DeniedMessage, "FEDERATION_GATE", ex.Message);
        }

        int maxTables = crossOptions.MaxTableCount;
        if (tables.Count > maxTables)
        {
            return new PhaseBDenial(
                400, "OLAP_TABLE_COUNT", $"Cross-source OLAP request exceeds maximum allowed distinct table count of {maxTables}.", "TABLE_COUNT");
        }

        try
        {
            EnforceSqlGuardrails(tables, inspection, sql, crossOptions, ct);
        }
        catch (OlapSqlRejectedException ex)
        {
            return new PhaseBDenial(
                ex.Code == OlapSqlRejectedException.ColumnPolicy ? 403 : 400, ex.Code, ex.Message, ex.Code, ex.InnerException?.Message);
        }

        return null;
    }

    /// <summary>
    /// BC-OLAP-02/03/17: the DuckDB AST policy (function allowlist, construct blocklist, staged tables only) AND the gateway analyzer
    /// (join/filter column references for SEC-JOIN-01 / SEC-FILTER-01). The column guardrail is a policy-consistency control; the
    /// confidentiality boundary is masking before staging.
    /// </summary>
    private void EnforceSqlGuardrails(
        List<ResolvedTable> tables, OlapSqlInspection inspection, string sql, CrossSourceOptions crossOptions, CancellationToken ct)
    {
        var stagedNames = tables
            .SelectMany(t => OlapStagedNames.For(new OlapTableSource(t.Meta.Identifier, [], t.Meta)))
            .ToList();
        OlapSqlPolicy.EnforceCrossSource(inspection, stagedNames, crossOptions.AllowNonEquiJoins);

        SqlQueryMetadata metadata;
        try
        {
            metadata = sqlEngine.Analyze(sql.AsMemory(), AnalysisTokenOptions, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new OlapSqlRejectedException(OlapSqlRejectedException.NotAnalyzable, "Cross-source OLAP queries must use the gateway SQL dialect.");
        }

        if (metadata.StatementType != SqlStatementType.Select ||
            metadata.TableFunctionCalls is { Count: > 0 } ||
            metadata.HasSessionProperties ||
            metadata.HasInlineFunctionDefinitions ||
            (metadata.FunctionCalls?.Any(f => !OlapSqlPolicy.AllowedFunctions.Contains(f)) ?? false))
        {
            throw new OlapSqlRejectedException(
                OlapSqlRejectedException.ConstructNotAllowed,
                "The SQL statement uses a construct that is not permitted in cross-source OLAP queries.");
        }

        bool hasJoinReferences = metadata.JoinColumnReferences is { Count: > 0 };
        if (!crossOptions.AllowNonEquiJoins && !hasJoinReferences && (metadata.JoinCount > 0 || metadata.ReferencedTables.Count > 1))
        {
            throw new OlapSqlRejectedException(
                OlapSqlRejectedException.ConstructNotAllowed,
                "The SQL statement uses a construct that is not permitted in cross-source OLAP queries.");
        }

        foreach (var target in metadata.ReferencedTables)
        {
            var owner = tables.FirstOrDefault(t =>
                string.Equals(t.Meta.Identifier.TableName, target.TableName, StringComparison.OrdinalIgnoreCase) ||
                OlapStagedNames.For(new OlapTableSource(t.Meta.Identifier, [], t.Meta))
                    .Any(n => string.Equals(n, target.TableName, StringComparison.OrdinalIgnoreCase)));
            if (owner == null)
            {
                throw new OlapSqlRejectedException(
                    OlapSqlRejectedException.ConstructNotAllowed,
                    "The SQL statement uses a construct that is not permitted in cross-source OLAP queries.");
            }

            try
            {
                GovernedSqlExecutionService.EnforceMaskedColumnGuardrails(target, owner.Meta, owner.Decision, metadata);
            }
            catch (WebSqlPolicyException ex)
            {
                // The guardrail text names column, table and rule; it stays in the log (and in Development details), never in the body.
                logger.LogWarning("OLAP masked-column guardrail denied the statement: {Message}", ex.Message);
                throw new OlapSqlRejectedException(
                    OlapSqlRejectedException.ColumnPolicy, "The SQL statement was rejected by the column security policy.", ex);
            }
        }
    }
}
