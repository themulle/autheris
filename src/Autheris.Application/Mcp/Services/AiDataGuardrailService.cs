namespace Autheris.Application.Mcp.Services;

using System;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Mcp.Diagnostics;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Domain.Common;
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
public sealed class AiDataGuardrailService : IAiDataGuardrailService
{
    private readonly IMcpToolRegistry _toolRegistry;
    private readonly IOptions<GatewayOptions> _options;
    private readonly ILogger<AiDataGuardrailService> _logger;
    private readonly IMcpQueryExecutor? _queryExecutor;
    private readonly IAuditLogRepository? _auditLogRepository;
    private readonly IPolicyEnforcementService? _policyEnforcementService;
    private readonly ITableMetadataRepository? _tableMetadataRepository;
    private readonly IMcpSessionStore? _sessionStore;
    private readonly ISemanticPromptGuardrail _promptGuardrail;
    private readonly IGoldenQueryService? _goldenQueryService;
    private readonly IHitLStepUpApprovalService? _stepUpApprovalService;
    private readonly IGraphQlCatalogMap? _graphQlCatalogMap;

    private static readonly TimeSpan DefaultRegexTimeout = TimeSpan.FromMilliseconds(250);

    // High-performance compiled regexes for automated PII detection with ReDoS timeout protection
    private static readonly Regex EmailRegex = new(
        @"\b([A-Za-z0-9._%+-])[A-Za-z0-9._%+-]*@([A-Za-z0-9.-]+\.[A-Za-z]{2,})\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        DefaultRegexTimeout);

    private static readonly Regex IbanRegex = new(
        @"\b[A-Z]{2}[0-9]{2}(?:[ ]?[0-9]{4}){3,7}(?:[ ]?[0-9]{1,4})?\b",
        RegexOptions.Compiled,
        DefaultRegexTimeout);

    private static readonly Regex GdprArt9Regex = new(
        @"""(healthCondition|diagnosis|medicalRecord|biometricData|geneticMarker|religiousAffiliation|politicalBelief)""\s*:\s*""([^""]+)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        DefaultRegexTimeout);

    public AiDataGuardrailService(
        IMcpToolRegistry toolRegistry,
        IOptions<GatewayOptions> options,
        ILogger<AiDataGuardrailService> logger,
        IMcpQueryExecutor? queryExecutor = null,
        IAuditLogRepository? auditLogRepository = null,
        IPolicyEnforcementService? policyEnforcementService = null,
        ITableMetadataRepository? tableMetadataRepository = null,
        IMcpSessionStore? sessionStore = null,
        ISemanticPromptGuardrail? promptGuardrail = null,
        IGoldenQueryService? goldenQueryService = null,
        IHitLStepUpApprovalService? stepUpApprovalService = null,
        IGraphQlCatalogMap? graphQlCatalogMap = null)
    {
        _toolRegistry = toolRegistry ?? throw new ArgumentNullException(nameof(toolRegistry));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _queryExecutor = queryExecutor;
        _auditLogRepository = auditLogRepository;
        _policyEnforcementService = policyEnforcementService;
        _tableMetadataRepository = tableMetadataRepository;
        _sessionStore = sessionStore;
        _promptGuardrail = promptGuardrail ?? new SemanticPromptGuardrail();
        _goldenQueryService = goldenQueryService;
        _stepUpApprovalService = stepUpApprovalService;
        _graphQlCatalogMap = graphQlCatalogMap;
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
            await RecordAuditEventAsync(
                request.ToolName,
                sessionContext,
                decision: "DENY",
                details: $"Tool '{request.ToolName}' is not registered or allowed.",
                isMasked: false,
                truncated: false,
                estimatedTokens: 0,
                cancellationToken).ConfigureAwait(false);

            return new McpToolCallResult(
                IsSuccess: false,
                ContentJson: "{}",
                ErrorMessage: $"Tool '{request.ToolName}' is not registered or allowed."
            );
        }

        // Semantic Prompt Injection & Jailbreak Guardrail Check
        var promptEvaluation = _promptGuardrail.EvaluatePrompt(request.ToolName, request.ArgumentsJson);
        if (!promptEvaluation.IsAllowed)
        {
            activity?.SetTag(McpDiagnostics.GenAiGuardrailVerdictKey, "deny");
            McpDiagnostics.RecordGuardrailVerdict("deny", request.ToolName, false, false);

            _logger.LogWarning("Semantic prompt injection or jailbreak detected for tool '{ToolName}': {Reason}",
                request.ToolName, promptEvaluation.Reason);

            await RecordAuditEventAsync(
                request.ToolName,
                sessionContext,
                decision: "DENY",
                details: $"Execution blocked by prompt injection guardrail ({promptEvaluation.AttackType}): {promptEvaluation.Reason}",
                isMasked: false,
                truncated: false,
                estimatedTokens: 0,
                cancellationToken).ConfigureAwait(false);

            return new McpToolCallResult(
                IsSuccess: false,
                ContentJson: "{}",
                ErrorMessage: $"Execution blocked: Prompt injection or jailbreak pattern detected ({promptEvaluation.AttackType})."
            );
        }

        // 1. Notify client via SSE progress notification if stream is open

        if (_sessionStore != null)
        {
            var progressData = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                method = "notifications/progress",
                @params = new
                {
                    tool = tool.Name,
                    status = "executing",
                    sessionId = sessionContext.SessionId
                }
            });
            _ = _sessionStore.SendEventAsync(sessionContext.SessionId, "message", progressData);
        }

        // Resolve real TargetTable for ABAC policy and Four-Eyes checks
        var resolvedTables = string.Equals(tool.Name, McpDatasetTools.QueryGraphQl, StringComparison.OrdinalIgnoreCase)
            ? await ResolveGraphQlTablesAsync(request.ArgumentsJson, cancellationToken).ConfigureAwait(false)
            : ParseTablesFromTool(tool, request.ArgumentsJson);

        // Security Hardening: For data access tools, target table must be resolvable.
        // If unresolvable, fail-closed to prevent bypassing Casbin ABAC and Four-Eyes gates.
        if (resolvedTables == null && (tool.Name.StartsWith("query_", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(tool.TargetGraphQLOperation)))
        {
            activity?.SetTag(McpDiagnostics.GenAiGuardrailVerdictKey, "deny");
            McpDiagnostics.RecordGuardrailVerdict("deny", tool.Name, false, false);

            _logger.LogWarning("Zero-Trust Fail-Closed: Could not determine target table for MCP tool '{ToolName}'. Denying automated AI agent execution.", tool.Name);

            await RecordAuditEventAsync(
                tool.Name,
                sessionContext,
                decision: "DENY",
                details: "Unresolvable target table for data access tool (fail-closed).",
                isMasked: false,
                truncated: false,
                estimatedTokens: 0,
                cancellationToken).ConfigureAwait(false);

            return new McpToolCallResult(
                IsSuccess: false,
                ContentJson: "{}",
                ErrorMessage: $"Access denied to tool '{tool.Name}': target data table could not be resolved (fail-closed)."
            );
        }

        // 2. Pre-Execution Policy Check: Casbin ABAC Enforcement
        // SEC A-4: ABAC and four-eyes run on every table derived from the executed GraphQL document.
        IReadOnlyList<TableIdentifier> abacTables = resolvedTables ?? [new TableIdentifier("mcp", "tool", tool.Name.ToLowerInvariant())];
        foreach (var effectiveTable in abacTables)
        {
            // SEC H-02: OpenSchema no longer exempts MCP schema tools from ABAC; only the explicit MCP auth bypass does.
            bool isSchemaTool = _options.Value.IsMcpAuthBypassed &&
                (string.Equals(tool.Name, "query_data_catalog", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(tool.Name, "get_golden_queries", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(effectiveTable.Domain, "governance", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(effectiveTable.Domain, "catalog", StringComparison.OrdinalIgnoreCase));

            if (_policyEnforcementService != null && _options.Value.Casbin.Enabled && !_options.Value.IsMcpAuthBypassed && !isSchemaTool)
            {

                var userSidStr = !string.IsNullOrWhiteSpace(sessionContext.UserSid)
                    ? sessionContext.UserSid
                    : sessionContext.ServicePrincipalId;
                var groupSids = sessionContext.GroupSids != null && sessionContext.GroupSids.Count > 0
                    ? sessionContext.GroupSids.Select(s => new Sid(s)).ToArray()
                    : [];

                // SEC H-4: Fail closed to IPAddress.None if unresolvable
                var clientIp = System.Net.IPAddress.None;
                if (!string.IsNullOrWhiteSpace(sessionContext.ClientIp) && System.Net.IPAddress.TryParse(sessionContext.ClientIp, out var parsedIp))
                {
                    clientIp = parsedIp;
                }

                var secContext = new SecurityEvaluationContext(
                    UserSid: new Sid(userSidStr),
                    GroupSids: groupSids,
                    Tenant: new TenantId(sessionContext.TenantId),
                    TargetTable: effectiveTable,
                    RequestedColumns: [],
                    ClientIp: clientIp,
                    Timestamp: DateTimeOffset.UtcNow,
                    PurposeId: "MCP_AI_AGENT_QUERY"
                );

                var policyDecision = await _policyEnforcementService.EvaluatePolicyAsync(secContext, cancellationToken).ConfigureAwait(false);
                if (!policyDecision.IsAllowed)
                {
                    activity?.SetTag(McpDiagnostics.GenAiGuardrailVerdictKey, "deny");
                    McpDiagnostics.RecordGuardrailVerdict("deny", tool.Name, false, false);

                    _logger.LogWarning("Casbin ABAC policy denied AI Agent '{Principal}' tool call '{ToolName}' (target: '{TargetTable}') in tenant '{TenantId}'. Reasons: {Reasons}",
                        sessionContext.ServicePrincipalId, tool.Name, effectiveTable, sessionContext.TenantId, string.Join("; ", policyDecision.DeniedReasons));

                    await RecordAuditEventAsync(
                        tool.Name,
                        sessionContext,
                        decision: "DENY",
                        details: $"Access denied by Casbin ABAC policy: {string.Join("; ", policyDecision.DeniedReasons)}",
                        isMasked: false,
                        truncated: false,
                        estimatedTokens: 0,
                        cancellationToken).ConfigureAwait(false);

                    return new McpToolCallResult(
                        IsSuccess: false,
                        ContentJson: "{}",
                        ErrorMessage: $"Access denied to tool '{tool.Name}' by ABAC security policy."
                    );
                }
            }
        }

        // 3. Four-Eyes Justification Gate (dataset tools that return no rows, such as describe_dataset, need no approval)
        var needsFourEyesCheck = !McpDatasetTools.IsDatasetTool(tool.Name) || McpDatasetTools.ReadsRows(tool.Name);
        foreach (TableIdentifier? resolvedTable in needsFourEyesCheck ? resolvedTables ?? [] : [])
        {
            if (_tableMetadataRepository == null)
            {
                break;
            }

            var meta = await _tableMetadataRepository.GetTableMetadataAsync(resolvedTable.Value, cancellationToken).ConfigureAwait(false);
            if (meta?.Table.RequiresFourEyes == true)
            {
                if (_stepUpApprovalService != null && _options.Value.HitLStepUp.Enabled)
                {
                    var requesterSid = sessionContext.UserSid ?? sessionContext.ServicePrincipalId;
                    _logger.LogInformation("Tool '{ToolName}' targets table '{Table}' which requires Four-Eyes approval. Requesting step-up approval for requester '{Requester}'.",
                        tool.Name, resolvedTable, requesterSid);

                    var approvalResult = await _stepUpApprovalService.RequestStepUpApprovalAsync(
                        tool.Name,
                        sessionContext.TenantId,
                        requesterSid,
                        resolvedTable.Value,
                        request.ArgumentsJson,
                        cancellationToken).ConfigureAwait(false);

                    if (!approvalResult.IsApproved)
                    {
                        activity?.SetTag(McpDiagnostics.GenAiGuardrailVerdictKey, "deny");
                        McpDiagnostics.RecordGuardrailVerdict("deny", tool.Name, false, false);

                        _logger.LogWarning("Four-Eyes approval denied or timed out for tool '{ToolName}' on table '{Table}'. Status: {Status}",
                            tool.Name, resolvedTable, approvalResult.Ticket.Status);

                        await RecordAuditEventAsync(
                            tool.Name,
                            sessionContext,
                            decision: "DENY",
                            details: $"Tool execution denied: Four-Eyes approval failed or timed out. Status: {approvalResult.Ticket.Status}. {approvalResult.Message}",
                            isMasked: false,
                            truncated: false,
                            estimatedTokens: 0,
                            cancellationToken).ConfigureAwait(false);

                        return new McpToolCallResult(
                            IsSuccess: false,
                            ContentJson: "{}",
                            ErrorMessage: $"Tool '{tool.Name}' requires Four-Eyes approval: {approvalResult.Message ?? "Approval denied or timed out."}"
                        );
                    }

                    _logger.LogInformation("Four-Eyes approval granted for tool '{ToolName}' on table '{Table}' by approver '{Approver}'.",
                        tool.Name, resolvedTable, approvalResult.Ticket.ApproverSid);
                }
                else
                {
                    activity?.SetTag(McpDiagnostics.GenAiGuardrailVerdictKey, "deny");
                    McpDiagnostics.RecordGuardrailVerdict("deny", tool.Name, false, false);

                    _logger.LogWarning("Tool '{ToolName}' targets table '{Table}' which requires Four-Eyes approval. Denying automated AI agent execution.",
                        tool.Name, resolvedTable);

                    await RecordAuditEventAsync(
                        tool.Name,
                        sessionContext,
                        decision: "DENY",
                        details: $"Tool execution denied: table {resolvedTable} requires interactive Four-Eyes justification approval.",
                        isMasked: false,
                        truncated: false,
                        estimatedTokens: 0,
                        cancellationToken).ConfigureAwait(false);

                    return new McpToolCallResult(
                        IsSuccess: false,
                        ContentJson: "{}",
                        ErrorMessage: $"Tool '{tool.Name}' requires interactive Four-Eyes justification approval. Consent ticket must be generated."
                    );
                }
            }
        }

        // 4. Execution Bridge: Execute operation via IMcpQueryExecutor or test fallback
        string rawDataJson;
        var argsJson = request.ArgumentsJson ?? "{}";
        if (tool.Name.Equals("get_golden_queries", StringComparison.OrdinalIgnoreCase) && _goldenQueryService != null)
        {
            string? domain = null;
            string? tableName = null;
            try
            {
                using var argDoc = JsonDocument.Parse(argsJson);
                if (argDoc.RootElement.TryGetProperty("domain", out var dProp) && dProp.ValueKind == JsonValueKind.String)
                {
                    domain = dProp.GetString();
                }
                if (argDoc.RootElement.TryGetProperty("tableName", out var tProp) && tProp.ValueKind == JsonValueKind.String)
                {
                    tableName = tProp.GetString();
                }
            }
            catch (JsonException)
            {
            }

            var queries = await _goldenQueryService.GetGoldenQueriesAsync(domain, tableName, cancellationToken).ConfigureAwait(false);
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

        // 5. Automated PII & GDPR Art. 9 Scrubbing
        bool shouldMask = !_options.Value.IsMcpUnmaskedAllowed;
        bool wasMasked = false;
        string scrubbedJson = rawDataJson;

        if (shouldMask)
        {
            scrubbedJson = ScrubPiiAndSensitiveData(rawDataJson, out wasMasked);
        }
        else
        {
            _logger.LogWarning("SECURITY ALERT [WARN]: AI tool execution for tool '{ToolName}' is running unmasked (warn_allow_unmasked_ai_access is ACTIVE).", tool.Name);
        }

        // SEC M-17: Structured executor error results (deny / execution failure / not available) are reported as
        // tool errors (MCP isError:true) and are not audited as ALLOW. GAP07: the audit vocabulary is ALLOW/DENY
        // (PolicySimulationService treats every non-DENY value as ALLOW), so errors are recorded as DENY with the code in the details.
        if (TryGetExecutorError(rawDataJson, out var errorCode))
        {
            activity?.SetTag(McpDiagnostics.GenAiGuardrailVerdictKey, "deny");
            McpDiagnostics.RecordGuardrailVerdict("deny", tool.Name, wasMasked, false);

            await RecordAuditEventAsync(
                tool.Name,
                sessionContext,
                decision: "DENY",
                details: $"Tool execution returned an error result ({errorCode ?? "UNKNOWN"}).",
                isMasked: wasMasked,
                truncated: false,
                estimatedTokens: 0,
                cancellationToken).ConfigureAwait(false);

            return new McpToolCallResult(
                IsSuccess: false,
                ContentJson: scrubbedJson,
                ErrorMessage: scrubbedJson,
                IsMasked: wasMasked);
        }

        // 6. Token-Budgeting & Context Window Safeguards
        int maxTokens = _options.Value.Mcp.MaxTokensPerCall > 0 ? _options.Value.Mcp.MaxTokensPerCall : 4096;
        int estimatedTokens = Math.Max(1, scrubbedJson.Length / 4);
        bool truncated = false;

        if (estimatedTokens > maxTokens)
        {
            _logger.LogWarning("Tool result for '{ToolName}' ({EstimatedTokens} tokens) exceeded max token budget ({MaxTokens}). Truncating result.",
                tool.Name, estimatedTokens, maxTokens);

            int maxChars = maxTokens * 4;
            if (scrubbedJson.Length > maxChars)
            {
                scrubbedJson = scrubbedJson[..maxChars] + " ... [TRUNCATED DUE TO MCP TOKEN BUDGET]";
                estimatedTokens = maxTokens;
                truncated = true;
            }
        }

        // 7. SHA-256 Tamper-Evident Audit Logging
        await RecordAuditEventAsync(
            tool.Name,
            sessionContext,
            decision: "ALLOW",
            details: "Tool executed successfully under AI guardrails.",
            isMasked: wasMasked,
            truncated: truncated,
            estimatedTokens: estimatedTokens,
            cancellationToken).ConfigureAwait(false);

        // 8. OpenTelemetry GenAI Semantic Conventions & Metrics
        var verdict = wasMasked ? "masked" : "allow";
        activity?.SetTag(McpDiagnostics.GenAiGuardrailVerdictKey, verdict);
        activity?.SetTag(McpDiagnostics.GenAiGuardrailPiiDetectedKey, wasMasked);
        activity?.SetTag(McpDiagnostics.GenAiUsageOutputTokensKey, estimatedTokens);
        McpDiagnostics.RecordTokenUsage(estimatedTokens, "output", tool.Name);
        McpDiagnostics.RecordGuardrailVerdict(verdict, tool.Name, wasMasked, false);

        return new McpToolCallResult(
            IsSuccess: true,
            ContentJson: scrubbedJson,
            EstimatedTokens: estimatedTokens,
            IsMasked: wasMasked,
            TruncatedDueToBudget: truncated
        );
    }

    private async Task RecordAuditEventAsync(
        string toolName,
        McpSessionContext sessionContext,
        string decision,
        string details,
        bool isMasked,
        bool truncated,
        int estimatedTokens,
        CancellationToken ct)
    {
        if (_auditLogRepository == null) return;

        try
        {
            var actorSid = !string.IsNullOrWhiteSpace(sessionContext.UserSid)
                ? new Sid(sessionContext.UserSid)
                : new Sid(sessionContext.ServicePrincipalId);
            var tenantId = TenantId.TryParse(sessionContext.TenantId, out var tid) ? tid : TenantId.LegacySingleTenant;

            var entry = new AuditLogEntry
            {
                TenantId = tenantId,
                EventType = "MCP_TOOL_EXECUTION",
                ActorSid = actorSid,
                TargetTable = toolName,
                Decision = decision,
                TraceId = sessionContext.SessionId,
                DetailsJson = JsonSerializer.Serialize(new
                {
                    sessionId = sessionContext.SessionId,
                    tenantId = sessionContext.TenantId,
                    tool = toolName,
                    decision,
                    details,
                    isMasked,
                    truncated,
                    estimatedTokens,
                    timestamp = DateTimeOffset.UtcNow
                })
            };
            await _auditLogRepository.RecordAuditEventAsync(entry, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to record audit event for MCP tool execution '{ToolName}'.", toolName);
            if (!_options.Value.IsMcpAuthBypassed)
            {
                throw new System.Security.SecurityException($"Zero-Trust: audit logging for MCP tool '{toolName}' failed. Execution aborted (fail-closed).", ex);
            }
        }
    }

    /// <summary>
    /// SEC M-17: Detects the structured error payload produced by the MCP query executor
    /// (<c>{"isError":true,"error":{"code":...,"message":...}}</c>).
    /// </summary>
    public static bool TryGetExecutorError(string? resultJson, out string? errorCode)
    {
        errorCode = null;
        if (string.IsNullOrWhiteSpace(resultJson))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(resultJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("isError", out var isErrorProp) ||
                isErrorProp.ValueKind != JsonValueKind.True)
            {
                return false;
            }

            if (root.TryGetProperty("error", out var errorProp) &&
                errorProp.ValueKind == JsonValueKind.Object &&
                errorProp.TryGetProperty("code", out var codeProp) &&
                codeProp.ValueKind == JsonValueKind.String)
            {
                errorCode = codeProp.GetString();
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// SEC A-4: Resolves every table a tool call touches. Returns null (fail-closed) when the table set cannot be
    /// determined reliably. Curated operations are analysed via the GraphQL AST (root fields and literal
    /// <c>table(domain, name)</c> arguments), never via a guessed first root field.
    /// </summary>
    internal static IReadOnlyList<TableIdentifier>? ParseTablesFromTool(McpToolDefinition tool, string? argumentsJson = null)
    {
        if (tool.TargetTable == null && !string.IsNullOrWhiteSpace(tool.TargetGraphQLOperation) &&
            !IsBuiltInTableTool(tool.Name))
        {
            return McpGraphQlTableResolver.ResolveTables(tool.TargetGraphQLOperation);
        }

        var single = ParseTableIdentifierFromTool(tool, argumentsJson);
        return single == null ? null : [single.Value];
    }

    /// <summary>
    /// Tables read by a query_graphql call, from the catalog GraphQL schema. Null (fail-closed) without a single
    /// <c>query</c> argument or for a document that is not one query on catalog fields; a pure introspection query
    /// is checked like a catalog listing.
    /// </summary>
    private async Task<IReadOnlyList<TableIdentifier>?> ResolveGraphQlTablesAsync(string? argumentsJson, CancellationToken ct)
    {
        if (_graphQlCatalogMap == null || !McpDatasetTools.TryGetStringArgument(argumentsJson, "query", out var query) || string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        var tables = await _graphQlCatalogMap.ResolveDocumentTablesAsync(query, ct).ConfigureAwait(false);
        return tables switch
        {
            null => null,
            { Count: 0 } => [McpDatasetTools.CatalogTable],
            _ => tables
        };
    }

    private static bool IsBuiltInTableTool(string name) =>
        name.Equals("query_customers", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("query_invoices", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("query_data_catalog", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("get_golden_queries", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("simulate_query", StringComparison.OrdinalIgnoreCase) ||
        McpDatasetTools.IsDatasetTool(name);

    private static TableIdentifier? ParseTableIdentifierFromTool(McpToolDefinition tool, string? argumentsJson = null)
    {
        if (tool.TargetTable != null)
            return tool.TargetTable;

        if (tool.Name.Equals("query_customers", StringComparison.OrdinalIgnoreCase))
            return new TableIdentifier("finance", "dbo", "customers");
        if (tool.Name.Equals("query_invoices", StringComparison.OrdinalIgnoreCase))
            return new TableIdentifier("finance", "dbo", "invoices");
        if (tool.Name.Equals("query_data_catalog", StringComparison.OrdinalIgnoreCase))
            return new TableIdentifier("governance", "catalog", "assets");
        if (tool.Name.Equals("get_golden_queries", StringComparison.OrdinalIgnoreCase))
            return new TableIdentifier("governance", "mcp", "golden_queries");

        if (McpDatasetTools.IsDatasetTool(tool.Name))
            return McpDatasetTools.TargetTable(tool.Name, argumentsJson);

        if (tool.Name.Equals("simulate_query", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(argumentsJson))
            {
                var m = Regex.Match(argumentsJson, @"table\s*\\?\(\s*domain:\s*\\*""([^""\\]+)\\*""\s*,\s*(?:schema:\s*\\*""([^""\\]+)\\*""\s*,\s*)?name:\s*\\*""([^""\\]+)\\*""", RegexOptions.IgnoreCase, DefaultRegexTimeout);
                if (m.Success)
                {
                    var d = m.Groups[1].Value;
                    var s = m.Groups[2].Success ? m.Groups[2].Value : "dbo";
                    var n = m.Groups[3].Value;
                    return new TableIdentifier(d, s, n);
                }
            }
            return new TableIdentifier("governance", "simulator", "ast");
        }

        if (tool.Name.StartsWith("query_", StringComparison.OrdinalIgnoreCase) && tool.Name.Length > 6)
        {
            return new TableIdentifier("default", "dbo", tool.Name[6..].ToLowerInvariant());
        }

        return null;
    }

    private static string ScrubPiiAndSensitiveData(string input, out bool wasModified)
    {
        wasModified = false;
        if (string.IsNullOrWhiteSpace(input)) return input;

        string result = input;

        // Mask emails: john.doe@example.com -> j***@example.com
        if (EmailRegex.IsMatch(result))
        {
            result = EmailRegex.Replace(result, "$1***@$2");
            wasModified = true;
        }

        // Mask IBANs: DE89 3704 ... 1234 -> **** **** **** 1234
        if (IbanRegex.IsMatch(result))
        {
            result = IbanRegex.Replace(result, "**** **** **** 1234");
            wasModified = true;
        }

        // GDPR Art. 9 Sensitive Categories -> [REDACTED-GDPR-ART9]
        if (GdprArt9Regex.IsMatch(result))
        {
            result = GdprArt9Regex.Replace(result, @"""$1"":""[REDACTED-GDPR-ART9]""");
            wasModified = true;
        }

        return result;
    }

    private static string GenerateRawToolResponse(McpToolDefinition tool, string argumentsJson, string tenantId)
    {
        var safeTenant = new TenantId(tenantId).Value;
        var encodedTenant = System.Text.Encodings.Web.JavaScriptEncoder.Default.Encode(safeTenant);

        return tool.Name.ToLowerInvariant() switch
        {
            "query_customers" => $$"""
            {
              "tenantId": "{{encodedTenant}}",
              "customers": [
                {
                  "id": "CUST-1001",
                  "name": "Erika Mustermann",
                  "email": "erika.mustermann@acme-corp.com",
                  "iban": "DE89 3704 0044 0532 0130 00",
                  "healthCondition": "Diabetes Type 2",
                  "tier": "Enterprise"
                },
                {
                  "id": "CUST-1002",
                  "name": "Max Mustermann",
                  "email": "max.mustermann@partner.org",
                  "iban": "DE12 5001 0517 0648 4898 90",
                  "tier": "Standard"
                }
              ]
            }
            """,

            "query_invoices" => $$"""
            {
              "tenantId": "{{encodedTenant}}",
              "invoices": [
                {
                  "invoiceId": "INV-2026-001",
                  "amount": 14500.00,
                  "currency": "EUR",
                  "status": "PAID"
                },
                {
                  "invoiceId": "INV-2026-002",
                  "amount": 3200.50,
                  "currency": "EUR",
                  "status": "PENDING"
                }
              ]
            }
            """,

            "query_data_catalog" => $$"""
            {
              "tenantId": "{{encodedTenant}}",
              "assets": [
                {
                  "tableName": "customers",
                  "classification": "CONFIDENTIAL",
                  "sensitivity": "HIGH",
                  "owner": "data-steward-sales@company.com",
                  "tags": ["PII", "GDPR.Article9", "Financial"]
                }
              ]
            }
            """,

            "get_golden_queries" => $$"""
            {
              "tenantId": "{{encodedTenant}}",
              "queries": [
                {
                  "id": "golden_customers_active",
                  "domain": "finance",
                  "tableName": "customers",
                  "title": "Get Active Customers with Account Details",
                  "queryText": "query GetActiveCustomers { customers(filter: { status: \"ACTIVE\" }) { id name email iban } }"
                }
              ]
            }
            """,

            _ => $$"""
            {
              "tenantId": "{{encodedTenant}}",
              "tool": "{{tool.Name}}",
              "status": "COMPLETED",
              "data": { "operation": "{{tool.TargetGraphQLOperation}}" }
            }
            """
        };
    }
}
