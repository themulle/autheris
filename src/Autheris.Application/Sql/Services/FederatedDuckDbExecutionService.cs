namespace Autheris.Application.Sql.Services;

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Security;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Interfaces;
using Autheris.Application.Olap;
using Autheris.Application.Policy;
using Autheris.Application.Services;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TrinoSqlEngine;
using TrinoSqlEngine.Analysis;

public sealed class FederatedDuckDbExecutionService : IFederatedQueryExecutionService
{
    private static readonly SqlTokenSecurityOptions AnalysisTokenOptions = new()
    {
        RejectComments = true,
        RejectBackslashInStrings = true,
        RejectEscapedStringLiterals = true,
        RejectNonAsciiIdentifiers = true,
        RejectDotsInQuotedIdentifiers = true,
        RejectTimeTravelQueries = true
    };

    private readonly IOptions<GatewayOptions> _options;
    private readonly IAuditLogRepository _auditLogRepository;
    private readonly ICrossSourceQueryRouter _router;
    private readonly ICrossSourcePlanner _planner;
    private readonly IFederatedStagingService _stagingService;
    private readonly IDuckDbOlapEngine _duckDbEngine;
    private readonly ISqlEngine _sqlEngine;
    private readonly IPolicyEnforcementService? _policyEnforcement;
    private readonly IConsentResolutionService? _consentResolution;
    private readonly IConsentRepository? _consentRepository;
    private readonly IConsentCacheService? _consentCache;
    private readonly IClientIpResolver? _clientIpResolver;
    private readonly Autheris.Application.Security.Rebac.Interfaces.IRebacEvaluator? _rebacEvaluator;
    private readonly Autheris.Application.VirtualFilters.IMandatoryRowFilterResolver? _mandatoryFilters;
    private readonly Autheris.Application.Governance.Contracts.ISchemaContractManager? _contractManager;
    private readonly IAccessProfileRepository? _accessProfileRepository;
    private readonly Microsoft.Extensions.Caching.Memory.IMemoryCache? _memoryCache;
    private readonly ILogger<FederatedDuckDbExecutionService>? _logger;

    public FederatedDuckDbExecutionService(
        IOptions<GatewayOptions> options,
        IAuditLogRepository auditLogRepository,
        ICrossSourceQueryRouter router,
        ICrossSourcePlanner planner,
        IFederatedStagingService stagingService,
        IDuckDbOlapEngine duckDbEngine,
        ISqlEngine? sqlEngine = null,
        IPolicyEnforcementService? policyEnforcement = null,
        IConsentResolutionService? consentResolution = null,
        IConsentRepository? consentRepository = null,
        IConsentCacheService? consentCache = null,
        IClientIpResolver? clientIpResolver = null,
        Autheris.Application.Security.Rebac.Interfaces.IRebacEvaluator? rebacEvaluator = null,
        Autheris.Application.VirtualFilters.IMandatoryRowFilterResolver? mandatoryFilters = null,
        Autheris.Application.Governance.Contracts.ISchemaContractManager? contractManager = null,
        IAccessProfileRepository? accessProfileRepository = null,
        Microsoft.Extensions.Caching.Memory.IMemoryCache? memoryCache = null,
        ILogger<FederatedDuckDbExecutionService>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _auditLogRepository = auditLogRepository ?? throw new ArgumentNullException(nameof(auditLogRepository));
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
        _stagingService = stagingService ?? throw new ArgumentNullException(nameof(stagingService));
        _duckDbEngine = duckDbEngine ?? throw new ArgumentNullException(nameof(duckDbEngine));
        _sqlEngine = sqlEngine ?? FastSqlEngine.Default;
        _policyEnforcement = policyEnforcement;
        _consentResolution = consentResolution;
        _consentRepository = consentRepository;
        _consentCache = consentCache;
        _clientIpResolver = clientIpResolver;
        _rebacEvaluator = rebacEvaluator;
        _mandatoryFilters = mandatoryFilters;
        _contractManager = contractManager;
        _accessProfileRepository = accessProfileRepository;
        _memoryCache = memoryCache;
        _logger = logger;
    }

