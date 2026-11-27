namespace Autheris.Application.Mcp.Services;

using Autheris.Application.Tenancy;
using System;
using Autheris.Application.Catalog;
using Autheris.Application.Catalog.Services;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Docs.Interfaces;
using Autheris.Application.Events;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Mcp.Diagnostics;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Tools;
using Autheris.Domain.Common;
using Autheris.Domain.Diagnostics;
using Autheris.Domain.Events;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Enterprise Guardrail engine enforcing zero-trust data protection, access decision point enforcement,
/// automated PII scrubbing, token budgeting, and SHA-256 tamper-evident
/// audit logging on all data supplied to AI agents via the Model Context Protocol (MCP).
/// </summary>
public sealed partial class AiDataGuardrailService : IAiDataGuardrailService
{
    private readonly IMcpToolRegistry _toolRegistry;
    private readonly IOptions<GatewayOptions> _options;
    private readonly ILogger<AiDataGuardrailService> _logger;
    private readonly IMcpQueryExecutor? _queryExecutor;
    private readonly IConsentRepository? _consentRepository;
    private readonly IAuditLogRepository _auditLogRepository;
    private readonly Autheris.Application.Policy.Pdp.IAccessDecisionPoint _decisionPoint;
    private readonly ITableMetadataRepository? _tableMetadataRepository;
    private readonly ISemanticPromptGuardrail _promptGuardrail;
    private readonly IGoldenQueryService? _goldenQueryService;
    private readonly IGraphQlCatalogMap? _graphQlCatalogMap;
    private readonly IAccessPlanningService? _accessPlanningService;
    private readonly IDocSearchService? _docSearchService;
    private readonly IUniversalDocStore? _docStore;
    private readonly IGatewayEventBus? _eventBus;
    private readonly TrinoSqlEngine.ISqlEngine? _sqlEngine;
    private readonly IReadPathSwitches? _switches;
    private readonly Autheris.Application.Governance.Interfaces.IDatasetCatalogAdminService? _datasetCatalogAdmin;
    private readonly Autheris.Application.Procedures.Interfaces.IProcedureRegistry? _procedureRegistry;
    private readonly Autheris.Application.Catalog.Semantic.ISemanticCatalogService? _semanticCatalog;
    private readonly Autheris.Application.Catalog.Interfaces.ICatalogVisibilityService? _catalogVisibility;
    private readonly Autheris.Application.Docs.Interfaces.IDocIndexReadiness? _docIndexReadiness;
    private readonly Autheris.Application.Docs.Services.IDocLinkCallerView? _docLinkReader;
    private readonly Autheris.Application.Audit.IAuditWriter? _auditWriter;
    private readonly TimeProvider _timeProvider;

    private static readonly TimeSpan DefaultRegexTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly Regex EmailRegex = new(@"\b([A-Za-z0-9._%+-])[A-Za-z0-9._%+-]*@([A-Za-z0-9.-]+\.[A-Za-z]{2,})\b", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking, DefaultRegexTimeout);
    // MCP-10: ISO 13616 structure (letters allowed in the BBAN, any case); candidates are confirmed with the mod-97 check.
    private static readonly Regex IbanRegex = new(@"\b[A-Z]{2}[0-9]{2}(?:[ ]?[A-Z0-9]{4}){2,7}(?:[ ]?[A-Z0-9]{1,4})?\b", RegexOptions.Compiled | RegexOptions.IgnoreCase, DefaultRegexTimeout);
    private static readonly Regex GdprArt9Regex = new(@"""(healthCondition|diagnosis|medicalRecord|biometricData|geneticMarker|religiousAffiliation|politicalBelief)""\s*:\s*""([^""]+)""", RegexOptions.Compiled | RegexOptions.IgnoreCase, DefaultRegexTimeout);

