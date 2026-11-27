namespace Autheris.Application.Mcp.Services;

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;

public sealed partial class AiDataGuardrailService
{
    private sealed class AuditDetails
    {
        public string? sessionId { get; init; }
        public string? tenantId { get; init; }
        public string? tool { get; init; }
        public string? decision { get; init; }
        public string? details { get; init; }
        public bool isMasked { get; init; }
        public bool truncated { get; init; }
        public int estimatedTokens { get; init; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public int? returnedRows { get; init; }
        public DateTimeOffset timestamp { get; init; }
    }

    private async Task RecordAuditEventAsync(
        string toolName,
        McpSessionContext sessionContext,
        string decision,
        string details,
        bool isMasked,
        bool truncated,
        int estimatedTokens,
        CancellationToken ct,
        string eventType = "MCP_TOOL_EXECUTION",
        int? returnedRows = null)
    {
        try
        {
            var actorSid = !string.IsNullOrWhiteSpace(sessionContext.UserSid)
                ? new Sid(sessionContext.UserSid)
                : new Sid(sessionContext.ServicePrincipalId);
            var tenantId = TenantId.TryParse(sessionContext.TenantId, out var tid) ? tid : TenantId.Platform;
            var entry = new AuditLogEntry
            {
                TenantId = tenantId,
                EventType = eventType,
                ActorSid = actorSid,
                TargetTable = toolName,
                Decision = decision,
                TraceId = sessionContext.SessionId,
                DetailsJson = JsonSerializer.Serialize(new AuditDetails
                {
                    sessionId = sessionContext.SessionId,
                    tenantId = sessionContext.TenantId,
                    tool = toolName,
                    decision = decision,
                    details = details,
                    isMasked = isMasked,
                    truncated = truncated,
                    estimatedTokens = estimatedTokens,
                    returnedRows = returnedRows,
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

    /// <summary>The subject of a session for ABAC and audit: the user SID, or the application identity of an app-only session.</summary>
    private static string CallerSidOf(McpSessionContext sessionContext) =>
        !string.IsNullOrWhiteSpace(sessionContext.UserSid) ? sessionContext.UserSid : sessionContext.ServicePrincipalId;

    /// <summary>
    /// The read decision of a session for one target table. The subject is the session principal (an app-only session carries no group
    /// SIDs and a stale group snapshot is degraded, SR-M-08); roles are never grantees. A table without catalog metadata, or a
    /// failing decision point, denies (fail closed).
    /// </summary>
    private async ValueTask<TableAccessDecision> DecideReadAsync(McpSessionContext sessionContext, TableIdentifier table, CancellationToken ct)
    {
        try
        {
            var metadata = _tableMetadataRepository == null
                ? null
                : await _tableMetadataRepository.GetTableMetadataAsync(table, ct).ConfigureAwait(false);
            if (metadata == null)
            {
                return TableAccessDecision.Denied(table, "Table not found in the catalog.");
            }

            var principal = McpProtocolHandler.BuildPrincipalFromSession(sessionContext, _timeProvider)!;
            var subject = Autheris.Domain.Security.SecurityPrincipalContext.FromPrincipal(
                principal, new TenantId(sessionContext.TenantId),
                clientIp: !string.IsNullOrWhiteSpace(sessionContext.ClientIp) && System.Net.IPAddress.TryParse(sessionContext.ClientIp, out var ip) ? ip : System.Net.IPAddress.None,
                timeProvider: _timeProvider);
            return await _decisionPoint.DecideAsync(
                new Autheris.Application.Policy.Pdp.AccessRequest(
                    subject, metadata, Autheris.Application.Policy.Pdp.AccessOperation.Read, Autheris.Application.Policy.Pdp.AccessTransport.Mcp),
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogError("MCP guardrail decision failed ({FailureType}); denying (fail-closed).", ex.GetType().Name);
            return TableAccessDecision.Denied(table, "Decision failed (fail-closed).");
        }
    }

    /// <summary>
    /// Whether the call is a catalog-level or metadata tool (no data rows, nothing to decide); <paramref name="table"/> is its target. Keyed on the tool,
    /// never on a table name alone: a tool that parses table names from user input always reaches the decision point.
    /// </summary>
    private static bool IsCatalogToolTarget(McpToolDefinition tool, TableIdentifier table) =>
        tool.Name.Equals("query_data_catalog", StringComparison.OrdinalIgnoreCase) ||
        tool.Name.Equals("get_golden_queries", StringComparison.OrdinalIgnoreCase) ||
        // Metadata tools read no data rows; discovery visibility governs them. get_table_schema stays an access target per table.
        (McpDatasetTools.IsDatasetTool(tool.Name) && !McpDatasetTools.ReadsRows(tool.Name) &&
         !tool.Name.Equals(McpDatasetTools.GetTableSchema, StringComparison.OrdinalIgnoreCase) &&
         !tool.Name.Equals(McpDatasetTools.DescribeDataset, StringComparison.OrdinalIgnoreCase)) ||
        McpSemanticTools.IsSemanticTool(tool.Name) ||
        ((tool.Name.Equals("query_graphql", StringComparison.OrdinalIgnoreCase) ||
          tool.Name.Equals("simulate_query", StringComparison.OrdinalIgnoreCase)) && table.Equals(McpDatasetTools.CatalogTable)) ||
        (tool.Name.Equals(McpDatasetTools.InvokeApi, StringComparison.OrdinalIgnoreCase) && table.Equals(McpDataTargetResolver.ApiNoTable));

    /// <summary>
    /// describe_dataset with brief:true never emits value-bearing statistics. When not brief, a visible dataset for which the
    /// caller holds no data grant (such as an open container or a catalog grant) also emits no value-bearing statistics
    /// (DiscoveryAccessMapper withholds them unless the read decision is allowed); when the caller holds a data grant (or admin
    /// view), the decision point decides so a policy denial blocks value-bearing profiles.
    /// </summary>
    private async ValueTask<bool> IsValueFreeDescribeAsync(
        McpToolDefinition tool, string? argumentsJson, McpSessionContext sessionContext, TableIdentifier table, CancellationToken ct)
    {
        if (!tool.Name.Equals(McpDatasetTools.DescribeDataset, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (McpDatasetTools.IsBriefRequested(argumentsJson))
        {
            return true;
        }

        if (_catalogVisibility is null || _consentRepository is null)
        {
            return false;
        }

        try
        {
            var principal = McpProtocolHandler.BuildPrincipalFromSession(sessionContext, _timeProvider);
            if (principal is null)
            {
                return false;
            }

            var caller = new Autheris.Application.Catalog.Interfaces.CatalogCaller(
                principal,
                McpCatalogVisibility.SafeTenantId(principal),
                Autheris.Application.Catalog.Interfaces.CatalogChannel.Mcp);
            if (await _catalogVisibility.FindVisibleAsync(caller, table, ct).ConfigureAwait(false) is null)
            {
                return false;
            }

            var (consents, pending, isAdmin) = await McpDatasetCatalog.ResolveCallerConsentsAsync(
                principal, _options.Value, _consentRepository, null, null, ct).ConfigureAwait(false);
            var status = McpDatasetCatalog.AccessStatusOf(table, consents, pending, isAdmin, _options.Value.IsMcpAuthBypassed);
            return !string.Equals(status, "GRANTED", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>Reason codes of a denied decision for the audit trail: codes only, never predicate values or policy text.</summary>
    private static string ReasonCodesOf(TableAccessDecision decision) =>
        decision.Reasons.Count == 0 ? "DEFAULT_DENY" : string.Join(';', decision.Reasons.Select(r => r.Code));

    /// <summary>
    /// Whether a tool result is a structured error: the executor shape (<c>"isError": true</c>, optional <c>error.code</c>), a governed
    /// ProblemDetails body (integer <c>status</c> of 400 or more plus a string <c>code</c> or <c>type</c>) or a relayed protocol error
    /// object. All are audited as DENY and returned with <c>isError: true</c>. The legacy bare <c>{ "error": "..." }</c> shape is no longer
    /// produced anywhere (NoNewBareErrorBodiesTests, baseline empty) and is not recognised.
    /// </summary>
    public static bool TryGetExecutorError(string? resultJson, out string? errorCode)
    {
        var isError = TryGetExecutorErrorCore(resultJson, out var rawCode);
        errorCode = NormalizeAuditCode(rawCode);
        return isError;
    }

    /// <summary>
    /// NL-SC-39: the code a tool result supplies is attacker-influenceable text; before it reaches the audit trail it must be a governed
    /// code format (upper-case identifier, at most 64 characters), otherwise the fixed <c>ERROR</c> marker is used.
    /// </summary>
    private static string? NormalizeAuditCode(string? code) =>
        code is null ? null : (code.Length <= 64 && AuditCodeFormat().IsMatch(code) ? code : "ERROR");

    [GeneratedRegex("^[A-Z][A-Z0-9_]{2,63}$", RegexOptions.CultureInvariant, 50)]
    private static partial Regex AuditCodeFormat();

    private static bool TryGetExecutorErrorCore(string? resultJson, out string? errorCode)
    {
        errorCode = null;
        if (string.IsNullOrWhiteSpace(resultJson)) return false;
        try
        {
            using var doc = JsonDocument.Parse(resultJson);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && IsProtocolErrorShape(root, out var protocolCode))
            {
                errorCode = protocolCode;
                return true;
            }

            if (root.ValueKind == JsonValueKind.Object && IsProblemDetailsShape(root, out var problemCode))
            {
                errorCode = problemCode;
                return true;
            }

            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("isError", out var isErrorProp) || isErrorProp.ValueKind != JsonValueKind.True)
            {
                return false;
            }
            if (root.TryGetProperty("error", out var errorProp) && errorProp.ValueKind == JsonValueKind.Object &&
                errorProp.TryGetProperty("code", out var codeProp) && codeProp.ValueKind == JsonValueKind.String)
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

    private static bool IsProblemDetailsShape(JsonElement root, out string? errorCode)
    {
        errorCode = null;
        if (!root.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.Number ||
            !status.TryGetInt32(out var statusCode) || statusCode < 400)
        {
            return false;
        }

        // A governed problem body carries a string code; RFC 7807 bodies without one are recognised by their type URI (NL-SC-26).
        if (root.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(code.GetString()))
        {
            errorCode = code.GetString();
            return true;
        }

        if (root.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(type.GetString()))
        {
            errorCode = "ERROR";
            return true;
        }

        return false;
    }

    /// <summary>
    /// Protocol-mandated error bodies that an MCP-reachable tool may relay (NL-SC-29): an object-valued <c>error</c> member with a string
    /// <c>message</c> (JSON-RPC, OData, Iceberg) or the Envoy pass-through <c>{ "error": "...", "status": 4xx }</c>.
    /// </summary>
    private static bool IsProtocolErrorShape(JsonElement root, out string? errorCode)
    {
        errorCode = null;
        if (!root.TryGetProperty("error", out var error))
        {
            return false;
        }

        // NL-SC-39 / NL-T-40: an object-valued top-level error member is an error even without a string message (fail closed).
        if (error.ValueKind == JsonValueKind.Object)
        {
            errorCode = error.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(code.GetString())
                ? code.GetString()
                : "ERROR";
            return true;
        }

        if (error.ValueKind == JsonValueKind.String && root.TryGetProperty("status", out var status) &&
            status.ValueKind == JsonValueKind.Number && status.TryGetInt32(out var statusCode) && statusCode >= 400)
        {
            errorCode = "ERROR";
            return true;
        }

        return false;
    }

    internal static async Task<IReadOnlyList<TableIdentifier>?> ParseTablesFromToolAsync(
        McpToolDefinition tool,
        string? argumentsJson = null,
        TrinoSqlEngine.ISqlEngine? sqlEngine = null,
        string? defaultDataSourceName = null,
        CancellationToken ct = default)
    {
        if (string.Equals(tool.Name, McpDatasetTools.QuerySql, StringComparison.OrdinalIgnoreCase))
        {
            var (tables, _) = await McpDataTargetResolver.TryResolveSqlTablesAsync(
                McpDatasetTools.TryGetStringArgument(argumentsJson, "query", out var sql) ? sql : null,
                sqlEngine,
                defaultDataSourceName,
                ct).ConfigureAwait(false);
            return tables;
        }
        if (string.Equals(tool.Name, McpDatasetTools.InvokeApi, StringComparison.OrdinalIgnoreCase))
        {
            return await McpDataTargetResolver.ResolveInvokeApiTablesAsync(argumentsJson, sqlEngine, defaultDataSourceName, ct).ConfigureAwait(false);
        }
        return ParseTablesFromTool(tool, argumentsJson, sqlEngine, defaultDataSourceName);
    }

    internal static IReadOnlyList<TableIdentifier>? ParseTablesFromTool(
        McpToolDefinition tool,
        string? argumentsJson = null,
        TrinoSqlEngine.ISqlEngine? sqlEngine = null,
        string? defaultDataSourceName = null)
    {
        if (string.Equals(tool.Name, McpDatasetTools.QuerySql, StringComparison.OrdinalIgnoreCase))
        {
            return McpDataTargetResolver.ResolveSqlTables(
                McpDatasetTools.TryGetStringArgument(argumentsJson, "query", out var sql) ? sql : null,
                sqlEngine,
                defaultDataSourceName);
        }
        if (string.Equals(tool.Name, McpDatasetTools.InvokeApi, StringComparison.OrdinalIgnoreCase))
        {
            return McpDataTargetResolver.ResolveInvokeApiTables(argumentsJson, sqlEngine, defaultDataSourceName);
        }
        if (string.Equals(tool.Name, McpDatasetTools.GetTableSchema, StringComparison.OrdinalIgnoreCase))
        {
            // Every requested dataset ('dataset' and each 'datasets' entry) is an ABAC target, parsed exactly as the executor parses
            // them; malformed or unresolvable arguments are null (fail closed).
            return McpDatasetTools.TableSchemaTargets(argumentsJson);
        }
        if (tool.TargetTable == null && !string.IsNullOrWhiteSpace(tool.TargetGraphQLOperation) && !IsBuiltInTableTool(tool.Name))
        {
            return McpGraphQlTableResolver.ResolveTables(tool.TargetGraphQLOperation);
        }
        var single = ParseTableIdentifierFromTool(tool, argumentsJson);
        return single == null ? null : [single.Value];
    }

    /// <summary>
    /// Output inspection (MCP-09): string values of a tool or resource result that carry a suspected prompt injection are replaced by a
    /// fixed marker and audited as <c>MCP_OUTPUT_INJECTION_SUSPECTED</c>. The call is never denied, so a poisoned catalog text cannot be
    /// used to block access.
    /// </summary>
    private async Task<string> InspectOutputAsync(
        string toolName,
        string json,
        McpSessionContext sessionContext,
        CancellationToken ct,
        Action<bool>? markMasked = null)
    {
        var sanitized = _promptGuardrail.SanitizeToolOutput(toolName, json, out var withheld, out var attackType);
        if (withheld == 0 || sanitized is null)
        {
            return json;
        }

        _logger.LogWarning("Suspected prompt injection withheld from the output of '{ToolName}' ({Count} value(s), {AttackType}).", toolName, withheld, attackType);
        markMasked?.Invoke(true);
        await RecordAuditEventAsync(
            toolName, sessionContext, "ALLOW",
            $"Output withheld: {withheld} value(s) with suspected prompt injection ({attackType ?? "unknown"}).",
            true, false, Math.Max(1, sanitized.Length / 4), ct, Autheris.Domain.Audit.AuditEventTypes.McpOutputInjectionSuspected).ConfigureAwait(false);
        return sanitized;
    }

    /// <summary>
    /// Applies the per-call token budget (MCP-08): a catalog result above <c>Mcp.MaxTokensPerCall</c> is shortened by whole datasets or, for
    /// <c>describe_dataset</c>, whole columns (issue #820; <paramref name="argumentsJson"/> carries the column cursor), any other result is
    /// replaced by the <c>TOKEN_BUDGET_EXCEEDED</c> error envelope (<paramref name="failed"/> is then true). Shared by dataset tools and doc tools so no tool skips the budget.
    /// </summary>
    private string ApplyTokenBudget(
        string toolName,
        string json,
        out int estimatedTokens,
        out bool truncated,
        out bool failed,
        out int? returnedRows,
        string? argumentsJson = null,
        Func<int, string?>? resumeAfter = null)
    {
        int maxTokens = _options.Value.Mcp.MaxTokensPerCall > 0 ? _options.Value.Mcp.MaxTokensPerCall : 4096;
        estimatedTokens = Math.Max(1, json.Length / 4);
        truncated = false;
        failed = false;
        returnedRows = null;

        if (estimatedTokens > maxTokens)
        {
            _logger.LogWarning("Tool result for '{ToolName}' ({EstimatedTokens} tokens) exceeded max token budget ({MaxTokens}). Truncating result.", toolName, estimatedTokens, maxTokens);
            int maxChars = maxTokens * 4;
            string? hint = null;
            if (json.Length > maxChars && McpBudgetShortening.TryShorten(toolName, json, argumentsJson, maxChars, maxTokens, out var shortened))
            {
                json = shortened;
                truncated = true;
            }
            else if (json.Length > maxChars)
            {
                var rowOutcome = McpRowShortening.AppliesTo(toolName)
                    ? McpRowShortening.Shorten(toolName, json, maxChars, resumeAfter)
                    : default;
                switch (rowOutcome.Status)
                {
                    case McpRowShorteningStatus.Shortened:
                        json = rowOutcome.Json;
                        truncated = true;
                        returnedRows = rowOutcome.ReturnedRows;
                        break;
                    case McpRowShorteningStatus.Fits:
                        json = rowOutcome.Json;
                        break;
                    case McpRowShorteningStatus.FirstRowTooLarge:
                        hint = McpRowShortening.FirstRowNote(toolName);
                        returnedRows = 0;
                        break;
                    default:
                        hint = McpBudgetShortening.NoticeNote(toolName, argumentsJson);
                        break;
                }

                if (rowOutcome.Status is McpRowShorteningStatus.NotApplicable or McpRowShorteningStatus.FirstRowTooLarge)
                {
                    json = McpToolError.Serialize(new McpToolErrorBody(McpErrorCodes.TokenBudgetExceeded, McpErrorCodes.FixedMessage(McpErrorCodes.TokenBudgetExceeded))
                    {
                        Hint = hint
                    });
                    failed = true;
                    truncated = true;
                }
            }

            estimatedTokens = Math.Max(1, json.Length / 4);
        }

        return json;
    }

    private async Task<IReadOnlyList<TableIdentifier>?> ResolveGraphQlTablesAsync(string? argumentsJson, CancellationToken ct)
    {
        if (_graphQlCatalogMap == null || !McpDatasetTools.TryGetStringArgument(argumentsJson, "query", out var query) || string.IsNullOrWhiteSpace(query))
            return null;
        var tables = await _graphQlCatalogMap.ResolveDocumentTablesAsync(query, ct).ConfigureAwait(false);
        return tables switch { null => null, { Count: 0 } => [McpDatasetTools.CatalogTable], _ => tables };
    }

    private static bool IsBuiltInTableTool(string name) =>
        name.Equals("query_customers", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("query_invoices", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("query_data_catalog", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("get_golden_queries", StringComparison.OrdinalIgnoreCase) ||
        McpDatasetTools.IsDatasetTool(name) ||
        McpSemanticTools.IsSemanticTool(name);

    private static bool ToolReadsDatasetRows(McpToolDefinition tool)
    {
        if (McpDatasetTools.IsDatasetTool(tool.Name)) return McpDatasetTools.ReadsRows(tool.Name);
        if (McpSemanticTools.IsSemanticTool(tool.Name)) return McpSemanticTools.ReadsRows(tool.Name);
        if (tool.Name.Equals("get_golden_queries", StringComparison.OrdinalIgnoreCase) ||
            tool.Name.Equals("query_data_catalog", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return true;
    }

    /// <summary>Tools whose rows come from tables other than their ABAC object (a procedure, a semantic metric).</summary>
    private static bool ReadsThroughUnderlyingTables(McpToolDefinition tool) =>
        tool.Name.Equals(McpDatasetTools.CallProcedure, StringComparison.OrdinalIgnoreCase) ||
        tool.Name.Equals(McpSemanticTools.QueryMetrics, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The real tables a <c>call_procedure</c> or <c>query_metrics</c> call reads, for the inactive / visibility gate.
    /// Null (fail closed) when they cannot be resolved; empty when the call names nothing the executor could run.
    /// </summary>
    private Task<IReadOnlyList<TableIdentifier>?> ResolveUnderlyingTablesAsync(
        McpToolDefinition tool,
        string? argumentsJson,
        McpSessionContext sessionContext,
        CancellationToken ct) =>
        tool.Name.Equals(McpDatasetTools.CallProcedure, StringComparison.OrdinalIgnoreCase)
            ? Task.FromResult(ResolveProcedureTables(argumentsJson, sessionContext))
            : ResolveMetricTablesAsync(argumentsJson, sessionContext, ct);

    /// <summary>
    /// The tables of a registered procedure, exactly the set its visibility stage consent-checks (GovernedProcedureExecutionService):
    /// the validated referenced tables of an Active procedure plus its result table, each keyed under the procedure's catalog domain.
    /// Unknown and non-Active procedures resolve to no tables alike (the executor refuses both).
    /// </summary>
    private IReadOnlyList<TableIdentifier>? ResolveProcedureTables(string? argumentsJson, McpSessionContext sessionContext)
    {
        var name = McpDatasetTools.ProcedureNameArgument(argumentsJson);
        if (name == null || _procedureRegistry == null)
        {
            return null;
        }

        if (!_procedureRegistry.TryGet(name, out var registered) || registered == null)
        {
            // An unknown procedure runs nothing (the executor answers NOT_FOUND); denying here instead would answer unknown and
            // hidden procedures differently (02 existence oracle).
            return [];
        }

        if (registered.State != ProcedureState.Active)
        {
            // A non-Active procedure reads nothing: the executor refuses it (NOT_FOUND, or unavailable only for callers that may
            // see it). Denying here would answer it differently from an unknown procedure (02 existence oracle).
            return [];
        }

        if (!string.IsNullOrWhiteSpace(registered.Definition.ProductSchema))
        {
            // a product procedure is governed on its product object (the tables its SQL touches carry no consent for the caller)
            var principal = McpProtocolHandler.BuildPrincipalFromSession(sessionContext);
            return Autheris.Application.Catalog.Semantic.SemanticTenancy.FromOptions(_options).ResolveCallerTenant(principal) is { } tenant
                ? [Autheris.Application.Products.ProductSource.ObjectOf(tenant, registered.Definition.ProductSchema.Trim().ToLowerInvariant(), registered.Definition.Name.ToLowerInvariant())]
                : null;
        }

        var tables = registered.GovernedTables();
        return tables is { Count: > 0 } ? tables : null;
    }

    /// <summary>
    /// The base tables of the requested metrics in the caller's semantic catalog partition, resolved like the metric query service
    /// does (metric -> semantic model -> active base table). Any metric, model or table that does not resolve is fail-closed.
    /// </summary>
    private async Task<IReadOnlyList<TableIdentifier>?> ResolveMetricTablesAsync(string? argumentsJson, McpSessionContext sessionContext, CancellationToken ct)
    {
        IReadOnlyList<string> metricNames;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            metricNames = doc.RootElement.ValueKind == JsonValueKind.Object ? McpSemanticTools.MetricNamesArgument(doc.RootElement) : [];
        }
        catch (JsonException)
        {
            // The executor rejects the call without reading data.
            return [];
        }

        if (metricNames.Count == 0)
        {
            return [];
        }

        if (_semanticCatalog == null || _tableMetadataRepository == null)
        {
            return null;
        }

        var principal = McpProtocolHandler.BuildPrincipalFromSession(sessionContext);
        if (Autheris.Application.Catalog.Semantic.SemanticTenancy.FromOptions(_options).ResolveCallerTenant(principal) is not { } tenant)
        {
            return null;
        }

        var models = await _semanticCatalog.ListSemanticModelsAsync(tenant, ct).ConfigureAwait(false);
        var resolver = new Autheris.Application.Catalog.Semantic.SemanticBaseTableResolver(_tableMetadataRepository);
        var tables = new List<TableIdentifier>();
        foreach (var metricName in metricNames)
        {
            var metric = await _semanticCatalog.GetMetricAsync(metricName.Trim(), tenant, ct).ConfigureAwait(false);
            if (metric == null)
            {
                return null;
            }

            DbtSemanticModel model;
            try
            {
                (model, _) = Autheris.Application.Analytics.SemanticMetricQueryService.ResolveModelAndMeasures(metric, models);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                return null;
            }

            var baseTable = await resolver.TryResolveAsync(model, isVisible: null, ct).ConfigureAwait(false);
            if (baseTable == null)
            {
                return null;
            }

            if (!tables.Contains(baseTable.Identifier))
            {
                tables.Add(baseTable.Identifier);
            }
        }

        return tables;
    }

    private static TableIdentifier? ParseTableIdentifierFromTool(McpToolDefinition tool, string? argumentsJson = null)
    {
        if (tool.TargetTable != null) return tool.TargetTable;
        if (tool.Name.Equals("query_data_catalog", StringComparison.OrdinalIgnoreCase)) return new TableIdentifier("governance", "catalog", "assets");
        if (tool.Name.Equals("get_golden_queries", StringComparison.OrdinalIgnoreCase)) return new TableIdentifier("governance", "mcp", "golden_queries");
        if (McpDatasetTools.IsDatasetTool(tool.Name)) return McpDatasetTools.TargetTable(tool.Name, argumentsJson);
        if (McpSemanticTools.IsSemanticTool(tool.Name)) return McpSemanticTools.SemanticCatalogTable;

        if (tool.Name.StartsWith("query_", StringComparison.OrdinalIgnoreCase) && tool.Name.Length > 6)
        {
            return new TableIdentifier("default", "dbo", tool.Name[6..].ToLowerInvariant());
        }
        return null;
    }

    internal static string ScrubPiiAndSensitiveData(string input, out bool wasModified)
    {
        wasModified = false;
        if (string.IsNullOrWhiteSpace(input)) return input;
        string result = input;
        // MCP-10: the default JSON encoder escapes '+' as \u002B, which would split an e-mail local part; decode it first.
        if (result.Contains("\\u002B", StringComparison.OrdinalIgnoreCase))
        {
            result = result.Replace("\\u002B", "+", StringComparison.OrdinalIgnoreCase);
        }

        var emailScrubbed = EmailRegex.Replace(result, "$1***@$2");
        if (!ReferenceEquals(emailScrubbed, result) && emailScrubbed != result) { result = emailScrubbed; wasModified = true; }
        var ibanScrubbed = IbanRegex.Replace(result, m => IsValidIban(m.Value) ? "**** **** **** 1234" : m.Value);
        if (!ReferenceEquals(ibanScrubbed, result) && ibanScrubbed != result) { result = ibanScrubbed; wasModified = true; }
        if (GdprArt9Regex.IsMatch(result)) { result = GdprArt9Regex.Replace(result, @"""$1"":""[REDACTED-GDPR-ART9]"""); wasModified = true; }
        return result;
    }

    /// <summary>ISO 7064 mod-97-10 check of an IBAN candidate (spaces ignored, 15-34 characters).</summary>
    internal static bool IsValidIban(string candidate)
    {
        Span<char> buffer = stackalloc char[40];
        var n = 0;
        foreach (var c in candidate)
        {
            if (c == ' ') continue;
            if (n >= buffer.Length) return false;
            buffer[n++] = char.ToUpperInvariant(c);
        }

        if (n is < 15 or > 34) return false;

        // Move the first four characters to the end, then compute the remainder digit by digit.
        var remainder = 0;
        for (var i = 0; i < n; i++)
        {
            var c = buffer[(i + 4) % n];
            if (c is >= '0' and <= '9')
            {
                remainder = ((remainder * 10) + (c - '0')) % 97;
            }
            else if (c is >= 'A' and <= 'Z')
            {
                remainder = ((remainder * 100) + (c - 'A' + 10)) % 97;
            }
            else
            {
                return false;
            }
        }

        return remainder == 1;
    }

    private static string GenerateRawToolResponse(McpToolDefinition tool, string argumentsJson, string tenantId)
    {
        var encodedTenant = System.Text.Encodings.Web.JavaScriptEncoder.Default.Encode(new TenantId(tenantId).Value);
        return tool.Name.ToLowerInvariant() switch
        {
            "query_customers" => $$"""{"tenantId":"{{encodedTenant}}","customers":[{"id":"CUST-1001","name":"Erika Mustermann","email":"erika.mustermann@acme-corp.com","iban":"DE89 3704 0044 0532 0130 00","healthCondition":"Diabetes Type 2","tier":"Enterprise"},{"id":"CUST-1002","name":"Max Mustermann","email":"max.mustermann@partner.org","iban":"DE12 5001 0517 0648 4898 90","tier":"Standard"}]}""",
            "query_invoices" => $$"""{"tenantId":"{{encodedTenant}}","invoices":[{"invoiceId":"INV-2026-001","amount":14500.00,"currency":"EUR","status":"PAID"},{"invoiceId":"INV-2026-002","amount":3200.50,"currency":"EUR","status":"PENDING"}]}""",
            "query_data_catalog" => $$"""{"tenantId":"{{encodedTenant}}","assets":[{"tableName":"customers","classification":"CONFIDENTIAL","sensitivity":"HIGH","owner":"data-steward-sales@company.com","tags":["PII","GDPR.Article9","Financial"]}]}""",
            "get_golden_queries" => $$"""{"tenantId":"{{encodedTenant}}","queries":[{"id":"golden_customers_active","domain":"finance","tableName":"customers","title":"Get Active Customers with Account Details","queryText":"query GetActiveCustomers { customers(filter: { status: \"ACTIVE\" }) { id name email iban } }"}]}""",
            _ => JsonSerializer.Serialize(new
            {
                tenantId = encodedTenant,
                tool = tool.Name,
                status = "COMPLETED",
                data = new { operation = tool.TargetGraphQLOperation }
            })
        };
    }
}