    private TableAccessPolicy AccessPolicy() =>
        new(_consentRepository!, _consentResolution!,
            _consentCache ?? Autheris.Application.Policy.Services.NullConsentCacheService.Instance,
            _policyEnforcement ?? Autheris.Application.Policy.Services.NullPolicyEnforcementService.Instance,
            _rebacEvaluator ?? Autheris.Application.Security.Rebac.Services.NullRebacEvaluator.Instance,
            _clientIpResolver,
            _options.Value,
            _mandatoryFilters ?? Autheris.Application.VirtualFilters.NullMandatoryRowFilterResolver.Instance,
            _contractManager,
            _accessProfileRepository,
            _memoryCache);

    private IPAddress? ResolveClientIp(ClaimsPrincipal user)
    {
        if (_clientIpResolver == null) return null;
        return _clientIpResolver.ResolveClientIp();
    }

    private static Sid ResolveUserSid(ClaimsPrincipal user)
    {
        var sidString = user.FindFirst(ClaimTypes.PrimarySid)?.Value
                     ?? user.FindFirst("sub")?.Value
                     ?? "anonymous";
        return new Sid(sidString);
    }

    public async Task ExecuteGovernedQueryAsync(
        GovernedSqlQueryRequest request,
        ClaimsPrincipal user,
        TenantId tenantId,
        Func<DbDataReader, CancellationToken, Task> rowWriter,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(rowWriter);

        var (duckDbResult, _) = await ExecuteFederatedInternalAsync(request, user, tenantId, ct).ConfigureAwait(false);

        using var dataTable = ConvertToDataTable(duckDbResult);
        using var reader = dataTable.CreateDataReader();
        await rowWriter(reader, ct).ConfigureAwait(false);
    }

    public async Task<GovernedSqlResult> ExecuteQueryBufferedAsync(
        GovernedSqlQueryRequest request,
        ClaimsPrincipal user,
        TenantId tenantId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (duckDbResult, plan) = await ExecuteFederatedInternalAsync(request, user, tenantId, ct).ConfigureAwait(false);

        var rows = new List<IReadOnlyDictionary<string, object?>>(duckDbResult.Rows.Count);
        foreach (var row in duckDbResult.Rows)
        {
            var dict = new Dictionary<string, object?>(duckDbResult.Columns.Count, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < duckDbResult.Columns.Count; i++)
            {
                dict[duckDbResult.Columns[i]] = row[i];
            }
            rows.Add(dict);
        }

        int maxResultRows = _options.Value.DuckDbOlap?.MaxResultRows > 0 ? _options.Value.DuckDbOlap.MaxResultRows : 50000;
        int effectiveMaxRows = request.RowLimit != null
            ? (int)Math.Min((long)maxResultRows, request.RowLimit.Effective(null))
            : maxResultRows;

        bool truncated = duckDbResult.TotalRowCount >= effectiveMaxRows;

        return new GovernedSqlResult(
            request.Sql,
            plan.GeneratedDuckDbSql,
            duckDbResult.Columns,
            rows,
            rows.Count,
            (long)duckDbResult.ExecutionDuration.TotalMilliseconds,
            truncated);
    }

    public async Task<string> RewriteSqlAsync(
        string rawSql,
        ClaimsPrincipal user,
        TenantId tenantId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rawSql);
        ArgumentNullException.ThrowIfNull(user);

        var crossSourceOptions = _options.Value.WebSql?.CrossSource ?? new CrossSourceOptions();
        if (!crossSourceOptions.Enabled)
        {
            throw new WebSqlPolicyException("Cross-catalog queries across multiple data sources are not supported in WebSQL.");
        }

        var metadata = _sqlEngine.Analyze(rawSql.AsMemory(), AnalysisTokenOptions, ct);

        if (metadata.StatementType is SqlStatementType.Insert or SqlStatementType.Update or SqlStatementType.Delete)
        {
            throw new WebSqlPolicyException("DML statements across multiple data sources or HTTP APIs are not permitted in WebSQL.");
        }

        var routing = await _router.RouteAsync(metadata, requestedDataSource: null, tenantId, isDml: false, ct).ConfigureAwait(false);
        if (routing.SourceClass == QuerySourceClass.Unsupported)
        {
            throw new WebSqlPolicyException(routing.RejectionReason ?? "Unsupported data source type.");
        }

        var (decisions, userSid) = await AuthorizeAllTablesAsync(metadata, routing.Tables, user, tenantId, isDml: false, ct).ConfigureAwait(false);
        var plan = _planner.Plan(rawSql, metadata, routing.Tables, decisions, crossSourceOptions);

