namespace Autheris.Application.Sql.Services;

using Autheris.Application.Common;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
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
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Olap;
using Autheris.Application.Policy;
using Autheris.Application.Services;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.VirtualFilters;
using Autheris.Application.VirtualFilters.Federation;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using TrinoSqlEngine.Ast.Emit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TrinoSqlEngine;
using TrinoSqlEngine.Analysis;
using TrinoSqlEngine.Ast.Builder;
using TrinoSqlEngine.Ast.Nodes;
using Autheris.Application.Sql.Federation.Parameters;
using Autheris.Application.Sql.ListParameters;

public sealed class FederatedDuckDbExecutionService : IFederatedQueryExecutionService
{
    private static readonly SqlTokenSecurityOptions AnalysisTokenOptions = Autheris.Application.Sql.Policy.ClientSqlTokenPolicy.Analysis;

    private readonly IOptions<GatewayOptions> _options;
    private readonly IAuditLogRepository _auditLogRepository;
    private readonly ICrossSourceQueryRouter _router;
    private readonly ICrossSourcePlanner _planner;
    private readonly IFederatedStagingService _stagingService;
    private readonly IDuckDbOlapEngine _duckDbEngine;
    private readonly ISqlEngine _sqlEngine;
    private readonly IConsentResolutionService? _consentResolution;
    private readonly IConsentRepository? _consentRepository;
    private readonly IConsentCacheService? _consentCache;
    private readonly IClientIpResolver? _clientIpResolver;
    private readonly Autheris.Application.VirtualFilters.IMandatoryRowFilterResolver? _mandatoryFilters;
    private readonly Autheris.Application.Governance.Contracts.ISchemaContractManager? _contractManager;
    private readonly Autheris.Application.Policy.Pdp.IAccessDecisionPoint _accessDecisionPoint;
    private readonly IAccessProfileRepository? _accessProfileRepository;
    private readonly Microsoft.Extensions.Caching.Memory.IMemoryCache? _memoryCache;
    private readonly IVirtualFilterFederationPlanner? _vfPlanner;
    private readonly Autheris.Application.Policy.TypedPolicySources? _typedPolicies;
    private readonly Autheris.Application.Common.Quota.IQueryCostQuota? _costQuota;
    private readonly ILogger<FederatedDuckDbExecutionService>? _logger;
    private readonly CompiledSqlAuditDigester _auditDigester;
    private readonly Autheris.Application.DataSources.IDataSourceTransportGate _transportGate;

    public FederatedDuckDbExecutionService(
        IOptions<GatewayOptions> options,
        IAuditLogRepository auditLogRepository,
        ICrossSourceQueryRouter router,
        ICrossSourcePlanner planner,
        IFederatedStagingService stagingService,
        IDuckDbOlapEngine duckDbEngine,
        ISqlEngine? sqlEngine = null,
        IConsentResolutionService? consentResolution = null,
        IConsentRepository? consentRepository = null,
        IConsentCacheService? consentCache = null,
        IClientIpResolver? clientIpResolver = null,
        Autheris.Application.VirtualFilters.IMandatoryRowFilterResolver? mandatoryFilters = null,
        Autheris.Application.Governance.Contracts.ISchemaContractManager? contractManager = null,
        IAccessProfileRepository? accessProfileRepository = null,
        Microsoft.Extensions.Caching.Memory.IMemoryCache? memoryCache = null,
        ILogger<FederatedDuckDbExecutionService>? logger = null,
        IVirtualFilterFederationPlanner? vfPlanner = null,
        Autheris.Application.Policy.TypedPolicySources? typedPolicies = null,
        CompiledSqlAuditDigester? auditDigester = null,
        Autheris.Application.DataSources.IDataSourceTransportGate? transportGate = null,
        Autheris.Application.Common.Quota.IQueryCostQuota? costQuota = null,
        Autheris.Application.Policy.Pdp.IAccessDecisionPoint? accessDecisionPoint = null)
    {
        _accessDecisionPoint = accessDecisionPoint ?? new Autheris.Application.Policy.Pdp.AccessDecisionPoint(
            options,
            consentResolution,
            consentRepository,
            consentCache,
            clientIpResolver,
            mandatoryFilters,
            contractManager,
            accessProfileRepository,
            memoryCache,
            accessProfileCache: null,
            typedPolicies);
        _costQuota = costQuota;
        // Issue #447 no gate means deny, never pass-through.
        _transportGate = transportGate ?? Autheris.Application.DataSources.TransportGateFallback.Current;
        _auditDigester = auditDigester ?? new CompiledSqlAuditDigester(options);
        _typedPolicies = typedPolicies;
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _auditLogRepository = auditLogRepository ?? throw new ArgumentNullException(nameof(auditLogRepository));
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
        _stagingService = stagingService ?? throw new ArgumentNullException(nameof(stagingService));
        _duckDbEngine = duckDbEngine ?? throw new ArgumentNullException(nameof(duckDbEngine));
        _sqlEngine = sqlEngine ?? FastSqlEngine.Default;
        _consentResolution = consentResolution;
        _consentRepository = consentRepository;
        _consentCache = consentCache;
        _clientIpResolver = clientIpResolver;
        _mandatoryFilters = mandatoryFilters;
        _contractManager = contractManager;
        _accessProfileRepository = accessProfileRepository;
        _memoryCache = memoryCache;
        _logger = logger;
        _vfPlanner = vfPlanner;
    }

