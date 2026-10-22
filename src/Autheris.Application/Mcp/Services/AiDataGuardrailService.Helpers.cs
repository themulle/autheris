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
        CancellationToken ct)
    {
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
    /// Whether a tool result is a structured error: the executor shape (<c>"isError": true</c>, optional <c>error.code</c>) or the bare
    /// legacy shape <c>{ "error": "...", "message": "..." }</c> (an object with a string <c>error</c>, an optional string <c>message</c> and
    /// nothing else). Both are audited as DENY and returned with <c>isError: true</c>.
    /// </summary>
    public static bool TryGetExecutorError(string? resultJson, out string? errorCode)
    {
        errorCode = null;
        if (string.IsNullOrWhiteSpace(resultJson)) return false;
        try
        {
            using var doc = JsonDocument.Parse(resultJson);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && IsLegacyErrorShape(root, out var legacyCode))
            {
                errorCode = legacyCode;
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

    private static bool IsLegacyErrorShape(JsonElement root, out string? errorCode)
    {
        errorCode = null;
        string? error = null;
        foreach (var prop in root.EnumerateObject())
        {
            if (string.Equals(prop.Name, "error", StringComparison.Ordinal) && prop.Value.ValueKind == JsonValueKind.String)
            {
                error = prop.Value.GetString();
            }
            else if (!(string.Equals(prop.Name, "message", StringComparison.Ordinal) && prop.Value.ValueKind == JsonValueKind.String))
            {
                return false;
            }
        }

        if (string.IsNullOrWhiteSpace(error))
        {
            return false;
        }

        // Only a code-like value (no blanks, short) is reported as the code; a free-text error is reported as ERROR.
        errorCode = error.Length <= 64 && !error.Any(char.IsWhiteSpace) ? error : "ERROR";
        return true;
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

    private static string ScrubPiiAndSensitiveData(string input, out bool wasModified)
    {
        wasModified = false;
        if (string.IsNullOrWhiteSpace(input)) return input;
        string result = input;
        if (EmailRegex.IsMatch(result)) { result = EmailRegex.Replace(result, "$1***@$2"); wasModified = true; }
        if (IbanRegex.IsMatch(result)) { result = IbanRegex.Replace(result, "**** **** **** 1234"); wasModified = true; }
        if (GdprArt9Regex.IsMatch(result)) { result = GdprArt9Regex.Replace(result, @"""$1"":""[REDACTED-GDPR-ART9]"""); wasModified = true; }
        return result;
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
