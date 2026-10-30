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
    private async Task RecordAuditEventAsync(
        string toolName,
        McpSessionContext sessionContext,
        string decision,
        string details,
        bool isMasked,
        bool truncated,
        int estimatedTokens,
        CancellationToken ct,
        string eventType = "MCP_TOOL_EXECUTION")
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

    /// <summary>The subject of a session for ABAC and audit: the user SID, or the application identity of an app-only session.</summary>
    private static string CallerSidOf(McpSessionContext sessionContext) =>
        !string.IsNullOrWhiteSpace(sessionContext.UserSid) ? sessionContext.UserSid : sessionContext.ServicePrincipalId;

    /// <summary>
    /// The Casbin ABAC request of a session for one target table. SR-M-08: an app-only session (no user SID) is evaluated without group
    /// SIDs, exactly like its principal (<see cref="McpProtocolHandler.BuildPrincipalFromSession"/>).
    /// </summary>
    private static SecurityEvaluationContext BuildSecurityContext(McpSessionContext sessionContext, TableIdentifier targetTable)
    {
        var groupSids = !string.IsNullOrWhiteSpace(sessionContext.UserSid) && sessionContext.GroupSids is { Count: > 0 }
            ? sessionContext.GroupSids.Select(s => new Sid(s)).ToArray()
            : [];
        var clientIp = !string.IsNullOrWhiteSpace(sessionContext.ClientIp) && System.Net.IPAddress.TryParse(sessionContext.ClientIp, out var parsedIp)
            ? parsedIp : System.Net.IPAddress.None;

        return new SecurityEvaluationContext(
            UserSid: new Sid(CallerSidOf(sessionContext)), GroupSids: groupSids, Tenant: new TenantId(sessionContext.TenantId),
            TargetTable: targetTable, RequestedColumns: [], ClientIp: clientIp,
            Timestamp: DateTimeOffset.UtcNow, PurposeId: "MCP_AI_AGENT_QUERY");
    }

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
    /// <c>message</c> (JSON-RPC, OData, Iceberg, Backstage) or the Envoy pass-through <c>{ "error": "...", "status": 4xx }</c>.
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
    /// Applies the per-call token budget (MCP-08): a result above <c>Mcp.MaxTokensPerCall</c> is replaced by a truncation notice.
    /// Shared by dataset tools and doc tools so no tool skips the budget.
    /// </summary>
    private string ApplyTokenBudget(string toolName, string json, out int estimatedTokens, out bool truncated)
    {
        int maxTokens = _options.Value.Mcp.MaxTokensPerCall > 0 ? _options.Value.Mcp.MaxTokensPerCall : 4096;
        estimatedTokens = Math.Max(1, json.Length / 4);
        truncated = false;

        if (estimatedTokens > maxTokens)
        {
            _logger.LogWarning("Tool result for '{ToolName}' ({EstimatedTokens} tokens) exceeded max token budget ({MaxTokens}). Truncating result.", toolName, estimatedTokens, maxTokens);
            int maxChars = maxTokens * 4;
            if (json.Length > maxChars)
            {
                json = JsonSerializer.Serialize(new
                {
                    _meta = new
                    {
                        truncated = true, reason = "TOKEN_BUDGET_EXCEEDED", warning = "[TRUNCATED DUE TO MCP TOKEN BUDGET]",
                        maxTokens, note = "Result exceeded token budget. Use pagination (offset/limit) or filters to narrow the result."
                    }
                });
                estimatedTokens = Math.Max(1, json.Length / 4);
                truncated = true;
            }
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
        name.Equals("simulate_query", StringComparison.OrdinalIgnoreCase) ||
        McpDatasetTools.IsDatasetTool(name) ||
        McpSemanticTools.IsSemanticTool(name);

    private static bool ToolReadsDatasetRows(McpToolDefinition tool)
    {
        if (McpDatasetTools.IsDatasetTool(tool.Name)) return McpDatasetTools.ReadsRows(tool.Name);
        if (McpSemanticTools.IsSemanticTool(tool.Name)) return McpSemanticTools.ReadsRows(tool.Name);
        if (tool.Name.Equals("get_golden_queries", StringComparison.OrdinalIgnoreCase) ||
            tool.Name.Equals("query_data_catalog", StringComparison.OrdinalIgnoreCase) ||
            tool.Name.Equals("simulate_query", StringComparison.OrdinalIgnoreCase))
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
    /// The real tables a <c>call_procedure</c> or <c>query_metrics</c> call reads, for the four-eyes / inactive / visibility gate.
    /// Null (fail closed) when they cannot be resolved; empty when the call names nothing the executor could run.
    /// </summary>
    private Task<IReadOnlyList<TableIdentifier>?> ResolveUnderlyingTablesAsync(
        McpToolDefinition tool,
        string? argumentsJson,
        McpSessionContext sessionContext,
        CancellationToken ct) =>
        tool.Name.Equals(McpDatasetTools.CallProcedure, StringComparison.OrdinalIgnoreCase)
            ? Task.FromResult(ResolveProcedureTables(argumentsJson))
            : ResolveMetricTablesAsync(argumentsJson, sessionContext, ct);

    /// <summary>
    /// The tables of a registered procedure, exactly the set its visibility stage consent-checks (GovernedProcedureExecutionService):
    /// the validated referenced tables of an Active procedure plus its result table, each keyed under the procedure's catalog domain.
    /// Unknown and non-Active procedures resolve to no tables alike (the executor refuses both).
    /// </summary>
    private IReadOnlyList<TableIdentifier>? ResolveProcedureTables(string? argumentsJson)
    {
        var name = McpDatasetTools.ProcedureNameArgument(argumentsJson);
        if (name == null || _procedureRegistry == null)
        {
            return null;
        }

        if (!_procedureRegistry.TryGet(name, out var registered) || registered == null)
        {
            // An unknown procedure runs nothing (the executor answers NOT_FOUND); denying here instead would answer unknown and
            // hidden procedures differently (SEC-GOV153-02 existence oracle).
            return [];
        }

        if (registered.State != ProcedureState.Active)
        {
            // A non-Active procedure reads nothing: the executor refuses it (NOT_FOUND, or unavailable only for callers that may
            // see it). Denying here would answer it differently from an unknown procedure (SEC-GOV153-02 existence oracle).
            return [];
        }

        var definition = registered.Definition;
        var keys = new List<string>();
        if (registered.Validation != null)
        {
            keys.AddRange(registered.Validation.ReferencedTables);
        }

        if (definition.ResultTable != null && !keys.Contains(definition.ResultTable, StringComparer.OrdinalIgnoreCase))
        {
            keys.Add(definition.ResultTable);
        }

        if (keys.Count == 0)
        {
            return null;
        }

        var tables = new List<TableIdentifier>(keys.Count);
        foreach (var key in keys)
        {
            if (!TryParseProcedureTable(definition.CatalogDomain, key, out var tableId))
            {
                return null;
            }

            if (!tables.Contains(tableId))
            {
                tables.Add(tableId);
            }
        }

        return tables;
    }

    /// <summary>Same key-to-table mapping as the procedure visibility stage; a malformed key is unresolvable.</summary>
    private static bool TryParseProcedureTable(string catalogDomain, string key, out TableIdentifier tableId)
    {
        try
        {
            return TableIdentifier.TryParse(catalogDomain + "." + key, out tableId) || TableIdentifier.TryParse(key, out tableId);
        }
        catch (ArgumentException)
        {
            tableId = default;
            return false;
        }
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
        if (Autheris.Application.Dbt.Services.SemanticTenancy.FromOptions(_options).ResolveCallerTenant(principal) is not { } tenant)
        {
            return null;
        }

        var models = await _semanticCatalog.ListSemanticModelsAsync(tenant, ct).ConfigureAwait(false);
        var resolver = new Autheris.Application.Dbt.Services.SemanticBaseTableResolver(_tableMetadataRepository);
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
        if (tool.Name.Equals("query_customers", StringComparison.OrdinalIgnoreCase)) return new TableIdentifier("finance", "dbo", "customers");
        if (tool.Name.Equals("query_invoices", StringComparison.OrdinalIgnoreCase)) return new TableIdentifier("finance", "dbo", "invoices");
        if (tool.Name.Equals("query_data_catalog", StringComparison.OrdinalIgnoreCase)) return new TableIdentifier("governance", "catalog", "assets");
        if (tool.Name.Equals("get_golden_queries", StringComparison.OrdinalIgnoreCase)) return new TableIdentifier("governance", "mcp", "golden_queries");
        if (McpDatasetTools.IsDatasetTool(tool.Name)) return McpDatasetTools.TargetTable(tool.Name, argumentsJson);
        if (McpSemanticTools.IsSemanticTool(tool.Name)) return McpSemanticTools.SemanticCatalogTable;

        if (tool.Name.Equals("simulate_query", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(argumentsJson))
            {
                var m = Regex.Match(argumentsJson, @"table\s*\\?\(\s*domain:\s*\\*""([^""\\]+)\\*""\s*,\s*(?:schema:\s*\\*""([^""\\]+)\\*""\s*,\s*)?name:\s*\\*""([^""\\]+)\\*""", RegexOptions.IgnoreCase, DefaultRegexTimeout);
                if (m.Success)
                {
                    return new TableIdentifier(m.Groups[1].Value, m.Groups[2].Success ? m.Groups[2].Value : "dbo", m.Groups[3].Value);
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

        if (EmailRegex.IsMatch(result)) { result = EmailRegex.Replace(result, "$1***@$2"); wasModified = true; }
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