    public AiDataGuardrailService(
        IMcpToolRegistry toolRegistry,
        IOptions<GatewayOptions> options,
        ILogger<AiDataGuardrailService> logger,
        IAuditLogRepository auditLogRepository,
        IMcpQueryExecutor? queryExecutor = null,
        Autheris.Application.Policy.Pdp.IAccessDecisionPoint? decisionPoint = null,
        ITableMetadataRepository? tableMetadataRepository = null,
        ISemanticPromptGuardrail? promptGuardrail = null,
        IGoldenQueryService? goldenQueryService = null,
        IHitLStepUpApprovalService? stepUpApprovalService = null,
        IGraphQlCatalogMap? graphQlCatalogMap = null,
        IConsentRepository? consentRepository = null,
        IAccessPlanningService? accessPlanningService = null,
        IDocSearchService? docSearchService = null,
        IUniversalDocStore? docStore = null,
        IGatewayEventBus? eventBus = null,
        TrinoSqlEngine.ISqlEngine? sqlEngine = null,
        IReadPathSwitches? switches = null,
        Autheris.Application.Governance.Interfaces.IDatasetCatalogAdminService? datasetCatalogAdmin = null,
        Autheris.Application.Procedures.Interfaces.IProcedureRegistry? procedureRegistry = null,
        Autheris.Application.Catalog.Semantic.ISemanticCatalogService? semanticCatalog = null,
        Autheris.Application.Catalog.Interfaces.ICatalogVisibilityService? catalogVisibility = null,
        Autheris.Application.Docs.Interfaces.IDocIndexReadiness? docIndexReadiness = null,
        Autheris.Application.Audit.IAuditWriter? auditWriter = null,
        Autheris.Application.Docs.Services.IDocLinkCallerView? docLinkReader = null,
        TimeProvider? timeProvider = null)
    {
        _docLinkReader = docLinkReader;
        _toolRegistry = toolRegistry ?? throw new ArgumentNullException(nameof(toolRegistry));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _auditLogRepository = auditLogRepository ?? throw new ArgumentNullException(nameof(auditLogRepository));
        _queryExecutor = queryExecutor;
        _decisionPoint = decisionPoint ?? throw new ArgumentNullException(nameof(decisionPoint));
        _tableMetadataRepository = tableMetadataRepository;
        _promptGuardrail = promptGuardrail ?? new SemanticPromptGuardrail();
        _goldenQueryService = goldenQueryService;
        _ = stepUpApprovalService;
        _graphQlCatalogMap = graphQlCatalogMap;
        _consentRepository = consentRepository;
        _accessPlanningService = accessPlanningService;
        _docSearchService = docSearchService;
        _docStore = docStore;
        _eventBus = eventBus;
        _sqlEngine = sqlEngine;
        _switches = switches;
        _datasetCatalogAdmin = datasetCatalogAdmin;
        _procedureRegistry = procedureRegistry;
        _semanticCatalog = semanticCatalog;
        _catalogVisibility = catalogVisibility;
        _docIndexReadiness = docIndexReadiness;
        _auditWriter = auditWriter;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Closed tag set for <c>autheris_disabled_path_requests_total</c> (no tool names, no ids).</summary>
    private static string DisabledPathTag(string reason) => reason switch
    {
        ReadPathSwitches.ListedReason => "mcp_listed",
        nameof(McpToolGroup.Catalog) => "mcp_catalog",
        nameof(McpToolGroup.Api) => "mcp_api",
        nameof(McpToolGroup.Access) => "mcp_access",
        _ => "mcp_query"
    };

    // Canonical caller class (never role strings): only the global administrator skips the catalog visibility check. The catalog owner
    // (default-tenant GovernanceAdmin) keeps it, because it drops other tenants' virtual tables and semantic models (DT-SC-08); a
    // tenant-scoped administrator is confined by it, a GovernanceAdmin without a resolvable tenant is no administrator.
    private bool IsUnrestrictedCatalogCaller(McpSessionContext session) =>
        CallerScopeResolver.Resolve(McpProtocolHandler.BuildPrincipalFromSession(session), TenantResolver.EffectiveDefaultOf(_options.Value.Tenancy))
            == CallerScope.Global;

    public async ValueTask<McpToolCallResult> ExecuteToolWithGuardrailAsync(
        McpToolCallRequest request,
        McpSessionContext sessionContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(sessionContext);

        using var activity = McpDiagnostics.ActivitySource.StartActivity($"gen_ai.tool {request.ToolName}", ActivityKind.Internal);
        if (activity != null)
        {
            activity.SetTag(McpDiagnostics.GenAiSystemKey, "autheris_mcp");
            activity.SetTag(McpDiagnostics.GenAiOperationNameKey, "tool_execution");
            activity.SetTag(McpDiagnostics.GenAiToolNameKey, request.ToolName);
            activity.SetTag(McpDiagnostics.GenAiToolCallIdKey, sessionContext.SessionId);
            activity.SetTag(McpDiagnostics.GenAiClientIdKey, sessionContext.ServicePrincipalId);
            var inputTokens = Math.Max(1, (request.ArgumentsJson?.Length ?? 0) / 4);
            activity.SetTag(McpDiagnostics.GenAiUsageInputTokensKey, inputTokens);
            McpDiagnostics.RecordTokenUsage(inputTokens, "input", request.ToolName);
        }

        var tool = _toolRegistry.FindTool(request.ToolName);
        if (tool != null && sessionContext.IsReadOnly && McpDatasetTools.IsHiddenFromReadOnlySession(tool.Name))
        {
            tool = null;
        }

        if (tool == null)
        {
            activity?.SetTag(McpDiagnostics.GenAiGuardrailVerdictKey, "deny");
            McpDiagnostics.RecordGuardrailVerdict("deny", request.ToolName, false, false);
            _logger.LogWarning("AI Agent attempted to call unauthorized or unknown MCP tool '{ToolName}'.", request.ToolName);
            // 115 C-115-4: the internal audit row names the switch behind the denial; the caller-visible result below is
            // byte-identical to the one for a name that never existed. Only names a switch really closed get a reason and a count.
            var disabledReason = _switches?.DisabledReason(request.ToolName);
            var denyDetails = $"Tool '{request.ToolName}' is not registered or allowed.";
            if (disabledReason is not null)
            {
                denyDetails = $"Tool '{request.ToolName}' is not registered or allowed. Tool disabled by configuration ({disabledReason}).";
                GatewayDiagnostics.DisabledPathRequestCounter.Add(1, new KeyValuePair<string, object?>("path", DisabledPathTag(disabledReason)));
            }

            await RecordAuditEventAsync(request.ToolName, sessionContext, "DENY", denyDetails, false, false, 0, cancellationToken).ConfigureAwait(false);
            return McpToolError.Fail(McpErrorCodes.ToolNotAvailable);
        }

        // The admin-role check precedes the input guardrail: an unknown tool and a hidden admin tool answer alike, with or without
        // injection text in the arguments.
        if (McpAdminTools.IsAdminTool(tool.Name) && !McpAdminTools.CanUseAdminTools(sessionContext, _options.Value.IsMcpAuthBypassed))
        {
            activity?.SetTag(McpDiagnostics.GenAiGuardrailVerdictKey, "deny");
            McpDiagnostics.RecordGuardrailVerdict("deny", tool.Name, false, false);
            await RecordAuditEventAsync(tool.Name, sessionContext, "DENY", $"Tool execution denied: Admin tool '{tool.Name}' requires the ClusterAdmin role (and a read/write session).", false, false, 0, cancellationToken).ConfigureAwait(false);
            return McpToolError.Fail(McpErrorCodes.ToolNotAvailable);
        }

        var promptEvaluation = _promptGuardrail.EvaluatePrompt(request.ToolName, request.ArgumentsJson);

        // A tool whose caller-specific denial answers TOOL_NOT_AVAILABLE (a curated tool) is blocked by the input guardrail only after
        // its target gates, so an injection text cannot tell a tool the caller may not use from a name that does not exist.
        var deferPromptBlock = !promptEvaluation.IsAllowed && !McpDocTools.IsDocTool(tool.Name) && !McpAdminTools.IsAdminTool(tool.Name)
            && McpErrorCodes.TargetDeniedCode(tool.Name) == McpErrorCodes.ToolNotAvailable;
        if (!promptEvaluation.IsAllowed && !deferPromptBlock)
        {
            return await DenyByPromptGuardAsync(activity, request, sessionContext, promptEvaluation, cancellationToken).ConfigureAwait(false);
        }

        if (McpAdminTools.IsAdminTool(tool.Name))
        {
            if (_accessPlanningService != null)
            {
                var adminResult = await McpAdminTools.ExecuteAdminToolAsync(tool.Name, request.ArgumentsJson ?? "{}", sessionContext, _accessPlanningService, _datasetCatalogAdmin, cancellationToken).ConfigureAwait(false);

                // Every admin tool call leaves an MCP_TOOL_EXECUTION row, the successful (mutating) ones included. The details are fixed
                // texts: the arguments can carry secrets (inline datasource credentials) and the error is already a fixed message.
                var adminVerdict = adminResult.IsSuccess ? "allow" : "deny";
                activity?.SetTag(McpDiagnostics.GenAiGuardrailVerdictKey, adminVerdict);
                McpDiagnostics.RecordGuardrailVerdict(adminVerdict, tool.Name, false, false);
                await RecordAuditEventAsync(
                    tool.Name,
                    sessionContext,
                    adminResult.IsSuccess ? "ALLOW" : "DENY",
                    adminResult.IsSuccess
                        ? $"Admin tool '{tool.Name}' executed by a cluster administrator."
                        : $"Admin tool '{tool.Name}' was rejected: {adminResult.ErrorMessage ?? "ERROR"}",
                    false,
                    false,
                    Math.Max(1, (adminResult.ContentJson?.Length ?? 0) / 4),
                    cancellationToken).ConfigureAwait(false);
                return adminResult.IsSuccess ? adminResult : McpToolError.FailAdmin(adminResult.ErrorMessage);
            }
        }

        if (McpDocTools.IsDocTool(tool.Name))
        {
            var rawDocJson = await McpDocTools.ExecuteDocToolAsync(tool.Name, request.ArgumentsJson ?? "{}", _docSearchService, _docStore, sessionContext, _auditWriter, _docIndexReadiness, _docLinkReader, cancellationToken).ConfigureAwait(false);
            bool docWasMasked = false;
            string scrubbedDocJson = !_options.Value.IsMcpUnmaskedAllowed ? ScrubPiiAndSensitiveData(rawDocJson, out docWasMasked) : rawDocJson;
            bool isError = TryGetExecutorError(rawDocJson, out var docErr);
            if (!isError)
            {
                scrubbedDocJson = await InspectOutputAsync(tool.Name, scrubbedDocJson, sessionContext, cancellationToken, m => docWasMasked |= m).ConfigureAwait(false);
            }

            // MCP-08: doc tools run under the same per-call token budget as dataset tools.
            scrubbedDocJson = ApplyTokenBudget(tool.Name, scrubbedDocJson, out var docTokens, out var docTruncated, out var docBudgetFailed);
            await RecordAuditEventAsync(tool.Name, sessionContext, isError ? "DENY" : "ALLOW",
                isError ? $"Doc tool returned error ({docErr ?? "ERROR"})." : "Document MCP tool executed successfully under AI guardrails.",
                docWasMasked, docTruncated, docTokens, cancellationToken).ConfigureAwait(false);
            var docFailed = isError || docBudgetFailed;
            var docContent = isError ? McpToolError.Normalize(rawDocJson) : scrubbedDocJson;
            return new McpToolCallResult(IsSuccess: !docFailed, ContentJson: docContent, ErrorMessage: docFailed ? docContent : null, EstimatedTokens: docTokens, IsMasked: docWasMasked, TruncatedDueToBudget: docTruncated);
        }

        if (string.Equals(tool.Name, McpDatasetTools.SampleRows, StringComparison.OrdinalIgnoreCase))
        {
            if (!McpDatasetTools.TryGetStringArgument(request.ArgumentsJson, "dataset", out var dsArg) || string.IsNullOrWhiteSpace(dsArg))
            {
                return McpToolError.Fail(McpErrorCodes.InvalidParams, "Missing or invalid required argument 'dataset'.");
            }
        }

        string? sqlSyntaxError = null;
        Exception? sqlFailure = null;
        IReadOnlyList<TableIdentifier>? resolvedTables;
        bool unresolvedDescribeGraphQlField = false;
        if (string.Equals(tool.Name, McpDatasetTools.DescribeDataset, StringComparison.OrdinalIgnoreCase))
        {
            bool hasDataset = McpDatasetTools.TryGetStringArgument(request.ArgumentsJson, "dataset", out var dsArg) && !string.IsNullOrWhiteSpace(dsArg);
            bool hasGraphQlField = McpDatasetTools.TryGetStringArgument(request.ArgumentsJson, "graphQlField", out var gqlArg) && !string.IsNullOrWhiteSpace(gqlArg);
            bool hasSqlName = McpDatasetTools.TryGetStringArgument(request.ArgumentsJson, "sqlName", out var sqlArg) && !string.IsNullOrWhiteSpace(sqlArg);
            int provided = (hasDataset ? 1 : 0) + (hasGraphQlField ? 1 : 0) + (hasSqlName ? 1 : 0);
            if (provided > 1)
            {
                return McpToolError.Fail(McpErrorCodes.InvalidParams, "Invalid arguments: specify only one of 'dataset', 'graphQlField', or 'sqlName'.");
            }
            if (provided == 0)
            {
                return McpToolError.Fail(McpErrorCodes.InvalidParams, "Missing or invalid required argument 'dataset', 'graphQlField', or 'sqlName'.");
            }

            if (hasSqlName)
            {
                resolvedTables = McpDatasetCatalog.TryParseDatasetId(sqlArg, out var sqlTableId)
                    ? [sqlTableId]
                    : null;
            }
            else if (hasGraphQlField)
            {
                var target = _graphQlCatalogMap is null
                    ? null
                    : await _graphQlCatalogMap.ResolveRootFieldAsync(gqlArg!, cancellationToken).ConfigureAwait(false);
                if (target is { } tableId)
                {
                    resolvedTables = [tableId];
                }
                else if (_options.Value.IsMcpAuthBypassed || McpDatasetTools.IsBriefRequested(request.ArgumentsJson))
                {
                    // Unknown and invisible GraphQL fields must behave identically on the value-free/bypassed path.
                    resolvedTables = null;
                    unresolvedDescribeGraphQlField = true;
                }
                else
                {
                    activity?.SetTag(McpDiagnostics.GenAiGuardrailVerdictKey, "deny");
                    McpDiagnostics.RecordGuardrailVerdict("deny", tool.Name, false, false);
                    await RecordAuditEventAsync(tool.Name, sessionContext, "DENY", "Access denied by access policy: DEFAULT_DENY", false, false, 0, cancellationToken).ConfigureAwait(false);
                    return McpToolError.Fail(McpErrorCodes.TargetDeniedCode(tool.Name));
                }
            }
            else
            {
                resolvedTables = await ParseTablesFromToolAsync(tool, request.ArgumentsJson, _sqlEngine, _options.Value.WebSql?.DefaultDataSourceName, cancellationToken).ConfigureAwait(false);
            }
        }
        else if (string.Equals(tool.Name, McpDatasetTools.QueryGraphQl, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(tool.Name, "simulate_query", StringComparison.OrdinalIgnoreCase))
        {
            resolvedTables = await ResolveGraphQlTablesAsync(request.ArgumentsJson, cancellationToken).ConfigureAwait(false);
        }
        else if (string.Equals(tool.Name, McpDatasetTools.QuerySql, StringComparison.OrdinalIgnoreCase))
        {
            var sql = McpDatasetTools.TryGetStringArgument(request.ArgumentsJson, "query", out var s) ? s : null;
            if (string.IsNullOrWhiteSpace(sql))
            {
                return McpToolError.Fail(McpErrorCodes.InvalidParams, "Missing required argument 'query'.");
            }

            var (tables, syntaxErr, failure) = await McpDataTargetResolver.TryResolveSqlTablesWithFailureAsync(sql, _sqlEngine, _options.Value.WebSql?.DefaultDataSourceName, cancellationToken).ConfigureAwait(false);
            resolvedTables = tables;
            sqlSyntaxError = syntaxErr;
            sqlFailure = failure;
        }
        else
        {
            resolvedTables = await ParseTablesFromToolAsync(tool, request.ArgumentsJson, _sqlEngine, _options.Value.WebSql?.DefaultDataSourceName, cancellationToken).ConfigureAwait(false);
        }

        if (string.Equals(sqlSyntaxError, McpDataTargetResolver.ConstructNotSupportedMarker, StringComparison.Ordinal))
        {
            // identical text for every table state (existing, forbidden, unknown); fixed message, no request text.
            var constructMessage = $"{TrinoSqlEngine.Ast.Emit.GovernedSqlErrorCodes.ConstructNotSupported}: " +
                                   TrinoSqlEngine.Ast.Emit.GovernedSqlErrorCodes.FixedMessage(TrinoSqlEngine.Ast.Emit.GovernedSqlErrorCodes.ConstructNotSupported);
            activity?.SetTag(McpDiagnostics.GenAiGuardrailVerdictKey, "deny");
            McpDiagnostics.RecordGuardrailVerdict("deny", tool.Name, false, false);
            await RecordAuditEventAsync(tool.Name, sessionContext, "DENY", constructMessage, false, false, 0, cancellationToken).ConfigureAwait(false);
            return McpToolError.Fail(TrinoSqlEngine.Ast.Emit.GovernedSqlErrorCodes.ConstructNotSupported);
        }

        if (sqlSyntaxError != null)
        {
            activity?.SetTag(McpDiagnostics.GenAiGuardrailVerdictKey, "deny");
            McpDiagnostics.RecordGuardrailVerdict("deny", tool.Name, false, false);
            _logger.LogWarning("SQL syntax error in MCP tool '{ToolName}': target data table could not be resolved: {Error}", tool.Name, sqlSyntaxError);
            await RecordAuditEventAsync(tool.Name, sessionContext, "DENY", $"SQL_SYNTAX_ERROR: target data table could not be resolved: {sqlSyntaxError}", false, false, 0, cancellationToken).ConfigureAwait(false);
            return McpToolError.FailSql(sqlFailure);
        }

        bool isSelfAccessTool = string.Equals(tool.Name, McpDatasetTools.DescribeMyAccess, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(tool.Name, McpDatasetTools.GetMyPermissions, StringComparison.OrdinalIgnoreCase);

        if (resolvedTables == null && !isSelfAccessTool && !unresolvedDescribeGraphQlField &&
            (tool.Name.StartsWith("query_", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(tool.TargetGraphQLOperation) || McpDatasetTools.IsDatasetTool(tool.Name)))
        {
            activity?.SetTag(McpDiagnostics.GenAiGuardrailVerdictKey, "deny");
            McpDiagnostics.RecordGuardrailVerdict("deny", tool.Name, false, false);
            _logger.LogWarning("Zero-Trust Fail-Closed: Could not determine target table for MCP tool '{ToolName}'. Denying automated AI agent execution.", tool.Name);
            await RecordAuditEventAsync(tool.Name, sessionContext, "DENY", "Unresolvable target table for data access tool (fail-closed).", false, false, 0, cancellationToken).ConfigureAwait(false);
            return McpToolError.Fail(McpErrorCodes.TargetDeniedCode(tool.Name));
        }

        IReadOnlyList<TableIdentifier> abacTables = resolvedTables ?? [new TableIdentifier("mcp", "tool", tool.Name.ToLowerInvariant())];
        foreach (var effectiveTable in abacTables)
        {
            bool isSchemaTool = _options.Value.IsMcpAuthBypassed &&
                (string.Equals(tool.Name, "query_data_catalog", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(tool.Name, "get_golden_queries", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(effectiveTable.Domain, "governance", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(effectiveTable.Domain, "catalog", StringComparison.OrdinalIgnoreCase));

            // The one decision point decides every disclosed table of a data tool (pseudo targets of tools without a data table are not objects;
            // call_procedure and query_metrics are decided on the real tables behind the call below).
            if (resolvedTables != null && !_options.Value.IsMcpAuthBypassed && !isSchemaTool && !ReadsThroughUnderlyingTables(tool) && !IsCatalogToolTarget(tool, effectiveTable) &&
                !await IsValueFreeDescribeAsync(tool, request.ArgumentsJson, sessionContext, effectiveTable, cancellationToken).ConfigureAwait(false))
            {
                var denied = await DenyByDecisionAsync(activity, tool, sessionContext, effectiveTable, cancellationToken).ConfigureAwait(false);
                if (denied != null)
                {
                    return denied;
                }
            }
        }

        var checksUnderlyingTables = ToolReadsDatasetRows(tool);

        // call_procedure and query_metrics: the ABAC object above is a pseudo table (procedure.dbo.<name>, the semantic catalog);
        // the inactive / metadata gate below runs on the real tables behind the call instead, failing closed if unresolvable.
        IReadOnlyList<TableIdentifier>? gatedTables = resolvedTables;
        var gatesUnderlyingTables = checksUnderlyingTables && ReadsThroughUnderlyingTables(tool);
        if (gatesUnderlyingTables)
        {
            gatedTables = await ResolveUnderlyingTablesAsync(tool, request.ArgumentsJson, sessionContext, cancellationToken).ConfigureAwait(false);
            if (gatedTables == null)
            {
                activity?.SetTag(McpDiagnostics.GenAiGuardrailVerdictKey, "deny");
                McpDiagnostics.RecordGuardrailVerdict("deny", tool.Name, false, false);
                _logger.LogWarning("Zero-Trust Fail-Closed: Could not resolve the tables behind MCP tool '{ToolName}'. Denying execution.", tool.Name);
                await RecordAuditEventAsync(tool.Name, sessionContext, "DENY", "Tool execution denied: the tables behind the call could not be resolved (fail-closed).", false, false, 0, cancellationToken).ConfigureAwait(false);
                return McpToolError.Fail(McpErrorCodes.TargetDeniedCode(tool.Name));
            }

            // The decision point decides the real tables behind the call before dataset metadata checks.
            if (!_options.Value.IsMcpAuthBypassed)
            {
                foreach (var underlying in gatedTables)
                {
                    var denied = await DenyByDecisionAsync(activity, tool, sessionContext, underlying, cancellationToken).ConfigureAwait(false);
                    if (denied != null)
                    {
                        return denied;
                    }
                }
            }
        }

        foreach (TableIdentifier? resolvedTable in checksUnderlyingTables ? gatedTables ?? [] : [])
        {
            if (_tableMetadataRepository == null || resolvedTable == null ||
                resolvedTable.Value.Equals(McpDatasetTools.CatalogTable))
            {
                continue;
            }

            TableMetadata? meta;
            var unavailable = false;
            try
            {
                meta = await _tableMetadataRepository.GetTableMetadataAsync(resolvedTable.Value, cancellationToken).ConfigureAwait(false);
            }
            catch (Autheris.Domain.Exceptions.TableNotFoundException)
            {
                // Issue #94 (M3): unavailable metadata is handled like an inactive table (fail closed, same answer).
                meta = null;
                unavailable = true;
            }

            // A table behind a procedure or metric without catalog metadata cannot be classified: same answer as an inactive one.
            if (unavailable || (meta == null && gatesUnderlyingTables) || (meta != null && !meta.Table.IsActive))
            {
                activity?.SetTag(McpDiagnostics.GenAiGuardrailVerdictKey, "deny");
                McpDiagnostics.RecordGuardrailVerdict("deny", tool.Name, false, false);
                _logger.LogWarning("Tool '{ToolName}' targets inactive table '{Table}'. Denying execution.", tool.Name, resolvedTable);
                await RecordAuditEventAsync(tool.Name, sessionContext, "DENY", $"Tool execution denied: target table {resolvedTable} is inactive.", false, false, 0, cancellationToken).ConfigureAwait(false);
                return McpToolError.Fail(McpErrorCodes.TargetDeniedCode(tool.Name));
            }
        }

        if (deferPromptBlock)
        {
            return await DenyByPromptGuardAsync(activity, request, sessionContext, promptEvaluation, cancellationToken).ConfigureAwait(false);
        }

        string rawDataJson;
        bool? policyOutcome = null;
        var argsJson = request.ArgumentsJson ?? "{}";
        if (tool.Name.Equals("get_golden_queries", StringComparison.OrdinalIgnoreCase) && _goldenQueryService != null)
        {
            string? domain = null, tableName = null;
            try
            {
                using var argDoc = JsonDocument.Parse(argsJson);
                if (argDoc.RootElement.TryGetProperty("domain", out var dProp) && dProp.ValueKind == JsonValueKind.String) domain = dProp.GetString();
                if (argDoc.RootElement.TryGetProperty("tableName", out var tProp) && tProp.ValueKind == JsonValueKind.String) tableName = tProp.GetString();
            }
            catch (JsonException) { }

            var isMcpAuthBypassed = _options.Value.IsMcpAuthBypassed;
            bool isAdmin = IsUnrestrictedCatalogCaller(sessionContext);
            IReadOnlyList<GoldenQuery> queries;
            Autheris.Domain.Common.TenantId? callerTenant = Autheris.Domain.Common.TenantId.TryParse(sessionContext.TenantId, out var parsedTenant) && !Autheris.Domain.Common.TenantId.IsReserved(parsedTenant) ? parsedTenant : (Autheris.Domain.Common.TenantId?)null;

            if (isMcpAuthBypassed || isAdmin)
            {
                // MCP-15: a null predicate still keeps the per-request cap.
                var candidateSet = await _goldenQueryService.GetCandidateQueriesAsync(callerTenant, domain, tableName, cancellationToken).ConfigureAwait(false);
                queries = candidateSet.HasKnownCandidates && candidateSet.Queries.Count == 0
                    ? Array.Empty<GoldenQuery>()
                    : await _goldenQueryService.GetGoldenQueriesAsync(GoldenQueryReadContext.AllVisible(callerTenant), domain, tableName, cancellationToken).ConfigureAwait(false);
            }
            else if (_consentRepository != null)
            {
                // MCP-15: visibility is evaluated first and applied before the cap; any failure yields an empty list, never "all".
                HashSet<(string Domain, string Table)>? visibleKeys = null;
                IReadOnlyList<TableMetadata>? visibleTables = null;
                System.Security.Claims.ClaimsPrincipal? callerPrincipal = null;
                try
                {
                    callerPrincipal = McpProtocolHandler.BuildPrincipalFromSession(sessionContext, _timeProvider);
                    var allTables = _tableMetadataRepository != null
                        ? await CatalogTablePageReader.ReadAllAsync(_tableMetadataRepository, new CatalogTableQuery(IncludeInactive: false), static t => t.Table.IsActive, ct: cancellationToken).ConfigureAwait(false)
                        : Array.Empty<TableMetadata>();
                    visibleTables = await McpCatalogVisibility.VisibleTablesAsync(allTables, callerPrincipal, _consentRepository, _options.Value, _catalogVisibility, cancellationToken).ConfigureAwait(false);
                    visibleKeys = visibleTables?.Select(t => (Domain: t.Identifier.Domain.ToLowerInvariant(), Table: t.Identifier.TableName.ToLowerInvariant())).ToHashSet();
                }
                catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
                {
                    visibleKeys = null;
                }

                // Caller visibility, hidden and denied columns and relation targets come from the shared caller view and are applied before the cap.
                var hiddenByTable = new Dictionary<Autheris.Domain.Common.TableIdentifier, IReadOnlySet<string>>();
                var resolved = visibleKeys is not null;
                if (resolved && _decisionPoint is not null && callerPrincipal is not null && visibleTables is { Count: > 0 })
                {
                    try
                    {
                        var decisions = await _decisionPoint.DecideManyAsync(
                            Autheris.Domain.Security.SecurityPrincipalContext.FromPrincipal(callerPrincipal, sessionContext.TenantId, timeProvider: _timeProvider),
                            visibleTables,
                            Autheris.Application.Policy.Pdp.AccessOperation.Discover,
                            Autheris.Application.Policy.Pdp.AccessTransport.Mcp,
                            cancellationToken).ConfigureAwait(false);
                        foreach (var d in decisions ?? [])
                        {
                            if (d is null) continue;
                            var hidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            foreach (var column in d.HiddenCatalogTextColumns ?? new HashSet<string>())
                            {
                                if (d.IsCatalogTextHidden(column)) hidden.Add(column);
                            }

                            foreach (var (column, level) in d.ColumnAccess)
                            {
                                if (level == Autheris.Domain.Interfaces.ColumnAccessLevel.Deny) hidden.Add(column);
                            }

                            if (hidden.Count > 0) hiddenByTable[d.Table] = hidden;
                        }
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
                    {
                        resolved = false;
                    }
                }

                if (!resolved)
                {
                    queries = Array.Empty<GoldenQuery>();
                }
                else
                {
                    var candidateSet = await _goldenQueryService.GetCandidateQueriesAsync(callerTenant, domain, tableName, cancellationToken).ConfigureAwait(false);
                    if (candidateSet.HasKnownCandidates && candidateSet.Queries.Count == 0)
                    {
                        queries = Array.Empty<GoldenQuery>();
                    }
                    else
                    {
                        var visibleSet = visibleTables!.Select(t => t.Identifier).ToHashSet();
                        var seeds = visibleTables!
                            .Where(t => (string.IsNullOrWhiteSpace(domain) || string.Equals(t.Identifier.Domain, domain, StringComparison.OrdinalIgnoreCase)) &&
                                        (string.IsNullOrWhiteSpace(tableName) || string.Equals(t.Identifier.TableName, tableName, StringComparison.OrdinalIgnoreCase)))
                            .Select(t => new GoldenQueryCallerView.Seed(t.Identifier.Domain, t.Identifier.TableName, t.Identifier));
                        var view = await GoldenQueryCallerView.BuildAsync(
                            seeds,
                            _graphQlCatalogMap,
                            (targets, _) => Task.FromResult<IReadOnlySet<Autheris.Domain.Common.TableIdentifier>>(targets.Where(visibleSet.Contains).ToHashSet()),
                            (id, _, _, _, _) => Task.FromResult<IReadOnlySet<string>?>(id is { } ident && hiddenByTable.TryGetValue(ident, out var h) ? h : new HashSet<string>(StringComparer.OrdinalIgnoreCase)),
                            candidateSet.HasKnownCandidates ? candidateSet.Queries : null,
                            cancellationToken).ConfigureAwait(false);
                        queries = await _goldenQueryService.GetGoldenQueriesAsync(view.ToContext(callerTenant), domain, tableName, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            else
            {
                // MCP-15: fail closed - without a consent repository visibility cannot be evaluated (this also covers the anonymous client).
                queries = Array.Empty<GoldenQuery>();
            }

            rawDataJson = JsonSerializer.Serialize(new { tenantId = sessionContext.TenantId, contentNotice = McpDocTools.UntrustedContentNotice, queries });
        }
        else if (_queryExecutor != null)
        {
            using var maskingScope = McpResultMasking.Begin();
            rawDataJson = await _queryExecutor.ExecuteOperationAsync(tool, argsJson, sessionContext, cancellationToken).ConfigureAwait(false);
            policyOutcome = maskingScope.Outcome;
        }
        else
        {
            rawDataJson = GenerateRawToolResponse(tool, argsJson, sessionContext.TenantId);
        }

        bool shouldMask = !_options.Value.IsMcpUnmaskedAllowed;
        bool wasMasked = false;
        string scrubbedJson = shouldMask ? ScrubPiiAndSensitiveData(rawDataJson, out wasMasked) : rawDataJson;
        if (!shouldMask)
        {
            _logger.LogWarning("SECURITY ALERT [WARN]: AI tool execution for tool '{ToolName}' is running unmasked (warn_allow_unmasked_ai_access is ACTIVE).", tool.Name);
        }

        if (TryGetExecutorError(rawDataJson, out var errorCode))
        {
            activity?.SetTag(McpDiagnostics.GenAiGuardrailVerdictKey, "deny");
            McpDiagnostics.RecordGuardrailVerdict("deny", tool.Name, wasMasked, false);
            await RecordAuditEventAsync(tool.Name, sessionContext, "DENY", $"Tool execution returned an error result ({errorCode ?? "UNKNOWN"}).", wasMasked, false, 0, cancellationToken).ConfigureAwait(false);
            var errorEnvelope = McpToolError.Normalize(rawDataJson);
            return new McpToolCallResult(IsSuccess: false, ContentJson: errorEnvelope, ErrorMessage: errorEnvelope, IsMasked: wasMasked || policyOutcome == true);
        }

        scrubbedJson = await InspectOutputAsync(tool.Name, scrubbedJson, sessionContext, cancellationToken, m => wasMasked |= m).ConfigureAwait(false);
        scrubbedJson = ApplyTokenBudget(tool.Name, scrubbedJson, out var estimatedTokens, out var truncated, out var budgetFailed, argsJson);

        await RecordAuditEventAsync(tool.Name, sessionContext, "ALLOW", "Tool executed successfully under AI guardrails.", wasMasked, truncated, estimatedTokens, cancellationToken).ConfigureAwait(false);

        var verdict = wasMasked ? "masked" : "allow";
        activity?.SetTag(McpDiagnostics.GenAiGuardrailVerdictKey, verdict);
        activity?.SetTag(McpDiagnostics.GenAiGuardrailPiiDetectedKey, wasMasked);
        activity?.SetTag(McpDiagnostics.GenAiUsageOutputTokensKey, estimatedTokens);
        McpDiagnostics.RecordTokenUsage(estimatedTokens, "output", tool.Name);
        McpDiagnostics.RecordGuardrailVerdict(verdict, tool.Name, wasMasked, false);

        return new McpToolCallResult(IsSuccess: !budgetFailed, ContentJson: scrubbedJson, ErrorMessage: budgetFailed ? scrubbedJson : null, EstimatedTokens: estimatedTokens, IsMasked: wasMasked || (policyOutcome ?? (_queryExecutor != null && McpResultMasking.IsGovernedDataTool(tool))), TruncatedDueToBudget: truncated);
    }

    /// <summary>The audited block of a request refused by the input guardrail; the caller sees the fixed code only.</summary>
    private async ValueTask<McpToolCallResult> DenyByPromptGuardAsync(
        Activity? activity, McpToolCallRequest request, McpSessionContext sessionContext, PromptGuardrailEvaluation promptEvaluation, CancellationToken cancellationToken)
    {
        activity?.SetTag(McpDiagnostics.GenAiGuardrailVerdictKey, "deny");
        McpDiagnostics.RecordGuardrailVerdict("deny", request.ToolName, false, false);
        _logger.LogWarning("Semantic prompt injection or jailbreak detected for tool '{ToolName}': {Reason}", request.ToolName, promptEvaluation.Reason);
        await RecordAuditEventAsync(request.ToolName, sessionContext, "DENY", $"Execution blocked by prompt injection guardrail ({promptEvaluation.AttackType}): {promptEvaluation.Reason}", false, false, 0, cancellationToken).ConfigureAwait(false);

        if (_eventBus != null)
        {
            await _eventBus.PublishAsync(new SecurityAlertEvent(
                EventId: Guid.NewGuid().ToString("N"),
                AlertType: promptEvaluation.AttackType ?? "PROMPT_INJECTION",
                Severity: "HIGH",
                Details: promptEvaluation.Reason ?? "Prompt injection detected",
                SourceIp: sessionContext.ClientIp,
                UserSid: sessionContext.UserSid,
                Timestamp: DateTimeOffset.UtcNow,
                TenantId: sessionContext.TenantId), cancellationToken).ConfigureAwait(false);
        }

        return McpToolError.Fail(McpErrorCodes.RequestBlocked);
    }

    /// <summary>Audit and metric label of a resource read (the audit row names the URI where a tool call names the tool).</summary>
    internal static string ResourceAuditName(string resourceUri) => "resources/read " + resourceUri;

    /// <summary>The decision point check of one table: null when allowed, otherwise the audited deny result.</summary>
    private async ValueTask<McpToolCallResult?> DenyByDecisionAsync(
        Activity? activity, McpToolDefinition tool, McpSessionContext sessionContext, TableIdentifier table, CancellationToken cancellationToken)
    {
        var policyDecision = await DecideReadAsync(sessionContext, table, cancellationToken).ConfigureAwait(false);
        if (policyDecision.IsAllowed)
        {
            return null;
        }

        activity?.SetTag(McpDiagnostics.GenAiGuardrailVerdictKey, "deny");
        McpDiagnostics.RecordGuardrailVerdict("deny", tool.Name, false, false);
        var reasonCodes = ReasonCodesOf(policyDecision);
        _logger.LogWarning("Decision point denied AI Agent '{Principal}' tool call '{ToolName}' (target: '{TargetTable}') in tenant '{TenantId}'. Reasons: {Reasons}",
            sessionContext.ServicePrincipalId, tool.Name, table, sessionContext.TenantId, reasonCodes);
        await RecordAuditEventAsync(tool.Name, sessionContext, "DENY", $"Access denied by access policy: {reasonCodes}", false, false, 0, cancellationToken).ConfigureAwait(false);

        if (_eventBus != null)
        {
            await _eventBus.PublishAsync(new PolicyDeniedEvent(
                EventId: Guid.NewGuid().ToString("N"), SubjectSid: CallerSidOf(sessionContext), Action: tool.Name,
                Resource: table.ToString(), Reason: PolicyDeniedEvent.DefaultReasonCode,
                Timestamp: DateTimeOffset.UtcNow, TenantId: sessionContext.TenantId,
                ReasonCode: PolicyDeniedEvent.DefaultReasonCode), cancellationToken).ConfigureAwait(false);
        }

        return McpToolError.Fail(McpErrorCodes.TargetDeniedCode(tool.Name));
    }

    public async ValueTask<McpToolCallResult> ReadResourceWithGuardrailAsync(
        string resourceUri,
        IReadOnlyList<TableIdentifier> targetTables,
        Func<CancellationToken, Task<string>> readContent,
        McpSessionContext sessionContext,
        bool applyTokenBudget = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceUri);
        ArgumentNullException.ThrowIfNull(targetTables);
        ArgumentNullException.ThrowIfNull(readContent);
        ArgumentNullException.ThrowIfNull(sessionContext);

        var name = ResourceAuditName(resourceUri);
        var notFound = new McpToolCallResult(IsSuccess: false, ContentJson: "{}", ErrorMessage: "Resource not found.");

        using var activity = McpDiagnostics.ActivitySource.StartActivity($"gen_ai.resource {resourceUri}", ActivityKind.Internal);
        activity?.SetTag(McpDiagnostics.GenAiSystemKey, "autheris_mcp");
        activity?.SetTag(McpDiagnostics.GenAiOperationNameKey, "resource_read");
        activity?.SetTag(McpDiagnostics.GenAiToolCallIdKey, sessionContext.SessionId);
        activity?.SetTag(McpDiagnostics.GenAiClientIdKey, sessionContext.ServicePrincipalId);

        // Fail closed: a read without a target is never treated as unrestricted.
        if (targetTables.Count == 0)
        {
            activity?.SetTag(McpDiagnostics.GenAiGuardrailVerdictKey, "deny");
            await RecordAuditEventAsync(name, sessionContext, "DENY", "Unresolvable target table for resource read (fail-closed).", false, false, 0, cancellationToken).ConfigureAwait(false);
            return notFound;
        }

        // Same decision as the equivalent tool call, on every table the resource discloses.
        if (!_options.Value.IsMcpAuthBypassed)
        {
            foreach (var table in targetTables)
            {
                if (McpResourceProvider.IsCatalogLevelResource(resourceUri) ||
                    (table.Equals(McpSemanticTools.SemanticCatalogTable) && !resourceUri.StartsWith("autheris://catalog/datasets/", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var decision = await DecideReadAsync(sessionContext, table, cancellationToken).ConfigureAwait(false);
                if (!decision.IsAllowed)
                {
                    activity?.SetTag(McpDiagnostics.GenAiGuardrailVerdictKey, "deny");
                    _logger.LogWarning("Decision point denied AI Agent '{Principal}' resource read '{Uri}' (target: '{TargetTable}') in tenant '{TenantId}'.",
                        sessionContext.ServicePrincipalId, resourceUri, table, sessionContext.TenantId);
                    await RecordAuditEventAsync(name, sessionContext, "DENY", $"Access denied by access policy: {ReasonCodesOf(decision)}", false, false, 0, cancellationToken).ConfigureAwait(false);

                    if (_eventBus != null)
                    {
                        await _eventBus.PublishAsync(new PolicyDeniedEvent(
                            EventId: Guid.NewGuid().ToString("N"), SubjectSid: CallerSidOf(sessionContext), Action: "resources/read",
                            Resource: table.ToString(), Reason: PolicyDeniedEvent.DefaultReasonCode,
                            Timestamp: DateTimeOffset.UtcNow, TenantId: sessionContext.TenantId,
                            ReasonCode: PolicyDeniedEvent.DefaultReasonCode), cancellationToken).ConfigureAwait(false);
                    }

                    // Denied and unknown resources answer alike (no existence oracle).
                    return notFound;
                }
            }
        }

        string rawText;
        try
        {
            rawText = await readContent(cancellationToken).ConfigureAwait(false);
        }
        catch (McpResourceNotFoundException)
        {
            activity?.SetTag(McpDiagnostics.GenAiGuardrailVerdictKey, "deny");
            await RecordAuditEventAsync(name, sessionContext, "DENY", "Resource not found or not visible to the caller.", false, false, 0, cancellationToken).ConfigureAwait(false);
            return notFound;
        }

        bool shouldMask = !_options.Value.IsMcpUnmaskedAllowed;
        bool wasMasked = false;
        string text = shouldMask ? ScrubPiiAndSensitiveData(rawText ?? string.Empty, out wasMasked) : rawText ?? string.Empty;
        if (!shouldMask)
        {
            _logger.LogWarning("SECURITY ALERT [WARN]: AI resource read '{Uri}' is running unmasked (warn_allow_unmasked_ai_access is ACTIVE).", resourceUri);
        }

        text = await InspectOutputAsync(name, text, sessionContext, cancellationToken, m => wasMasked |= m).ConfigureAwait(false);

        int estimatedTokens = Math.Max(1, text.Length / 4);
        bool truncated = false;
        int maxTokens = _options.Value.Mcp.MaxTokensPerCall > 0 ? _options.Value.Mcp.MaxTokensPerCall : 4096;
        if (applyTokenBudget && estimatedTokens > maxTokens && text.Length > maxTokens * 4)
        {
            _logger.LogWarning("Resource '{Uri}' ({EstimatedTokens} tokens) exceeded max token budget ({MaxTokens}). Truncating result.", resourceUri, estimatedTokens, maxTokens);
            const string marker = "\n\n[TRUNCATED DUE TO MCP TOKEN BUDGET] Use the search and schema tools to narrow the result.";
            text = string.Concat(text.AsSpan(0, Math.Max(0, (maxTokens * 4) - marker.Length)), marker.AsSpan());
            estimatedTokens = Math.Max(1, text.Length / 4);
            truncated = true;
        }

        await RecordAuditEventAsync(name, sessionContext, "ALLOW", "Resource read under AI guardrails.", wasMasked, truncated, estimatedTokens, cancellationToken).ConfigureAwait(false);

        var verdict = wasMasked ? "masked" : "allow";
        activity?.SetTag(McpDiagnostics.GenAiGuardrailVerdictKey, verdict);
        activity?.SetTag(McpDiagnostics.GenAiGuardrailPiiDetectedKey, wasMasked);
        activity?.SetTag(McpDiagnostics.GenAiUsageOutputTokensKey, estimatedTokens);

        return new McpToolCallResult(IsSuccess: true, ContentJson: text, EstimatedTokens: estimatedTokens, IsMasked: wasMasked, TruncatedDueToBudget: truncated);
    }
}
