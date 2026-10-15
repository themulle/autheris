namespace Autheris.Application.Mcp.Services;

using System;
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

    private static readonly TimeSpan DefaultRegexTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly Regex EmailRegex = new(@"\b([A-Za-z0-9._%+-])[A-Za-z0-9._%+-]*@([A-Za-z0-9.-]+\.[A-Za-z]{2,})\b", RegexOptions.Compiled | RegexOptions.IgnoreCase, DefaultRegexTimeout);
    private static readonly Regex IbanRegex = new(@"\b[A-Z]{2}[0-9]{2}(?:[ ]?[0-9]{4}){3,7}(?:[ ]?[0-9]{1,4})?\b", RegexOptions.Compiled, DefaultRegexTimeout);
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
        TrinoSqlEngine.ISqlEngine? sqlEngine = null)
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
    }

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
            await RecordAuditEventAsync(request.ToolName, sessionContext, "DENY", $"Tool '{request.ToolName}' is not registered or allowed.", false, false, 0, cancellationToken).ConfigureAwait(false);
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
                return await McpAdminTools.ExecuteAdminToolAsync(tool.Name, request.ArgumentsJson ?? "{}", sessionContext, _accessPlanningService, cancellationToken).ConfigureAwait(false);
            }
        }

        if (McpDocTools.IsDocTool(tool.Name))
        {
            var rawDocJson = await McpDocTools.ExecuteDocToolAsync(tool.Name, request.ArgumentsJson ?? "{}", _docSearchService, _docStore, sessionContext, cancellationToken).ConfigureAwait(false);
            bool docWasMasked = false;
            string scrubbedDocJson = !_options.Value.IsMcpUnmaskedAllowed ? ScrubPiiAndSensitiveData(rawDocJson, out docWasMasked) : rawDocJson;
            bool isError = TryGetExecutorError(rawDocJson, out var docErr);
            await RecordAuditEventAsync(tool.Name, sessionContext, isError ? "DENY" : "ALLOW",
                isError ? $"Doc tool returned error ({docErr ?? "ERROR"})." : "Document MCP tool executed successfully under AI guardrails.",
                docWasMasked, false, Math.Max(1, scrubbedDocJson.Length / 4), cancellationToken).ConfigureAwait(false);
            return new McpToolCallResult(IsSuccess: !isError, ContentJson: scrubbedDocJson, ErrorMessage: isError ? scrubbedDocJson : null, IsMasked: docWasMasked);
        }

        var resolvedTables = string.Equals(tool.Name, McpDatasetTools.QueryGraphQl, StringComparison.OrdinalIgnoreCase)
            ? await ResolveGraphQlTablesAsync(request.ArgumentsJson, cancellationToken).ConfigureAwait(false)
            : ParseTablesFromTool(tool, request.ArgumentsJson, _sqlEngine, _options.Value.WebSql?.DefaultDataSourceName);

        if (string.Equals(tool.Name, McpDatasetTools.DescribeDataset, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(tool.Name, McpDatasetTools.SampleRows, StringComparison.OrdinalIgnoreCase))
        {
            if (!McpDatasetTools.TryGetStringArgument(request.ArgumentsJson, "dataset", out var dsArg) || string.IsNullOrWhiteSpace(dsArg))
            {
                return new McpToolCallResult(IsSuccess: false, ContentJson: "{}", ErrorMessage: $"Missing or invalid required argument 'dataset' for tool '{tool.Name}'.");
            }
        }

        if (resolvedTables == null && (tool.Name.StartsWith("query_", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(tool.TargetGraphQLOperation)))
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
                var userSidStr = !string.IsNullOrWhiteSpace(sessionContext.UserSid) ? sessionContext.UserSid : sessionContext.ServicePrincipalId;
                var groupSids = sessionContext.GroupSids is { Count: > 0 } ? sessionContext.GroupSids.Select(s => new Sid(s)).ToArray() : [];
                var clientIp = !string.IsNullOrWhiteSpace(sessionContext.ClientIp) && System.Net.IPAddress.TryParse(sessionContext.ClientIp, out var parsedIp)
                    ? parsedIp : System.Net.IPAddress.None;

                var secContext = new SecurityEvaluationContext(
                    UserSid: new Sid(userSidStr), GroupSids: groupSids, Tenant: new TenantId(sessionContext.TenantId),
                    TargetTable: effectiveTable, RequestedColumns: [], ClientIp: clientIp,
                    Timestamp: DateTimeOffset.UtcNow, PurposeId: "MCP_AI_AGENT_QUERY");

                var policyDecision = await _policyEnforcementService.EvaluatePolicyAsync(secContext, cancellationToken).ConfigureAwait(false);
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
        foreach (TableIdentifier? resolvedTable in needsFourEyesCheck ? resolvedTables ?? [] : [])
        {
            if (_tableMetadataRepository == null || resolvedTable == null ||
                resolvedTable.Value.Equals(McpDatasetTools.CatalogTable) ||
                resolvedTable.Value.Equals(McpSemanticTools.SemanticCatalogTable))
            {
                continue;
            }

            var meta = await _tableMetadataRepository.GetTableMetadataAsync(resolvedTable.Value, cancellationToken).ConfigureAwait(false);
            if (meta != null && !meta.Table.IsActive)
            {
                activity?.SetTag(McpDiagnostics.GenAiGuardrailVerdictKey, "deny");
                McpDiagnostics.RecordGuardrailVerdict("deny", tool.Name, false, false);
                _logger.LogWarning("Tool '{ToolName}' targets inactive table '{Table}'. Denying execution.", tool.Name, resolvedTable);
                await RecordAuditEventAsync(tool.Name, sessionContext, "DENY", $"Tool execution denied: target table {resolvedTable} is inactive.", false, false, 0, cancellationToken).ConfigureAwait(false);
                return new McpToolCallResult(IsSuccess: false, ContentJson: "{}", ErrorMessage: $"Access denied to tool '{tool.Name}': target dataset is not accessible.");
            }

            if (meta?.Table.RequiresFourEyes == true)
            {
                var roles = sessionContext.Roles ?? [];
                bool isAdmin = roles.Contains("ClusterAdmin", StringComparer.OrdinalIgnoreCase) || roles.Contains("GovernanceAdmin", StringComparer.OrdinalIgnoreCase);

                if (_consentRepository != null && !_options.Value.IsMcpAuthBypassed && !isAdmin)
                {
                    var principal = McpProtocolHandler.BuildPrincipalFromSession(sessionContext);
                    var visibleTables = await McpCatalogVisibility.VisibleTablesAsync([meta], principal, _consentRepository, cancellationToken).ConfigureAwait(false);
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
                        _logger.LogWarning("Four-Eyes approval denied or timed out for tool '{ToolName}' on table '{Table}'. Status: {Status}", tool.Name, resolvedTable, approvalResult.Ticket.Status);
                        await RecordAuditEventAsync(tool.Name, sessionContext, "DENY", $"Tool execution denied: Four-Eyes approval failed or timed out. Status: {approvalResult.Ticket.Status}. {approvalResult.Message}", false, false, 0, cancellationToken).ConfigureAwait(false);
                        return new McpToolCallResult(IsSuccess: false, ContentJson: "{}", ErrorMessage: $"Tool '{tool.Name}' requires Four-Eyes approval: {approvalResult.Message ?? "Approval denied or timed out."}");
                    }
                    _logger.LogInformation("Four-Eyes approval granted for tool '{ToolName}' on table '{Table}' by approver '{Approver}'.", tool.Name, resolvedTable, approvalResult.Ticket.ApproverSid);
                }
                else
                {
                    activity?.SetTag(McpDiagnostics.GenAiGuardrailVerdictKey, "deny");
                    McpDiagnostics.RecordGuardrailVerdict("deny", tool.Name, false, false);
                    _logger.LogWarning("Tool '{ToolName}' targets table '{Table}' which requires Four-Eyes approval. Denying automated AI agent execution.", tool.Name, resolvedTable);
                    await RecordAuditEventAsync(tool.Name, sessionContext, "DENY", $"Tool execution denied: table {resolvedTable} requires interactive Four-Eyes justification approval.", false, false, 0, cancellationToken).ConfigureAwait(false);
                    return new McpToolCallResult(IsSuccess: false, ContentJson: "{}", ErrorMessage: $"Tool '{tool.Name}' requires interactive Four-Eyes justification approval. Consent ticket must be generated.");
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

            var queries = await _goldenQueryService.GetGoldenQueriesAsync(domain, tableName, cancellationToken).ConfigureAwait(false);
            var isMcpAuthBypassed = _options.Value.IsMcpAuthBypassed;
            var roles = sessionContext.Roles ?? [];
            bool isAdmin = roles.Contains("ClusterAdmin", StringComparer.OrdinalIgnoreCase) || roles.Contains("GovernanceAdmin", StringComparer.OrdinalIgnoreCase);

            if (_consentRepository != null && !isMcpAuthBypassed && !isAdmin)
            {
                var principal = McpProtocolHandler.BuildPrincipalFromSession(sessionContext);
                var allTables = _tableMetadataRepository != null ? await _tableMetadataRepository.GetAllTablesAsync(cancellationToken).ConfigureAwait(false) : Array.Empty<TableMetadata>();
                var visibleTables = await McpCatalogVisibility.VisibleTablesAsync(allTables, principal, _consentRepository, cancellationToken).ConfigureAwait(false);
                var visibleKeys = visibleTables.Select(t => (Domain: t.Identifier.Domain.ToLowerInvariant(), Table: t.Identifier.TableName.ToLowerInvariant())).ToHashSet();
                queries = queries.Where(q => visibleKeys.Contains((q.Domain.ToLowerInvariant(), q.TableName.ToLowerInvariant()))).ToList();
            }
            else if (!isMcpAuthBypassed && !isAdmin && string.Equals(sessionContext.UserSid, "ANONYMOUS_MCP_CLIENT", StringComparison.OrdinalIgnoreCase))
            {
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

        int maxTokens = _options.Value.Mcp.MaxTokensPerCall > 0 ? _options.Value.Mcp.MaxTokensPerCall : 4096;
        int estimatedTokens = Math.Max(1, scrubbedJson.Length / 4);
        bool truncated = false;

        if (estimatedTokens > maxTokens)
        {
            _logger.LogWarning("Tool result for '{ToolName}' ({EstimatedTokens} tokens) exceeded max token budget ({MaxTokens}). Truncating result.", tool.Name, estimatedTokens, maxTokens);
            int maxChars = maxTokens * 4;
            if (scrubbedJson.Length > maxChars)
            {
                scrubbedJson = JsonSerializer.Serialize(new
                {
                    _meta = new
                    {
                        truncated = true, reason = "TOKEN_BUDGET_EXCEEDED", warning = "[TRUNCATED DUE TO MCP TOKEN BUDGET]",
                        maxTokens, note = "Result exceeded token budget. Use pagination (offset/limit) or filters to narrow the result."
                    }
                });
                estimatedTokens = Math.Max(1, scrubbedJson.Length / 4);
                truncated = true;
            }
        }

        await RecordAuditEventAsync(tool.Name, sessionContext, "ALLOW", "Tool executed successfully under AI guardrails.", wasMasked, truncated, estimatedTokens, cancellationToken).ConfigureAwait(false);

        var verdict = wasMasked ? "masked" : "allow";
        activity?.SetTag(McpDiagnostics.GenAiGuardrailVerdictKey, verdict);
        activity?.SetTag(McpDiagnostics.GenAiGuardrailPiiDetectedKey, wasMasked);
        activity?.SetTag(McpDiagnostics.GenAiUsageOutputTokensKey, estimatedTokens);
        McpDiagnostics.RecordTokenUsage(estimatedTokens, "output", tool.Name);
        McpDiagnostics.RecordGuardrailVerdict(verdict, tool.Name, wasMasked, false);

        return new McpToolCallResult(IsSuccess: true, ContentJson: scrubbedJson, EstimatedTokens: estimatedTokens, IsMasked: wasMasked, TruncatedDueToBudget: truncated);
    }
}