    internal Autheris.Application.Policy.Pdp.IAccessDecisionPoint AccessDecisionPoint => _accessDecisionPoint;

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
        Func<DbDataReader, GovernedResultInfo, CancellationToken, Task> rowWriter,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(rowWriter);

        var (duckDbResult, plan, _) = await ExecuteFederatedInternalAsync(request, user, tenantId, ct).ConfigureAwait(false);

        using var dataTable = ConvertToDataTable(duckDbResult);
        using var reader = dataTable.CreateDataReader();
        var columns = SqlResultColumns.Describe(reader);
        var maskArray = duckDbResult.Columns.Select(c => IsMaskedByName(c, plan)).ToImmutableArray();
        if (maskArray.Length != reader.FieldCount)
        {
            throw new GovernedSqlException(GovernedSqlErrorCodes.CompilerInvariant, TargetSqlDialect.DuckDb);
        }
        await rowWriter(reader, new GovernedResultInfo(columns, maskArray), ct).ConfigureAwait(false);
    }

    public Task ExecuteGovernedQueryAsync(
        GovernedSqlQueryRequest request,
        ClaimsPrincipal user,
        TenantId tenantId,
        Func<DbDataReader, CancellationToken, Task> rowWriter,
        CancellationToken ct = default) =>
        ExecuteGovernedQueryAsync(request, user, tenantId, (reader, _, token) => rowWriter(reader, token), ct);

    private static bool IsMaskedByName(string columnName, CrossSourcePlan plan)
    {
        bool foundMatch = false;
        foreach (var req in plan.StagingRequests)
        {
            if (req.Subtree != null)
            {
                if (req.Subtree.ProjectedColumns.Any(c => string.Equals(c, columnName, StringComparison.OrdinalIgnoreCase)))
                {
                    foundMatch = true;
                    if (req.Subtree.MaskedColumns.Contains(columnName))
                    {
                        return true;
                    }
                }
            }
            else
            {
                var matchingCol = req.Metadata.Columns.FirstOrDefault(c => string.Equals(c.ColumnName, columnName, StringComparison.OrdinalIgnoreCase));
                if (matchingCol != null)
                {
                    foundMatch = true;
                    if (req.Decision.GetEffectiveColumnAccess(matchingCol.ColumnName, req.Metadata) == ColumnAccessLevel.Mask)
                    {
                        return true;
                    }
                }
            }
        }

        return !foundMatch;
    }

    public async Task<GovernedSqlResult> ExecuteQueryBufferedAsync(
        GovernedSqlQueryRequest request,
        ClaimsPrincipal user,
        TenantId tenantId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (duckDbResult, plan, truncated) = await ExecuteFederatedInternalAsync(request, user, tenantId, ct).ConfigureAwait(false);

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

        return new GovernedSqlResult(
            request.Sql,
            duckDbResult.Columns,
            rows,
            rows.Count,
            (long)duckDbResult.ExecutionDuration.TotalMilliseconds,
            truncated,
            InDbColumnMaskingExecuted: !_options.Value.IsColumnMaskingDisabled);
    }

    public async Task ValidateAsync(
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

        var sqlForAnalysis = GovernedSqlExecutionService.NormalizeClientParameters(rawSql, null, out _);
        var metadata = _sqlEngine.Analyze(sqlForAnalysis.AsMemory(), AnalysisTokenOptions, ct);

        if (metadata.StatementType is SqlStatementType.Insert or SqlStatementType.Update or SqlStatementType.Delete)
        {
            throw new WebSqlPolicyException("DML statements across multiple data sources or HTTP APIs are not permitted in WebSQL.");
        }

        // SqlFunctionPolicy check
        if (metadata.FunctionCalls is { Count: > 0 })
        {
            var allowedFunctions = EffectiveDuckDbFunctions();
            foreach (var functionName in metadata.FunctionCalls)
            {
                if (!SqlFunctionPolicy.IsFunctionAllowed(functionName, allowedFunctions, additionalDeniedFunctions: null))
                {
                    throw new WebSqlPolicyException($"Function '{functionName}' is not permitted in WebSQL.");
                }
            }
        }

        var routing = await _router.RouteAsync(metadata, requestedDataSource: null, tenantId, isDml: false, ct).ConfigureAwait(false);
        if (routing.SourceClass == QuerySourceClass.Unsupported)
        {
            throw new WebSqlPolicyException(routing.RejectionReason ?? "Unsupported data source type.");
        }

        var (decisions, userSid) = await AuthorizeAllTablesAsync(metadata, routing.Tables, user, tenantId, isDml: false, ct).ConfigureAwait(false);

        // Issue #447 C2: every routed source must allow the ingress transport; nothing is planned or staged otherwise.
        await _transportGate.EnsureCatalogSourcesAsync(routing.Tables.Select(t => t.EffectiveDataSource), ct).ConfigureAwait(false);

        _planner.Plan(sqlForAnalysis, metadata, routing.Tables, decisions, crossSourceOptions, user, tenantId);
    }

    private async Task<(OlapQueryResult Result, CrossSourcePlan Plan, bool Truncated)> ExecuteFederatedInternalAsync(
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
        var allowedTransports = crossSourceOptions.GetEffectiveAllowedTransports();
        // Fail-closed: a request without a transport is rejected, never exempted from the gate.
        if (string.IsNullOrWhiteSpace(request.Transport) ||
            !allowedTransports.Any(t => string.Equals(t, request.Transport, StringComparison.OrdinalIgnoreCase)))
        {
            throw new WebSqlPolicyException("Cross-catalog queries across multiple data sources are not supported in WebSQL.");
        }

        if (request.GatewayParameters is { Count: > 0 })
        {
            throw new WebSqlPolicyException("Gateway-planned parameters are not supported on the federated path.");
        }

        GovernedSqlExecutionService.EnsureClientParameterNames(request.Parameters);

        var sqlForAnalysis = GovernedSqlExecutionService.NormalizeClientParameters(request.Sql, request.Parameters, out _);
        var metadata = _sqlEngine.Analyze(sqlForAnalysis.AsMemory(), AnalysisTokenOptions, ct);

        Autheris.Application.Common.Quota.QueryCostAdmission? admission = null;
        bool isMeteredTransport = SqlTransports.IsCostMetered(request.Transport);
        if (_costQuota != null && (_options.Value.WebSql?.Cost?.Enabled ?? true) && !request.PlannedTableRead && isMeteredTransport)
        {
            var rowLimit = request.RowLimit ?? SqlRowLimit.For(_options.Value.WebSql ?? new WebSqlOptions());
            var costOpts = _options.Value.QueryCost ?? new QueryCostOptions();
            var analysisOpts = new Autheris.Application.Governance.Analysis.QueryAnalysisOptions();
            var webSqlCost = _options.Value.WebSql?.Cost ?? new WebSqlCostOptions();

            int cEst = Autheris.Application.Sql.Cost.SqlStatementCostEstimator.EstimateCost(
                metadata,
                rowLimit,
                costOpts,
                analysisOpts,
                webSqlCost,
                pageOffset: request.PageOffset ?? 0,
                isPaged: request.IsPaged);

            try
            {
                admission = await _costQuota.AdmitAsync(
                    new Autheris.Application.Common.Quota.QueryCostRequest(
                        Principal: user,
                        ApiKey: null,
                        ClientIp: null,
                        EstimatedCost: cEst,
                        TransportMaxCost: webSqlCost.MaxStatementCost,
                        CapByTierMaxCostPerQuery: false),
                    ct).ConfigureAwait(false);
            }
            catch (Exception admitEx) when (admitEx is not OperationCanceledException)
            {
                throw new SqlQuotaUnavailableException();
            }

            if (admission?.Outcome == Autheris.Application.Common.Quota.QueryCostOutcome.OverCost)
            {
                throw new SqlCostExceededException(cEst, admission.MaxAllowedCost);
            }
            if (admission?.Outcome == Autheris.Application.Common.Quota.QueryCostOutcome.QuotaExhausted)
            {
                throw new SqlQuotaExhaustedException(admission.RetryAfterSeconds);
            }
        }

        var executionSw = System.Diagnostics.Stopwatch.StartNew();
        bool failed = false;
        long deliveredRows = 0;

        if (metadata.StatementType is SqlStatementType.Insert or SqlStatementType.Update or SqlStatementType.Delete)
        {
            throw new WebSqlPolicyException("DML statements across multiple data sources or HTTP APIs are not permitted in WebSQL.");
        }

        // SqlFunctionPolicy check
        if (metadata.FunctionCalls is { Count: > 0 })
        {
            var allowedFunctions = EffectiveDuckDbFunctions();
            foreach (var functionName in metadata.FunctionCalls)
            {
                if (!SqlFunctionPolicy.IsFunctionAllowed(functionName, allowedFunctions, additionalDeniedFunctions: null))
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

        // One correlation id for every audit record of this query (start, key set reads, source reads, denial).
        var correlationId = System.Diagnostics.Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
        // CR-04 (AC-9.1): key set reads are on record even when the plan is denied after the keys were loaded.
        var keySourceReads = new List<VirtualFilterKeySourceRead>();
        bool keySourceReadsAudited = false;
        FederatedParameterContract? parameterContract = null;
        try
        {
            // Budget and Timeout (INV-12, INV-13). the timeout also covers authorization, planning and key loading.
            int timeoutSeconds = crossSourceOptions.TimeoutSeconds > 0
                ? crossSourceOptions.TimeoutSeconds
                : (_options.Value.WebSql?.ExecutionTimeoutSeconds > 0 ? _options.Value.WebSql.ExecutionTimeoutSeconds : 30);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            // Phase A: Authorize ALL tables before any read (INV-1, INV-2)
            var (decisions, userSid) = await AuthorizeAllTablesAsync(metadata, routing.Tables, user, tenantId, isDml: false, timeoutCts.Token).ConfigureAwait(false);

            // Issue #447 C2: all-or-nothing before key loading, planning, the start audit record and any staging.
            await _transportGate.EnsureCatalogSourcesAsync(routing.Tables.Select(t => t.EffectiveDataSource), timeoutCts.Token).ConfigureAwait(false);

            // parameter validation strictly follows table authorization (anti-oracle defense).
            var parameterLimits = ListParameterLimits.From(_options.Value.WebSql ?? new WebSqlOptions());
            parameterContract = FederatedParameterContract.Create(request.Parameters, parameterLimits);

            // F-GOV-14 (WI-6): the planner decides per protected HTTP table how the authorized keys are loaded and enforced
            // (single policy decision point: the decisions). Without a planner a table with applied Virtual Filters is denied.
            var statement = ParseStatement(sqlForAnalysis);
            var vfPlans = await PlanVirtualFiltersAsync(statement, routing.Tables, decisions, tenantId, keySourceReads, timeoutCts.Token).ConfigureAwait(false);
            var plan = _planner.Plan(sqlForAnalysis, metadata, routing.Tables, decisions, crossSourceOptions, user, tenantId, vfPlans, statement, parameterContract);

            // Start Audit (fail-closed, INV-17)
            await RecordStartAuditAsync(request, routing.Tables, userSid, tenantId, vfPlans, keySourceReads, correlationId, parameterContract, plan.Compiled.Parameters.Length, ct).ConfigureAwait(false);
            keySourceReadsAudited = true;

            var budget = new FederationBudget(
                MaxTableCount: maxTables,
                MaxStagedRowsPerTable: crossSourceOptions.MaxStagedRowsPerTable > 0 ? crossSourceOptions.MaxStagedRowsPerTable : 50_000,
                MaxTotalStagedRows: crossSourceOptions.MaxTotalStagedRows > 0 ? crossSourceOptions.MaxTotalStagedRows : 200_000,
                MaxStagedBytesPerTable: crossSourceOptions.MaxStagedBytesPerTable > 0 ? crossSourceOptions.MaxStagedBytesPerTable : 32 * 1024 * 1024,
                MaxTotalStagedBytes: crossSourceOptions.MaxTotalStagedBytes > 0 ? crossSourceOptions.MaxTotalStagedBytes : 128 * 1024 * 1024,
                TimeoutSeconds: timeoutSeconds,
                MaxParallelSourceReads: crossSourceOptions.MaxParallelSourceReads > 0 ? crossSourceOptions.MaxParallelSourceReads : 4,
                AuditCorrelationId: correlationId);

            // Phase B: Governed Staging (INV-3, INV-10, INV-17)
            var stagedSources = await _stagingService.StageAsync(plan.StagingRequests, user, tenantId, budget, timeoutCts.Token).ConfigureAwait(false);

            // Execute in DuckDB
            int maxResultRows = _options.Value.DuckDbOlap?.MaxResultRows > 0 ? _options.Value.DuckDbOlap.MaxResultRows : 50000;
            // SQLA-08: the same limit as the single-source path (explicit LIMIT, else the WebSQL default), bounded by the OLAP result cap;
            // one probe row beyond it tells a truncated result from an exact-size one.
            var rowLimit = request.RowLimit ?? SqlRowLimit.For(_options.Value.WebSql ?? new WebSqlOptions());
            int effectiveMaxRows = (int)Math.Min((long)maxResultRows, rowLimit.Effective(metadata.HasExplicitLimit ? metadata.ExplicitLimitValue : null));

            var duckDbResult = await _duckDbEngine.ExecuteGeneratedAsync(
                new GeneratedOlapQuery(plan.GeneratedDuckDbSql, plan.Compiled.Parameters, Compiled: plan.Compiled, ClientValues: parameterContract.ClientValues),
                stagedSources,
                (int)Math.Min((long)effectiveMaxRows + 1, int.MaxValue),
                timeoutCts.Token).ConfigureAwait(false);

            // The engine bounds its result at MaxResultRows: a result that fills that bound is reported as truncated (fail safe).
            bool truncated = duckDbResult.Rows.Count > effectiveMaxRows
                || (effectiveMaxRows >= maxResultRows && duckDbResult.Rows.Count >= maxResultRows);
            if (duckDbResult.Rows.Count > effectiveMaxRows)
            {
                duckDbResult = duckDbResult with { Rows = duckDbResult.Rows.Take(effectiveMaxRows).ToList(), TotalRowCount = effectiveMaxRows };
            }

            deliveredRows = duckDbResult.Rows.Count;
            return (duckDbResult, plan, truncated);
        }
        catch (Exception ex)
        {
            failed = true;
            await RecordFailureAuditAsync(request, tenantId, user, ex, correlationId, keySourceReadsAudited ? [] : keySourceReads, parameterContract).ConfigureAwait(false);
            throw;
        }
        finally
        {
            executionSw.Stop();
            if (admission != null && _costQuota != null)
            {
                try
                {
                    var costOpts = _options.Value.QueryCost ?? new QueryCostOptions();
                    var webSqlCost = _options.Value.WebSql?.Cost ?? new WebSqlCostOptions();
                    int cAct = Autheris.Application.Sql.Cost.SqlStatementCostEstimator.CalculateSettlementCost(
                        deliveredRows,
                        executionSw.Elapsed.TotalSeconds,
                        costOpts,
                        webSqlCost,
                        failed);
                    await _costQuota.SettleAsync(admission, cAct, CancellationToken.None).ConfigureAwait(false);
                }
                catch { }
            }
        }
    }

    private SqlStatement ParseStatement(string sql)
    {
        var (tree, _) = _sqlEngine.Parse(sql.AsMemory());
        return new SqlAstBuilder(new AstBuilderOptions
        {
            AllowedFunctions = EffectiveDuckDbFunctions(),
            TranslateTrinoDateFunctions = true
        }).BuildStatement(tree);
    }

    private IReadOnlySet<string> EffectiveDuckDbFunctions()
    {
        var webSqlOptions = _options.Value.WebSql ?? new WebSqlOptions();
        return SqlFunctionAllowlists.WithDateTranslation(
            SqlFunctionAllowlists.Build(TargetSqlDialect.DuckDb, webSqlOptions.AdditionalAllowedFunctions));
    }

    private async Task<IReadOnlyDictionary<TableIdentifier, VirtualFilterTablePlan>?> PlanVirtualFiltersAsync(
        SqlStatement statement,
        IReadOnlyList<ResolvedSourceTable> tables,
        IReadOnlyDictionary<TableIdentifier, TableAccessDecision> decisions,
        TenantId tenantId,
        ICollection<VirtualFilterKeySourceRead> keySourceReads,
        CancellationToken ct)
    {
        if (_vfPlanner == null)
        {
            // ST-22: no planner, no enforcement; a protected table is never read without it.
            foreach (var table in tables)
            {
                if (VirtualFilterFederationPlanner.RequiresEnforcement(table.Metadata, CrossSourcePlanner.RequireDecision(decisions, table)))
                {
                    throw new GatewaySecurityException(
                        $"The Virtual Filters of '{table.Metadata.Identifier}' cannot be enforced: no federation planner is configured.",
                        VirtualFilterErrorCodes.EnforcementUnavailable);
                }
            }

            return null;
        }

        return await _vfPlanner.PlanAsync(statement, tables, decisions, tenantId, ct, keySourceReads).ConfigureAwait(false);
    }

    private async Task<(Dictionary<TableIdentifier, TableAccessDecision> Decisions, Sid UserSid)> AuthorizeAllTablesAsync(
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
        if (userSidNullable == null && _mandatoryFilters != null &&
            await _mandatoryFilters.HasActiveProfilesAsync(tenantId, ct).ConfigureAwait(false))
        {
            // under the consent bypass the identity is synthetic and matches no grantee; Virtual Filters of the
            // tenant cannot be applied, so the query is denied instead of running unfiltered.
            throw new GatewaySecurityException(
                "Virtual Filters are configured for this tenant but the caller has no subject identity.",
                VirtualFilterErrorCodes.EnforcementUnavailable);
        }

        var groupSids = user.GetGroupSids();
        var roles = user.GetUserRoles();
        var clientIp = ResolveClientIp(user);

        var actionAttribute = new Dictionary<string, object?>
        {
            ["gql.action"] = isDml ? "write" : "read",
            ["action"] = isDml ? "write" : "read"
        };

        //  (F-GOV-14): one decision per source-qualified table (CrossSourcePlanner.DecisionKey); a table that occurs
        // several times (self-join) is decided once and every occurrence uses that same decision.
        var decisions = new Dictionary<TableIdentifier, TableAccessDecision>();

        foreach (var resolved in tables)
        {
            var decisionKey = CrossSourcePlanner.DecisionKey(resolved);
            if (decisions.TryGetValue(decisionKey, out var known))
            {
                GovernedSqlExecutionService.EnforceMaskedColumnGuardrails(resolved.Target, resolved.Metadata, known, metadata);
                continue;
            }

            if (!consentBypassed && (_consentRepository == null || _consentResolution == null))
            {
                _logger?.LogError("WebSQL cannot evaluate consents (consent services not available); denying access (fail-closed).");
                throw new WebSqlTableDeniedException($"WebSQL access denied to table '{resolved.Target.FullName}'.");
            }

            var colList = resolved.Metadata.Columns.Select(c => c.ColumnName).ToList();
            var secContext = Autheris.Domain.Security.SecurityPrincipalContext.FromPrincipal(user, tenantId, clientIp: clientIp);
            var req = new Autheris.Application.Policy.Pdp.AccessRequest(
                secContext,
                resolved.Metadata,
                Autheris.Application.Policy.Pdp.AccessOperation.Read,
                Autheris.Application.Policy.Pdp.AccessTransport.Federation,
                Columns: colList);
            var decision = await _accessDecisionPoint!.DecideAsync(req, ct).ConfigureAwait(false);

            if (!decision.IsAllowed)
            {
                _logger?.LogWarning("WebSQL access to table {Table} denied: {Reasons}", resolved.Target.FullName, string.Join("; ", decision.DeniedReasons));
                foreach (var reason in decision.DeniedReasons)
                {
                    if (VirtualFilterErrorCodes.TryGetCode(reason, out var code))
                    {
                        // F-GOV-14: a Virtual Filter evaluation failure keeps its stable code (HTTP 403, audit reasonCode).
                        throw new GatewaySecurityException($"WebSQL access denied to table '{resolved.Target.FullName}'.", code);
                    }
                }

                throw new WebSqlTableDeniedException($"WebSQL access denied to table '{resolved.Target.FullName}'.");
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

            // 01 Zero-Trust Guardrails (INV-4)
            GovernedSqlExecutionService.EnforceMaskedColumnGuardrails(resolved.Target, resolved.Metadata, decision, metadata);

            decisions[decisionKey] = decision;
        }

        return (decisions, userSid);
    }

    /// <summary>
    /// Virtual Filters also apply under the consent bypass without consent services, where <c>TableAccessPolicy</c> is
    /// not used (same rule as the procedure channel): a deny stays a deny, a predicate with its bindings is attached.
    /// </summary>
    private async Task<TableAccessDecision> WithVirtualFiltersAsync(
        TableAccessDecision decision, Autheris.Application.VirtualFilters.MandatoryFilterQuery query, CancellationToken ct)
    {
        var mandatory = await (_mandatoryFilters ?? Autheris.Application.VirtualFilters.NullMandatoryRowFilterResolver.Instance)
            .ResolveAsync(query, ct).ConfigureAwait(false);
        if (mandatory.IsDenied)
        {
            return TableAccessDecision.Denied(decision.Table, mandatory.DenyReason ?? "Denied by virtual filters.");
        }

        if (mandatory.CallerAttributesTextConstant)
        {
            // 129 (BC-129-05, F-4): this consent bypass path is text only; the text of a caller attribute filter is a constant, so the table is denied.
            return TableAccessDecision.Denied(decision.Table, "Denied by virtual filters.");
        }

        return mandatory.PredicateSql != null
            ? decision.WithMandatoryPredicate(mandatory.PredicateSql, mandatory.AppliedFilters, mandatory.AppliedBindings)
            : decision;
    }

    private async Task RecordStartAuditAsync(
        GovernedSqlQueryRequest request,
        IReadOnlyList<ResolvedSourceTable> tables,
        Sid userSid,
        TenantId tenantId,
        IReadOnlyDictionary<TableIdentifier, VirtualFilterTablePlan>? vfPlans,
        IReadOnlyList<VirtualFilterKeySourceRead> keySourceReads,
        string correlationId,
        FederatedParameterContract? parameters,
        int bindCount,
        CancellationToken ct)
    {
        if (_auditLogRepository == null)
        {
            throw new InvalidOperationException("Audit log repository is required for cross-source queries.");
        }

        var (redactedOriginalSql, originalSqlHash) = _auditDigester.Anonymize(request.Sql);
        var tablesAudit = tables.Select(t => new
        {
            table = t.Target.FullName,
            dataSourceType = t.Metadata.Table.DataSourceType.ToString(),
            dataSource = t.EffectiveDataSource
        }).ToList();

        var paramsAudit = parameters?.ToAudit()?.Parameters ?? (IReadOnlyList<FederatedParameterAuditEntry>)Array.Empty<FederatedParameterAuditEntry>();

        var startDetails = JsonSerializer.Serialize(new
        {
            originalSql = redactedOriginalSql,
            originalSqlHash,
            tables = tablesAudit,
            transport = request.Transport ?? "WebSql",
            virtualFilters = VirtualFilterAudit(vfPlans),
            parameters = paramsAudit,
            bindCount
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
                TraceId = correlationId,
                DetailsJson = startDetails
            }, ct).ConfigureAwait(false);

            // F-GOV-14 (FR-9): every key set read is its own record (counts and cache state, never key values).
            await RecordKeySourceReadsAsync(keySourceReads, userSid, tenantId, correlationId, ct).ConfigureAwait(false);
        }
        catch (Exception auditEx)
        {
            _logger?.Error(auditEx, "Failed to record start audit for cross-source query.");
            throw new InvalidOperationException("Audit logging failure during cross-source query execution.", auditEx);
        }
    }

    private async Task RecordKeySourceReadsAsync(
        IReadOnlyList<VirtualFilterKeySourceRead> reads, Sid userSid, TenantId tenantId, string correlationId, CancellationToken ct)
    {
        foreach (var read in reads)
        {
            await _auditLogRepository!.RecordAuditEventAsync(new AuditLogEntry
            {
                TenantId = tenantId,
                EventType = AuditEventTypes.WebSqlCrossSourceSourceRead,
                ActorSid = userSid,
                TargetTable = read.Table.ToString(),
                Decision = "ALLOW",
                TraceId = correlationId,
                DetailsJson = JsonSerializer.Serialize(new
                {
                    kind = "virtualFilterKeySource",
                    table = read.Table.ToString(),
                    filter = read.KeySet.Filter,
                    source = read.KeySet.Source,
                    keyCount = read.KeySet.KeyCount,
                    droppedKeys = read.KeySet.DroppedKeys,
                    cacheHit = read.KeySet.CacheHit
                })
            }, ct).ConfigureAwait(false);
        }
    }

    private static object[] VirtualFilterAudit(IReadOnlyDictionary<TableIdentifier, VirtualFilterTablePlan>? vfPlans) =>
        (vfPlans ?? new Dictionary<TableIdentifier, VirtualFilterTablePlan>())
            .OrderBy(p => p.Key.ToString(), StringComparer.Ordinal)
            .Select(p => (object)new
            {
                table = p.Key.ToString(),
                filters = p.Value.KeySets.Select(k => new { name = k.Filter, bindingHash = k.BindingHash, strategy = k.Strategy }).ToList(),
                tier = p.Value.Tier.ToString(),
                tierReason = p.Value.Reason,
                shortCircuited = p.Value.SkipUpstream,
                pushdownKeyCount = p.Value.PushdownArguments?.Values.OfType<System.Collections.ICollection>().Sum(c => c.Count) ?? 0
            })
            .ToArray();

    private async Task RecordFailureAuditAsync(
        GovernedSqlQueryRequest request,
        TenantId tenantId,
        ClaimsPrincipal user,
        Exception ex,
        string correlationId,
        IReadOnlyList<VirtualFilterKeySourceRead> unauditedKeySourceReads,
        FederatedParameterContract? parameters = null)
    {
        if (_auditLogRepository == null) return;

        try
        {
            if (unauditedKeySourceReads.Count > 0)
            {
                // Keys were read but the query was denied before the start record: the reads are still on record (AC-9.1).
                await RecordKeySourceReadsAsync(unauditedKeySourceReads, ResolveUserSid(user), tenantId, correlationId, CancellationToken.None).ConfigureAwait(false);
            }

            var (redactedOriginalSql, originalSqlHash) = _auditDigester.Anonymize(request.Sql);
            var userSid = ResolveUserSid(user);
            bool isDenied = ex is WebSqlPolicyException or SecurityException or GatewaySecurityException;
            var eventType = isDenied ? AuditEventTypes.WebSqlQueryDenied : AuditEventTypes.QueryExecutionError;

            var reasonCode = ex is GatewaySecurityException gse ? gse.ErrorCode : isDenied ? "POLICY_VIOLATION" : ex.GetType().Name;

            bool hasParams = (request.Parameters != null && request.Parameters.Count > 0) || (parameters != null && parameters.ClientValues.Count > 0);
            string errorText = ex is GatewaySecurityException
                ? $"Access denied ({reasonCode})"
                : hasParams
                    ? $"Query failed ({ex.GetType().Name})"
                    : $"Query failed ({reasonCode})";

            object? paramsAudit = parameters?.ToAudit()?.Parameters;
            if (paramsAudit == null && request.Parameters != null && request.Parameters.Count > 0)
            {
                var list = new List<object>();
                foreach (var (k, v) in request.Parameters)
                {
                    var cleanKey = k.TrimStart('@');
                    bool isList = v is System.Collections.IEnumerable and not string and not byte[];
                    int count = 1;
                    if (isList && v is System.Collections.ICollection c) count = c.Count;
                    else if (isList && v is System.Collections.IEnumerable e) count = e.Cast<object?>().Count();
                    list.Add(new { name = cleanKey, kind = isList ? "list" : "scalar", count });
                }
                paramsAudit = list;
            }

            var detailsObj = new Dictionary<string, object?>
            {
                ["originalSql"] = redactedOriginalSql,
                ["originalSqlHash"] = originalSqlHash,
                ["reasonCode"] = reasonCode,
                ["error"] = errorText
            };
            if (paramsAudit != null)
            {
                detailsObj["parameters"] = paramsAudit;
            }

            var detailsJson = JsonSerializer.Serialize(detailsObj);

            await _auditLogRepository.RecordAuditEventAsync(new AuditLogEntry
            {
                TenantId = tenantId,
                EventType = eventType,
                ActorSid = userSid,
                TargetTable = "cross-source",
                Decision = isDenied ? "DENY" : "ERROR",
                TraceId = correlationId,
                DetailsJson = detailsJson
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception auditEx)
        {
            // Do not mask the original failure, but never lose a denial silently.
            _logger?.Error(auditEx, "Failed to record the failure audit of a cross-source query; the original error is rethrown.");
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
