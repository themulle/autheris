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

    public static bool TryGetExecutorError(string? resultJson, out string? errorCode)
    {
        errorCode = null;
        if (string.IsNullOrWhiteSpace(resultJson)) return false;
        try
        {
            using var doc = JsonDocument.Parse(resultJson);
            var root = doc.RootElement;
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
