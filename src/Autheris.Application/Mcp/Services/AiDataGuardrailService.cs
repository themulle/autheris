namespace Autheris.Application.Mcp.Services;

using Autheris.Application.Tenancy;
using System;
using Autheris.Application.Catalog.Services;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Docs.Interfaces;
using Autheris.Application.Events;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Interfaces;
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
/// Enterprise Guardrail engine enforcing zero-trust data protection, Casbin ABAC enforcement,
/// automated PII scrubbing, token budgeting, Four-Eyes justification gating, and SHA-256 tamper-evident
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
    private readonly IPolicyEnforcementService? _policyEnforcementService;
    private readonly ITableMetadataRepository? _tableMetadataRepository;
    private readonly IMcpSessionStore? _sessionStore;
    private readonly ISemanticPromptGuardrail _promptGuardrail;
    private readonly IGoldenQueryService? _goldenQueryService;
    private readonly IHitLStepUpApprovalService? _stepUpApprovalService;
    private readonly IGraphQlCatalogMap? _graphQlCatalogMap;
    private readonly IAccessPlanningService? _accessPlanningService;
    private readonly IDocSearchService? _docSearchService;
    private readonly IUniversalDocStore? _docStore;
    private readonly IGatewayEventBus? _eventBus;
    private readonly TrinoSqlEngine.ISqlEngine? _sqlEngine;
    private readonly IReadPathSwitches? _switches;
    private readonly Autheris.Application.Governance.Interfaces.IDatasetCatalogAdminService? _datasetCatalogAdmin;
    private readonly Autheris.Application.Procedures.Interfaces.IProcedureRegistry? _procedureRegistry;
    private readonly Autheris.Application.Dbt.Interfaces.ISemanticCatalogService? _semanticCatalog;
    private readonly Autheris.Application.Catalog.Interfaces.ICatalogVisibilityService? _catalogVisibility;

    private static readonly TimeSpan DefaultRegexTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly Regex EmailRegex = new(@"\b([A-Za-z0-9._%+-])[A-Za-z0-9._%+-]*@([A-Za-z0-9.-]+\.[A-Za-z]{2,})\b", RegexOptions.Compiled | RegexOptions.IgnoreCase, DefaultRegexTimeout);
    // MCP-10: ISO 13616 structure (letters allowed in the BBAN, any case); candidates are confirmed with the mod-97 check.
    private static readonly Regex IbanRegex = new(@"\b[A-Z]{2}[0-9]{2}(?:[ ]?[A-Z0-9]{4}){2,7}(?:[ ]?[A-Z0-9]{1,4})?\b", RegexOptions.Compiled | RegexOptions.IgnoreCase, DefaultRegexTimeout);
    private static readonly Regex GdprArt9Regex = new(@"""(healthCondition|diagnosis|medicalRecord|biometricData|geneticMarker|religiousAffiliation|politicalBelief)""\s*:\s*""([^""]+)""", RegexOptions.Compiled | RegexOptions.IgnoreCase, DefaultRegexTimeout);

    public AiDataGuardrailService(
        IMcpToolRegistry toolRegistry,
        IOptions<GatewayOptions> options,
        ILogger<AiDataGuardrailService> logger,
        IAuditLogRepository auditLogRepository,
        IMcpQueryExecutor? queryExecutor = null,
        IPolicyEnforcementService? policyEnforcementService = null,
        ITableMetadataRepository? tableMetadataRepository = null,
        IMcpSessionStore? sessionStore = null,
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
        Autheris.Application.Dbt.Interfaces.ISemanticCatalogService? semanticCatalog = null,
        Autheris.Application.Catalog.Interfaces.ICatalogVisibilityService? catalogVisibility = null)
    {
        _toolRegistry = toolRegistry ?? throw new ArgumentNullException(nameof(toolRegistry));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _auditLogRepository = auditLogRepository ?? throw new ArgumentNullException(nameof(auditLogRepository));
        _queryExecutor = queryExecutor;
        _policyEnforcementService = policyEnforcementService;
        _tableMetadataRepository = tableMetadataRepository;
        _sessionStore = sessionStore;
        _promptGuardrail = promptGuardrail ?? new SemanticPromptGuardrail();
        _goldenQueryService = goldenQueryService;
        _stepUpApprovalService = stepUpApprovalService;
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
        if (tool == null)
        {
            activity?.SetTag(McpDiagnostics.GenAiGuardrailVerdictKey, "deny");
            McpDiagnostics.RecordGuardrailVerdict("deny", request.ToolName, false, false);
            _logger.LogWarning("AI Agent attempted to call unauthorized or unknown MCP tool '{ToolName}'.", request.ToolName);
            // PLAN-SEC-115 C-115-4: the internal audit row names the switch behind the denial; the caller-visible result below is
            // byte-identical to the one for a name that never existed. Only names a switch really closed get a reason and a count.
            var disabledReason = _switches?.DisabledReason(request.ToolName);
            var denyDetails = $"Tool '{request.ToolName}' is not registered or allowed.";
            if (disabledReason is not null)
            {
                denyDetails = $"Tool '{request.ToolName}' is not registered or allowed. Tool disabled by configuration ({disabledReason}).";
                GatewayDiagnostics.DisabledPathRequestCounter.Add(1, new KeyValuePair<string, object?>("path", DisabledPathTag(disabledReason)));
            }

            await RecordAuditEventAsync(request.ToolName, sessionContext, "DENY", denyDetails, false, false, 0, cancellationToken).ConfigureAwait(false);
            return new McpToolCallResult(IsSuccess: false, ContentJson: "{}", ErrorMessage: $"Tool '{request.ToolName}' is not registered or allowed.");
        }

        var promptEvaluation = _promptGuardrail.EvaluatePrompt(request.ToolName, request.ArgumentsJson);
        if (!promptEvaluation.IsAllowed)
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

            return new McpToolCallResult(IsSuccess: false, ContentJson: "{}", ErrorMessage: $"Execution blocked: Prompt injection or jailbreak pattern detected ({promptEvaluation.AttackType}).");
        }

        if (_sessionStore != null)
        {
            var progressData = JsonSerializer.Serialize(new { jsonrpc = "2.0", method = "notifications/progress", @params = new { tool = tool.Name, status = "executing", sessionId = sessionContext.SessionId } });
            _ = _sessionStore.SendEventAsync(sessionContext.SessionId, "message", progressData);
        }

        if (McpAdminTools.IsAdminTool(tool.Name))
        {
            if (!McpAdminTools.CanUseAdminTools(sessionContext, _options.Value.IsMcpAuthBypassed))
            {
                activity?.SetTag(McpDiagnostics.GenAiGuardrailVerdictKey, "deny");
                McpDiagnostics.RecordGuardrailVerdict("deny", tool.Name, false, false);
                await RecordAuditEventAsync(tool.Name, sessionContext, "DENY", $"Tool execution denied: Admin tool '{tool.Name}' requires the ClusterAdmin role (and a read/write session).", false, false, 0, cancellationToken).ConfigureAwait(false);
                return new McpToolCallResult(IsSuccess: false, ContentJson: "{}", ErrorMessage: $"Access denied: Tool '{tool.Name}' requires the ClusterAdmin role (and a read/write session).");
            }

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
                return adminResult;
            }
        }

        if (McpDocTools.IsDocTool(tool.Name))
        {
            var rawDocJson = await McpDocTools.ExecuteDocToolAsync(tool.Name, request.ArgumentsJson ?? "{}", _docSearchService, _docStore, sessionContext, cancellationToken).ConfigureAwait(false);
            bool docWasMasked = false;
            string scrubbedDocJson = !_options.Value.IsMcpUnmaskedAllowed ? ScrubPiiAndSensitiveData(rawDocJson, out docWasMasked) : rawDocJson;
            bool isError = TryGetExecutorError(rawDocJson, out var docErr);
            if (!isError)
            {
                scrubbedDocJson = await InspectOutputAsync(tool.Name, scrubbedDocJson, sessionContext, cancellationToken, m => docWasMasked |= m).ConfigureAwait(false);
            }

            // MCP-08: doc tools run under the same per-call token budget as dataset tools.
            scrubbedDocJson = ApplyTokenBudget(tool.Name, scrubbedDocJson, out var docTokens, out var docTruncated);
            await RecordAuditEventAsync(tool.Name, sessionContext, isError ? "DENY" : "ALLOW",
                isError ? $"Doc tool returned error ({docErr ?? "ERROR"})." : "Document MCP tool executed successfully under AI guardrails.",
                docWasMasked, docTruncated, docTokens, cancellationToken).ConfigureAwait(false);
            return new McpToolCallResult(IsSuccess: !isError, ContentJson: scrubbedDocJson, ErrorMessage: isError ? scrubbedDocJson : null, EstimatedTokens: docTokens, IsMasked: docWasMasked, TruncatedDueToBudget: docTruncated);
        }

        string? sqlSyntaxError = null;
        IReadOnlyList<TableIdentifier>? resolvedTables;
        if (string.Equals(tool.Name, McpDatasetTools.QueryGraphQl, StringComparison.OrdinalIgnoreCase))
        {
            resolvedTables = await ResolveGraphQlTablesAsync(request.ArgumentsJson, cancellationToken).ConfigureAwait(false);
        }
        else if (string.Equals(tool.Name, McpDatasetTools.QuerySql, StringComparison.OrdinalIgnoreCase))
        {
            var sql = McpDatasetTools.TryGetStringArgument(request.ArgumentsJson, "query", out var s) ? s : null;
            var (tables, syntaxErr) = McpDataTargetResolver.TryResolveSqlTables(sql, _sqlEngine, _options.Value.WebSql?.DefaultDataSourceName);
            resolvedTables = tables;
            sqlSyntaxError = syntaxErr;
        }
        else
        {
            resolvedTables = ParseTablesFromTool(tool, request.ArgumentsJson, _sqlEngine, _options.Value.WebSql?.DefaultDataSourceName);
        }

        if (string.Equals(sqlSyntaxError, McpDataTargetResolver.ConstructNotSupportedMarker, StringComparison.Ordinal))
        {
            // SEC-277-04: identical text for every table state (existing, forbidden, unknown); fixed message, no request text.
            var constructMessage = $"{TrinoSqlEngine.Ast.Emit.GovernedSqlErrorCodes.ConstructNotSupported}: " +
                                   TrinoSqlEngine.Ast.Emit.GovernedSqlErrorCodes.FixedMessage(TrinoSqlEngine.Ast.Emit.GovernedSqlErrorCodes.ConstructNotSupported);
            activity?.SetTag(McpDiagnostics.GenAiGuardrailVerdictKey, "deny");
            McpDiagnostics.RecordGuardrailVerdict("deny", tool.Name, false, false);
            await RecordAuditEventAsync(tool.Name, sessionContext, "DENY", constructMessage, false, false, 0, cancellationToken).ConfigureAwait(false);
            return new McpToolCallResult(IsSuccess: false, ContentJson: "{}", ErrorMessage: constructMessage);
        }

        if (sqlSyntaxError != null)
        {
            activity?.SetTag(McpDiagnostics.GenAiGuardrailVerdictKey, "deny");
            McpDiagnostics.RecordGuardrailVerdict("deny", tool.Name, false, false);
            _logger.LogWarning("SQL syntax error in MCP tool '{ToolName}': target data table could not be resolved: {Error}", tool.Name, sqlSyntaxError);
            await RecordAuditEventAsync(tool.Name, sessionContext, "DENY", $"SQL_SYNTAX_ERROR: target data table could not be resolved: {sqlSyntaxError}", false, false, 0, cancellationToken).ConfigureAwait(false);
            return new McpToolCallResult(IsSuccess: false, ContentJson: "{}", ErrorMessage: $"SQL_SYNTAX_ERROR: target data table could not be resolved: {sqlSyntaxError}");
        }

        if (string.Equals(tool.Name, McpDatasetTools.DescribeDataset, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(tool.Name, McpDatasetTools.SampleRows, StringComparison.OrdinalIgnoreCase))
        {
            if (!McpDatasetTools.TryGetStringArgument(request.ArgumentsJson, "dataset", out var dsArg) || string.IsNullOrWhiteSpace(dsArg))
            {
                return new McpToolCallResult(IsSuccess: false, ContentJson: "{}", ErrorMessage: $"Missing or invalid required argument 'dataset' for tool '{tool.Name}'.");
            }
        }

        bool isSelfAccessTool = string.Equals(tool.Name, McpDatasetTools.DescribeMyAccess, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(tool.Name, McpDatasetTools.GetMyPermissions, StringComparison.OrdinalIgnoreCase);

        if (resolvedTables == null && !isSelfAccessTool &&
            (tool.Name.StartsWith("query_", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(tool.TargetGraphQLOperation) || McpDatasetTools.IsDatasetTool(tool.Name)))
        {
            activity?.SetTag(McpDiagnostics.GenAiGuardrailVerdictKey, "deny");
            McpDiagnostics.RecordGuardrailVerdict("deny", tool.Name, false, false);
            _logger.LogWarning("Zero-Trust Fail-Closed: Could not determine target table for MCP tool '{ToolName}'. Denying automated AI agent execution.", tool.Name);
            await RecordAuditEventAsync(tool.Name, sessionContext, "DENY", "Unresolvable target table for data access tool (fail-closed).", false, false, 0, cancellationToken).ConfigureAwait(false);
            return new McpToolCallResult(IsSuccess: false, ContentJson: "{}", ErrorMessage: $"Access denied to tool '{tool.Name}': target data table could not be resolved (fail-closed).");
        }

        IReadOnlyList<TableIdentifier> abacTables = resolvedTables ?? [new TableIdentifier("mcp", "tool", tool.Name.ToLowerInvariant())];
        foreach (var effectiveTable in abacTables)
        {
            bool isSchemaTool = _options.Value.IsMcpAuthBypassed &&
                (string.Equals(tool.Name, "query_data_catalog", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(tool.Name, "get_golden_queries", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(effectiveTable.Domain, "governance", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(effectiveTable.Domain, "catalog", StringComparison.OrdinalIgnoreCase));

            if (_policyEnforcementService != null && _options.Value.Casbin.Enabled && !_options.Value.IsMcpAuthBypassed && !isSchemaTool)
            {
                var userSidStr = CallerSidOf(sessionContext);
                var policyDecision = await _policyEnforcementService.EvaluatePolicyAsync(BuildSecurityContext(sessionContext, effectiveTable), cancellationToken).ConfigureAwait(false);
                if (!policyDecision.IsAllowed)
                {
                    activity?.SetTag(McpDiagnostics.GenAiGuardrailVerdictKey, "deny");
                    McpDiagnostics.RecordGuardrailVerdict("deny", tool.Name, false, false);
                    _logger.LogWarning("Casbin ABAC policy denied AI Agent '{Principal}' tool call '{ToolName}' (target: '{TargetTable}') in tenant '{TenantId}'. Reasons: {Reasons}",
                        sessionContext.ServicePrincipalId, tool.Name, effectiveTable, sessionContext.TenantId, string.Join("; ", policyDecision.DeniedReasons));
                    await RecordAuditEventAsync(tool.Name, sessionContext, "DENY", $"Access denied by Casbin ABAC policy: {string.Join("; ", policyDecision.DeniedReasons)}", false, false, 0, cancellationToken).ConfigureAwait(false);

                    if (_eventBus != null)
                    {
                        await _eventBus.PublishAsync(new PolicyDeniedEvent(
                            EventId: Guid.NewGuid().ToString("N"), SubjectSid: userSidStr, Action: tool.Name,
                            Resource: effectiveTable.ToString(), Reason: PolicyDeniedEvent.DefaultReasonCode,
                            Timestamp: DateTimeOffset.UtcNow, TenantId: sessionContext.TenantId,
                            ReasonCode: PolicyDeniedEvent.DefaultReasonCode), cancellationToken).ConfigureAwait(false);
                    }

                    return new McpToolCallResult(IsSuccess: false, ContentJson: "{}", ErrorMessage: $"Access denied to tool '{tool.Name}' by ABAC security policy.");
                }
            }
        }

        var needsFourEyesCheck = ToolReadsDatasetRows(tool);

        // call_procedure and query_metrics: the ABAC object above is a pseudo table (procedure.dbo.<name>, the semantic catalog) that
        // never carries the four-eyes flag. The gate below runs on the real tables behind the call instead; unresolvable is fail-closed.
        IReadOnlyList<TableIdentifier>? fourEyesTables = resolvedTables;
        var gatesUnderlyingTables = needsFourEyesCheck && _tableMetadataRepository != null && ReadsThroughUnderlyingTables(tool);
        if (gatesUnderlyingTables)
        {
            fourEyesTables = await ResolveUnderlyingTablesAsync(tool, request.ArgumentsJson, sessionContext, cancellationToken).ConfigureAwait(false);
            if (fourEyesTables == null)
            {
                activity?.SetTag(McpDiagnostics.GenAiGuardrailVerdictKey, "deny");
                McpDiagnostics.RecordGuardrailVerdict("deny", tool.Name, false, false);
                _logger.LogWarning("Zero-Trust Fail-Closed: Could not resolve the tables behind MCP tool '{ToolName}'. Denying execution.", tool.Name);
                await RecordAuditEventAsync(tool.Name, sessionContext, "DENY", "Tool execution denied: the tables behind the call could not be resolved (fail-closed).", false, false, 0, cancellationToken).ConfigureAwait(false);
                return new McpToolCallResult(IsSuccess: false, ContentJson: "{}", ErrorMessage: $"Access denied to tool '{tool.Name}': target dataset is not accessible.");
            }
        }

        foreach (TableIdentifier? resolvedTable in needsFourEyesCheck ? fourEyesTables ?? [] : [])
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
                return new McpToolCallResult(IsSuccess: false, ContentJson: "{}", ErrorMessage: $"Access denied to tool '{tool.Name}': target dataset is not accessible.");
            }

            if (meta?.Table.RequiresFourEyes == true)
            {
                bool isAdmin = IsUnrestrictedCatalogCaller(sessionContext);

                if (_consentRepository != null && !_options.Value.IsMcpAuthBypassed && !isAdmin)
                {
                    var principal = McpProtocolHandler.BuildPrincipalFromSession(sessionContext);
                    var visibleTables = await McpCatalogVisibility.VisibleTablesAsync([meta], principal, _consentRepository, _options.Value, _catalogVisibility, cancellationToken).ConfigureAwait(false);
                    bool isVisible = visibleTables.Any(t =>
                        t.Identifier.Equals(resolvedTable.Value) ||
                        (string.Equals(t.Identifier.Domain, resolvedTable.Value.Domain, StringComparison.OrdinalIgnoreCase) &&
                         string.Equals(t.Identifier.TableName, resolvedTable.Value.TableName, StringComparison.OrdinalIgnoreCase)));

                    if (!isVisible)
                    {
                        activity?.SetTag(McpDiagnostics.GenAiGuardrailVerdictKey, "deny");
                        McpDiagnostics.RecordGuardrailVerdict("deny", tool.Name, false, false);
                        _logger.LogWarning("Tool '{ToolName}' targets dataset '{Table}' which is not visible or consented to caller '{Caller}'. Denying execution before Four-Eyes gate.", tool.Name, resolvedTable, sessionContext.ServicePrincipalId);
                        await RecordAuditEventAsync(tool.Name, sessionContext, "DENY", $"Tool execution denied: target dataset {resolvedTable} is not visible or consented to caller.", false, false, 0, cancellationToken).ConfigureAwait(false);
                        return new McpToolCallResult(IsSuccess: false, ContentJson: "{}", ErrorMessage: $"Access denied to tool '{tool.Name}': target dataset is not accessible.");
                    }
                }

                if (_stepUpApprovalService != null && _options.Value.HitLStepUp.Enabled)
                {
                    var requesterSid = sessionContext.UserSid ?? sessionContext.ServicePrincipalId;
                    _logger.LogInformation("Tool '{ToolName}' targets table '{Table}' which requires Four-Eyes approval. Requesting step-up approval for requester '{Requester}'.", tool.Name, resolvedTable, requesterSid);
                    var approvalResult = await _stepUpApprovalService.RequestStepUpApprovalAsync(tool.Name, sessionContext.TenantId, requesterSid, resolvedTable.Value, request.ArgumentsJson, cancellationToken).ConfigureAwait(false);
                    if (!approvalResult.IsApproved)
                    {
                        activity?.SetTag(McpDiagnostics.GenAiGuardrailVerdictKey, "deny");
                        McpDiagnostics.RecordGuardrailVerdict("deny", tool.Name, false, false);
                        _logger.LogWarning("Four-Eyes approval denied or timed out for tool '{ToolName}' on table '{Table}'. Status: {Status}", tool.Name, resolvedTable, approvalResult.Ticket?.Status.ToString() ?? "denied/timed out");
                        await RecordAuditEventAsync(tool.Name, sessionContext, "DENY", $"Tool execution denied: Four-Eyes approval failed or timed out. Status: {approvalResult.Ticket?.Status.ToString() ?? "denied/timed out"}. {approvalResult.Message}", false, false, 0, cancellationToken).ConfigureAwait(false);
                        return new McpToolCallResult(IsSuccess: false, ContentJson: "{}", ErrorMessage: $"Tool '{tool.Name}' requires Four-Eyes approval: status is {approvalResult.Ticket?.Status.ToString() ?? "denied/timed out"}. {approvalResult.Message ?? "Approval was not granted within timeout period. Re-request with valid justification."}");
                    }
                    _logger.LogInformation("Four-Eyes approval granted for tool '{ToolName}' on table '{Table}' by approver '{Approver}'.", tool.Name, resolvedTable, approvalResult.Ticket.ApproverSid);
                }
                else
                {
                    activity?.SetTag(McpDiagnostics.GenAiGuardrailVerdictKey, "deny");
                    McpDiagnostics.RecordGuardrailVerdict("deny", tool.Name, false, false);
                    _logger.LogWarning("Tool '{ToolName}' targets table '{Table}' which requires Four-Eyes approval. Denying automated AI agent execution.", tool.Name, resolvedTable);
                    await RecordAuditEventAsync(tool.Name, sessionContext, "DENY", $"Tool execution denied: table {resolvedTable} requires interactive Four-Eyes justification approval.", false, false, 0, cancellationToken).ConfigureAwait(false);
                    return new McpToolCallResult(IsSuccess: false, ContentJson: "{}", ErrorMessage: $"Tool '{tool.Name}' requires interactive Four-Eyes justification approval for table '{resolvedTable}'. Consent ticket must be generated.");
                }
            }
        }

        string rawDataJson;
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

            if (isMcpAuthBypassed || isAdmin)
            {
                // MCP-15: a null predicate still keeps the per-request cap.
                queries = await _goldenQueryService.GetGoldenQueriesAsync(domain, tableName, null, cancellationToken).ConfigureAwait(false);
            }
            else if (_consentRepository != null)
            {
                // MCP-15: visibility is evaluated first and applied before the cap; any failure yields an empty list, never "all".
                HashSet<(string Domain, string Table)>? visibleKeys = null;
                try
                {
                    var principal = McpProtocolHandler.BuildPrincipalFromSession(sessionContext);
                    var allTables = _tableMetadataRepository != null ? await _tableMetadataRepository.GetAllTablesAsync(cancellationToken).ConfigureAwait(false) : Array.Empty<TableMetadata>();
                    var visibleTables = await McpCatalogVisibility.VisibleTablesAsync(allTables, principal, _consentRepository, _options.Value, _catalogVisibility, cancellationToken).ConfigureAwait(false);
                    visibleKeys = visibleTables?.Select(t => (Domain: t.Identifier.Domain.ToLowerInvariant(), Table: t.Identifier.TableName.ToLowerInvariant())).ToHashSet();
                }
                catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
                {
                    visibleKeys = null;
                }

                queries = visibleKeys is null
                    ? Array.Empty<GoldenQuery>()
                    : await _goldenQueryService.GetGoldenQueriesAsync(domain, tableName, q => visibleKeys.Contains((q.Domain.ToLowerInvariant(), q.TableName.ToLowerInvariant())), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // MCP-15: fail closed - without a consent repository visibility cannot be evaluated (this also covers the anonymous client).
                queries = Array.Empty<GoldenQuery>();
            }

            rawDataJson = JsonSerializer.Serialize(new { tenantId = sessionContext.TenantId, queries });
        }
        else if (_queryExecutor != null)
        {
            rawDataJson = await _queryExecutor.ExecuteOperationAsync(tool, argsJson, sessionContext, cancellationToken).ConfigureAwait(false);
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
            return new McpToolCallResult(IsSuccess: false, ContentJson: scrubbedJson, ErrorMessage: scrubbedJson, IsMasked: wasMasked);
        }

        scrubbedJson = await InspectOutputAsync(tool.Name, scrubbedJson, sessionContext, cancellationToken, m => wasMasked |= m).ConfigureAwait(false);
        scrubbedJson = ApplyTokenBudget(tool.Name, scrubbedJson, out var estimatedTokens, out var truncated);

        await RecordAuditEventAsync(tool.Name, sessionContext, "ALLOW", "Tool executed successfully under AI guardrails.", wasMasked, truncated, estimatedTokens, cancellationToken).ConfigureAwait(false);

        var verdict = wasMasked ? "masked" : "allow";
        activity?.SetTag(McpDiagnostics.GenAiGuardrailVerdictKey, verdict);
        activity?.SetTag(McpDiagnostics.GenAiGuardrailPiiDetectedKey, wasMasked);
        activity?.SetTag(McpDiagnostics.GenAiUsageOutputTokensKey, estimatedTokens);
        McpDiagnostics.RecordTokenUsage(estimatedTokens, "output", tool.Name);
        McpDiagnostics.RecordGuardrailVerdict(verdict, tool.Name, wasMasked, false);

        return new McpToolCallResult(IsSuccess: true, ContentJson: scrubbedJson, EstimatedTokens: estimatedTokens, IsMasked: wasMasked, TruncatedDueToBudget: truncated);
    }

    /// <summary>Audit and metric label of a resource read (the audit row names the URI where a tool call names the tool).</summary>
    internal static string ResourceAuditName(string resourceUri) => "resources/read " + resourceUri;

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
        var notFound = new McpToolCallResult(IsSuccess: false, ContentJson: "{}", ErrorMessage: $"Resource '{resourceUri}' not found.");

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

        // Same Casbin ABAC request as the equivalent tool call, on every table the resource discloses.
        if (_policyEnforcementService != null && _options.Value.Casbin.Enabled && !_options.Value.IsMcpAuthBypassed)
        {
            foreach (var table in targetTables)
            {
                var decision = await _policyEnforcementService.EvaluatePolicyAsync(BuildSecurityContext(sessionContext, table), cancellationToken).ConfigureAwait(false);
                if (!decision.IsAllowed)
                {
                    activity?.SetTag(McpDiagnostics.GenAiGuardrailVerdictKey, "deny");
                    _logger.LogWarning("Casbin ABAC policy denied AI Agent '{Principal}' resource read '{Uri}' (target: '{TargetTable}') in tenant '{TenantId}'.",
                        sessionContext.ServicePrincipalId, resourceUri, table, sessionContext.TenantId);
                    await RecordAuditEventAsync(name, sessionContext, "DENY", $"Access denied by Casbin ABAC policy: {string.Join("; ", decision.DeniedReasons)}", false, false, 0, cancellationToken).ConfigureAwait(false);

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