        return plan.GeneratedDuckDbSql;
    }

    private async Task<(OlapQueryResult Result, CrossSourcePlan Plan)> ExecuteFederatedInternalAsync(
        GovernedSqlQueryRequest request,
        ClaimsPrincipal user,
        TenantId tenantId,
        CancellationToken ct)
    {
        var crossSourceOptions = _options.Value.WebSql?.CrossSource ?? new CrossSourceOptions();
        if (!crossSourceOptions.Enabled)
        {
            throw new WebSqlPolicyException("Cross-catalog queries across multiple data sources are not supported in WebSQL.");
        }

        // Transport Gate (Section 3.5)
        var allowedTransports = crossSourceOptions.AllowedTransports ?? ["WebSql", "Trino"];
        if (!string.IsNullOrWhiteSpace(request.Transport) &&
            !allowedTransports.Any(t => string.Equals(t, request.Transport, StringComparison.OrdinalIgnoreCase)))
        {
            throw new WebSqlPolicyException("Cross-catalog queries across multiple data sources are not supported in WebSQL.");
        }

        var metadata = _sqlEngine.Analyze(request.Sql.AsMemory(), AnalysisTokenOptions, ct);

        if (metadata.StatementType is SqlStatementType.Insert or SqlStatementType.Update or SqlStatementType.Delete)
        {
            throw new WebSqlPolicyException("DML statements across multiple data sources or HTTP APIs are not permitted in WebSQL.");
        }

        // SqlFunctionPolicy check
        if (metadata.FunctionCalls is { Count: > 0 })
        {
            var functionPolicy = new RlsOptions { EnforceFunctionPolicy = true };
            foreach (var functionName in metadata.FunctionCalls)
            {
                if (!SqlFunctionPolicy.IsFunctionAllowed(functionName, functionPolicy))
                {
                    throw new WebSqlPolicyException($"Function '{functionName}' is not permitted in WebSQL.");
                }
            }
        }

        // Classification via router
        var routing = await _router.RouteAsync(metadata, request.DataSourceName, tenantId, isDml: false, ct).ConfigureAwait(false);
        if (routing.SourceClass == QuerySourceClass.Unsupported)
        {
            throw new WebSqlPolicyException(routing.RejectionReason ?? "Unsupported data source type.");
        }
        if (routing.SourceClass != QuerySourceClass.CrossSource)
        {
            throw new WebSqlPolicyException("Query is not a cross-source query.");
        }

        // Limit: MaxTableCount
        int maxTables = crossSourceOptions.MaxTableCount > 0 ? crossSourceOptions.MaxTableCount : 5;
        if (routing.Tables.Count > maxTables)
        {
            throw new WebSqlPolicyException($"Query references {routing.Tables.Count} tables, which exceeds the cross-source maximum of {maxTables}.");
        }

        try
        {
            // Phase A: Authorize ALL tables before any read (INV-1, INV-2)
            var (decisions, userSid) = await AuthorizeAllTablesAsync(metadata, routing.Tables, user, tenantId, isDml: false, ct).ConfigureAwait(false);

            // Plan execution
            var plan = _planner.Plan(request.Sql, metadata, routing.Tables, decisions, crossSourceOptions);

            // Start Audit (fail-closed, INV-17)
            await RecordStartAuditAsync(request, routing.Tables, userSid, tenantId, ct).ConfigureAwait(false);

            // Budget and Timeout (INV-12, INV-13)
            int timeoutSeconds = crossSourceOptions.TimeoutSeconds > 0
                ? crossSourceOptions.TimeoutSeconds
                : (_options.Value.WebSql?.ExecutionTimeoutSeconds > 0 ? _options.Value.WebSql.ExecutionTimeoutSeconds : 30);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            var budget = new FederationBudget(
                MaxTableCount: maxTables,
                MaxStagedRowsPerTable: crossSourceOptions.MaxStagedRowsPerTable > 0 ? crossSourceOptions.MaxStagedRowsPerTable : 50_000,
                MaxTotalStagedRows: crossSourceOptions.MaxTotalStagedRows > 0 ? crossSourceOptions.MaxTotalStagedRows : 200_000,
                MaxStagedBytesPerTable: crossSourceOptions.MaxStagedBytesPerTable > 0 ? crossSourceOptions.MaxStagedBytesPerTable : 32 * 1024 * 1024,
                MaxTotalStagedBytes: crossSourceOptions.MaxTotalStagedBytes > 0 ? crossSourceOptions.MaxTotalStagedBytes : 128 * 1024 * 1024,
                TimeoutSeconds: timeoutSeconds,
                MaxParallelSourceReads: crossSourceOptions.MaxParallelSourceReads > 0 ? crossSourceOptions.MaxParallelSourceReads : 4);

            // Phase B: Governed Staging (INV-3, INV-10, INV-17)
            var stagedSources = await _stagingService.StageAsync(plan.StagingRequests, user, tenantId, budget, timeoutCts.Token).ConfigureAwait(false);

            // Execute in DuckDB
            int maxResultRows = _options.Value.DuckDbOlap?.MaxResultRows > 0 ? _options.Value.DuckDbOlap.MaxResultRows : 50000;
            int effectiveMaxRows = request.RowLimit != null
                ? (int)Math.Min((long)maxResultRows, request.RowLimit.Effective(null))
                : maxResultRows;

            var duckDbResult = await _duckDbEngine.ExecuteGeneratedAsync(
                new GeneratedOlapQuery(plan.GeneratedDuckDbSql),
                stagedSources,
                effectiveMaxRows,
                timeoutCts.Token).ConfigureAwait(false);

            return (duckDbResult, plan);
        }
        catch (Exception ex)
        {
            await RecordFailureAuditAsync(request, tenantId, user, ex).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<(Dictionary<string, TableAccessDecision> Decisions, Sid UserSid)> AuthorizeAllTablesAsync(
        SqlQueryMetadata metadata,
        IReadOnlyList<ResolvedSourceTable> tables,
        ClaimsPrincipal user,
        TenantId tenantId,
        bool isDml,
        CancellationToken ct)
    {
        bool consentBypassed = _options.Value.IsConsentBypassed;
        var userSidNullable = user.GetUserSid();
        if (userSidNullable == null && !consentBypassed)
        {
            throw new WebSqlPolicyException("An authenticated user identity is required for WebSQL.");
        }

        var userSid = userSidNullable ?? new Sid("anonymous");
        var groupSids = user.GetGroupSids();
        var roles = user.GetUserRoles();
        var clientIp = ResolveClientIp(user);

        var actionAttribute = new Dictionary<string, object?>
        {
            ["gql.action"] = isDml ? "write" : "read",
            ["action"] = isDml ? "write" : "read"
        };

        var decisions = new Dictionary<string, TableAccessDecision>(StringComparer.OrdinalIgnoreCase);

        foreach (var resolved in tables)
        {
            if (!consentBypassed && (_consentRepository == null || _consentResolution == null))
            {
                _logger?.LogError("WebSQL cannot evaluate consents (consent services not available); denying access (fail-closed).");
                throw new WebSqlPolicyException($"WebSQL access denied to table '{resolved.Target.FullName}'.");
            }

            var colList = resolved.Metadata.Columns.Select(c => c.ColumnName).ToList();
            var decision = consentBypassed && (_consentRepository == null || _consentResolution == null)
                ? TableAccessDecision.Allowed(resolved.ResolvedIdentifier, new Dictionary<string, ColumnAccessLevel>(), rowFilterSql: null, hasUnconstrainedColumnAllow: true)
                : await AccessPolicy().DecideAsync(
                    new TableAccessQuery(userSid, tenantId, groupSids, roles, resolved.Metadata, user.Claims, colList, RebacEnforcement.QueryPaths, clientIp, actionAttribute),
                    ct).ConfigureAwait(false);

            if (!decision.IsAllowed)
            {
                _logger?.LogWarning("WebSQL access to table {Table} denied: {Reasons}", resolved.Target.FullName, string.Join("; ", decision.DeniedReasons));
                throw new WebSqlPolicyException($"WebSQL access denied to table '{resolved.Target.FullName}'.");
            }

            // Tenant column check (INV-9)
            string? tenantColumn = TableMetadata.RequireTenantColumnOrThrow(
                resolved.Metadata,
                _options.Value.DataSources?.RequireTenantColumn == true,
                _options.Value.DataSources?.TenantColumnExemptTables);
            if (tenantColumn != null && !System.Text.RegularExpressions.Regex.IsMatch(tenantColumn, "^[A-Za-z_][A-Za-z0-9_]{0,127}$"))
            {
                throw new InvalidOperationException($"The tenant column '{tenantColumn}' of {resolved.Target.FullName} is not a plain identifier; the query is refused (fail-closed).");
            }

            // SEC-JOIN-01 & SEC-FILTER-01 Zero-Trust Guardrails (INV-4)
            GovernedSqlExecutionService.EnforceMaskedColumnGuardrails(resolved.Target, resolved.Metadata, decision, metadata);

            decisions[resolved.ResolvedIdentifier.ToQualifiedName()] = decision;
        }

        return (decisions, userSid);
    }

    private async Task RecordStartAuditAsync(
        GovernedSqlQueryRequest request,
        IReadOnlyList<ResolvedSourceTable> tables,
        Sid userSid,
        TenantId tenantId,
        CancellationToken ct)
    {
        if (_auditLogRepository == null)
        {
            throw new InvalidOperationException("Audit log repository is required for cross-source queries.");
        }

        var (redactedOriginalSql, originalSqlHash) = TrinoSqlEngine.Ast.Visitors.AstSecurityVisitor.AnonymizeSqlForAudit(request.Sql);
        var tablesAudit = tables.Select(t => new
        {
            table = t.Target.FullName,
            dataSourceType = t.Metadata.Table.DataSourceType.ToString(),
            dataSource = t.EffectiveDataSource
        }).ToList();

        var startDetails = JsonSerializer.Serialize(new
        {
            originalSql = redactedOriginalSql,
            originalSqlHash,
            tables = tablesAudit,
            transport = request.Transport ?? "WebSql"
        });

        try
        {
            await _auditLogRepository.RecordAuditEventAsync(new AuditLogEntry
            {
                TenantId = tenantId,
                EventType = AuditEventTypes.WebSqlCrossSourceQuery,
                ActorSid = userSid,
                TargetTable = string.Join(",", tables.Select(t => t.EffectiveDataSource).Distinct()),
                Decision = "ALLOW",
                TraceId = Guid.NewGuid().ToString("N"),
                DetailsJson = startDetails
            }, ct).ConfigureAwait(false);
        }
        catch (Exception auditEx)
        {
            _logger?.LogError(auditEx, "Failed to record start audit for cross-source query.");
            throw new InvalidOperationException("Audit logging failure during cross-source query execution.", auditEx);
        }
    }

    private async Task RecordFailureAuditAsync(
        GovernedSqlQueryRequest request,
        TenantId tenantId,
        ClaimsPrincipal user,
        Exception ex)
    {
        if (_auditLogRepository == null) return;

        try
        {
            var (redactedOriginalSql, originalSqlHash) = TrinoSqlEngine.Ast.Visitors.AstSecurityVisitor.AnonymizeSqlForAudit(request.Sql);
            var userSid = ResolveUserSid(user);
            bool isDenied = ex is WebSqlPolicyException or SecurityException;
            var eventType = isDenied ? AuditEventTypes.WebSqlQueryDenied : AuditEventTypes.QueryExecutionError;

            var detailsJson = JsonSerializer.Serialize(new
            {
                originalSql = redactedOriginalSql,
                originalSqlHash,
                reasonCode = isDenied ? "POLICY_VIOLATION" : ex.GetType().Name,
                error = ex.Message
            });

            await _auditLogRepository.RecordAuditEventAsync(new AuditLogEntry
            {
                TenantId = tenantId,
                EventType = eventType,
                ActorSid = userSid,
                TargetTable = "cross-source",
                Decision = isDenied ? "DENY" : "ERROR",
                TraceId = Guid.NewGuid().ToString("N"),
                DetailsJson = detailsJson
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Do not mask original failure
        }
    }

    private static DataTable ConvertToDataTable(OlapQueryResult result)
    {
        var table = new DataTable();
        foreach (var col in result.Columns)
        {
            table.Columns.Add(col, typeof(object));
        }

        foreach (var row in result.Rows)
        {
            var arr = new object?[row.Count];
            for (int i = 0; i < row.Count; i++)
            {
                arr[i] = row[i] ?? DBNull.Value;
            }
            table.Rows.Add(arr);
        }

        return table;
    }
}
