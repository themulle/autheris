namespace Autheris.Application.Mcp.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Semantic MCP compiler fusing dbt documentation and catalog metadata into AI tool definitions and resources (F-AI-02).
/// </summary>
public sealed class SemanticMcpCompiler(
    ITableMetadataRepository metadataRepo,
    ILogger<SemanticMcpCompiler> logger,
    IGoldenQueryService? goldenQueryService = null,
    IConsentRepository? consentRepo = null,
    IOptions<GatewayOptions>? options = null) : ISemanticMcpCompiler
{
    private readonly ITableMetadataRepository _metadataRepo = metadataRepo ?? throw new ArgumentNullException(nameof(metadataRepo));
    private readonly ILogger<SemanticMcpCompiler> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IGoldenQueryService? _goldenQueryService = goldenQueryService;
    private readonly IConsentRepository? _consentRepo = consentRepo;
    private readonly IOptions<GatewayOptions>? _options = options;
    private static readonly JsonSerializerOptions CachedIndentedOptions = new() { WriteIndented = true };

    public async Task<McpToolDefinition> CompileToolAsync(
        string toolName,
        TableIdentifier targetTable,
        CancellationToken ct = default)
    {
        var meta = await _metadataRepo.GetTableMetadataAsync(targetTable, ct).ConfigureAwait(false);
        var tableDesc = !string.IsNullOrWhiteSpace(meta?.Table.Description)
            ? meta.Table.Description
            : (meta?.Table.DisplayName ?? $"Dataset for {targetTable.Domain}.{targetTable.TableName}");

        // Enforce Dynamic Compactor constraint: Short description < 120 chars for LLM Tool Picker
        var shortDesc = tableDesc.Length > 110 ? tableDesc[..107] + "..." : tableDesc;
        shortDesc = $"{shortDesc} ({targetTable.Domain}.{targetTable.TableName})";
        if (shortDesc.Length > 120)
        {
            shortDesc = shortDesc[..117] + "...";
        }

        var properties = new Dictionary<string, object>
        {
            ["first"] = new { type = "integer", description = "Max rows to return (default 10, max 1000)." },
            ["offset"] = new { type = "integer", description = "Offset for pagination." },
            ["filter"] = new { type = "string", description = "Zero-Trust filter expression." }
        };

        if (meta?.Columns != null && meta.Columns.Count > 0)
        {
            var colDict = new Dictionary<string, object>();
            foreach (var col in meta.Columns)
            {
                var desc = !string.IsNullOrWhiteSpace(col.Description)
                    ? col.Description
                    : $"Column {col.ColumnName} ({col.DataType})";
                if (desc.Length > 120)
                {
                    desc = desc[..117] + "...";
                }
                colDict[col.ColumnName] = new
                {
                    type = MapDataTypeToJsonType(col.DataType),
                    description = desc
                };
            }
            properties["columns"] = new
            {
                type = "object",
                description = "Available columns for projection and filtering with business descriptions.",
                properties = colDict
            };
        }

        var inputSchemaObj = new
        {
            type = "object",
            properties = properties
        };

        var inputJsonSchema = JsonSerializer.Serialize(inputSchemaObj);
        var targetOp = $"query {{ table(domain: \"{targetTable.Domain}\", name: \"{targetTable.TableName}\") {{ tableName totalCount jsonRows }} }}";

        return new McpToolDefinition(
            Name: toolName,
            Description: shortDesc,
            InputJsonSchema: inputJsonSchema,
            TargetGraphQLOperation: targetOp,
            TargetTable: targetTable
        );
    }

    public async Task<IReadOnlyList<McpResourceItem>> GetSemanticResourcesAsync(
        string? domainScope = null,
        System.Security.Claims.ClaimsPrincipal? principal = null,
        CancellationToken ct = default)
    {
        var allTables = await _metadataRepo.GetAllTablesAsync(ct).ConfigureAwait(false);
        // SEC H-02: OpenSchema no longer opens MCP resources; only the explicit (production-blocked) MCP auth bypass does.
        var isMcpAuthBypassed = _options?.Value.IsMcpAuthBypassed == true;
        if (_consentRepo != null && !isMcpAuthBypassed)
        {
            allTables = await FilterTablesForPrincipalAsync(allTables, principal, ct).ConfigureAwait(false);
        }

        var filtered = string.IsNullOrWhiteSpace(domainScope)
            ? allTables
            : allTables.Where(t => string.Equals(t.Identifier.Domain, domainScope, StringComparison.OrdinalIgnoreCase));

        var resources = new List<McpResourceItem>();

        foreach (var t in filtered)
        {
            var domain = t.Identifier.Domain;
            var table = t.Identifier.TableName;

            // 1. Glossary Resource
            var glossaryText = $"# Business Glossary: {domain}.{table}\n\n" +
                               (!string.IsNullOrWhiteSpace(t.Table.Description) ? $"**Description**: {t.Table.Description}\n\n" : "") +
                               $"* **Domain**: {domain}\n" +
                               $"* **Table**: {table}\n" +
                               $"* **Sensitivity**: {t.Table.Sensitivity}\n" +
                               $"* **Columns**:\n" +
                               string.Join("\n", t.Columns.Select(c =>
                               {
                                   var sensitivityTag = c.IsSensitive ? " [SENSITIVE/MASKED]" : "";
                                   var descTag = !string.IsNullOrWhiteSpace(c.Description) ? $": {c.Description}" : "";
                                   return $"  - `{c.ColumnName}` ({c.DataType}){sensitivityTag}{descTag}";
                               }));

            resources.Add(new McpResourceItem(
                Uri: $"glossary://{domain}/{table}",
                Name: $"{domain}_{table}_glossary",
                Description: $"Business glossary and semantic column definitions for {domain}.{table}",
                MimeType: "text/markdown",
                Text: glossaryText
            ));

            // 2. dbt Lineage Resource
            var lineageText = $"# dbt Lineage & Contract: {table}\n\n" +
                              $"* **Model**: models/marts/{domain}/{table}.sql\n" +
                              $"* **Primary Keys**: {string.Join(", ", t.PrimaryKeyColumns)}\n" +
                              $"* **Upstream**: sources.{domain}.raw_{table}\n" +
                              $"* **Governance Contract**: Enforced fail-closed Zero-Trust policy.";

            resources.Add(new McpResourceItem(
                Uri: $"dbt://models/{table}/lineage",
                Name: $"{table}_dbt_lineage",
                Description: $"dbt lineage graph and schema contracts for {table}",
                MimeType: "text/markdown",
                Text: lineageText
            ));

            // 3. Column-level Docs Resources on Demand
            foreach (var col in t.Columns)
            {
                var hasDesc = !string.IsNullOrWhiteSpace(col.Description);
                var hasLongDesc = !string.IsNullOrWhiteSpace(col.LongDescription);
                var hasMeta = col.Meta != null && col.Meta.Count > 0;

                if (hasDesc || hasLongDesc || hasMeta)
                {
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine($"# Column Documentation: {table}.{col.ColumnName}");
                    sb.AppendLine();
                    sb.AppendLine($"* **Domain**: {domain}");
                    sb.AppendLine($"* **Table**: {table}");
                    sb.AppendLine($"* **Column**: `{col.ColumnName}`");
                    sb.AppendLine($"* **Data Type**: `{col.DataType}`");
                    sb.AppendLine($"* **Sensitivity**: {(col.IsSensitive ? "Sensitive/Masked" : "Standard")}");
                    sb.AppendLine();

                    if (hasDesc)
                    {
                        sb.AppendLine("## Description");
                        sb.AppendLine(col.Description);
                        sb.AppendLine();
                    }

                    if (hasLongDesc)
                    {
                        sb.AppendLine("## Detailed Specification");
                        sb.AppendLine(col.LongDescription);
                        sb.AppendLine();
                    }

                    if (hasMeta)
                    {
                        sb.AppendLine("## dbt / OpenMetadata Meta");
                        sb.AppendLine("```json");
                        sb.AppendLine(JsonSerializer.Serialize(col.Meta, CachedIndentedOptions));
                        sb.AppendLine("```");
                        sb.AppendLine();
                    }

                    resources.Add(new McpResourceItem(
                        Uri: $"dbt://models/{table}/columns/{col.ColumnName}/docs",
                        Name: $"{table}_{col.ColumnName}_docs",
                        Description: $"Documentation and metadata for {table}.{col.ColumnName}",
                        MimeType: "text/markdown",
                        Text: sb.ToString().TrimEnd()
                    ));
                }
            }

            // 4. Golden Queries / Few-Shot Examples Resource (examples://{domain}/{table})
            if (_goldenQueryService != null)
            {
                var goldens = await _goldenQueryService.GetGoldenQueriesAsync(domain, table, ct).ConfigureAwait(false);
                if (goldens.Count > 0)
                {
                    var sbExamples = new System.Text.StringBuilder();
                    sbExamples.AppendLine($"# Golden Queries & Verified Few-Shot Examples: {domain}.{table}");
                    sbExamples.AppendLine();
                    sbExamples.AppendLine("Use these verified queries as few-shot patterns to eliminate hallucinations on this dataset:");
                    sbExamples.AppendLine();
                    foreach (var g in goldens)
                    {
                        sbExamples.AppendLine($"## {g.Title}");
                        if (!string.IsNullOrWhiteSpace(g.Description))
                        {
                            sbExamples.AppendLine(g.Description);
                        }
                        sbExamples.AppendLine("```graphql");
                        sbExamples.AppendLine(g.QueryText.Trim());
                        sbExamples.AppendLine("```");
                        if (!string.IsNullOrWhiteSpace(g.VariablesJson))
                        {
                            sbExamples.AppendLine("Variables:");
                            sbExamples.AppendLine("```json");
                            sbExamples.AppendLine(g.VariablesJson.Trim());
                            sbExamples.AppendLine("```");
                        }
                        sbExamples.AppendLine();
                    }

                    resources.Add(new McpResourceItem(
                        Uri: $"examples://{domain}/{table}",
                        Name: $"{domain}_{table}_golden_queries",
                        Description: $"Verified golden GraphQL queries and few-shot examples for {domain}.{table}",
                        MimeType: "text/markdown",
                        Text: sbExamples.ToString().TrimEnd()
                    ));
                }
            }
        }

        return resources;
    }

    /// <summary>
    /// SEC M-28: anonymous callers (no principal, no SID, the synthetic MCP anonymous SID) see no tables at all;
    /// only explicit governance roles see the whole catalog; everybody else sees only tables with an active
    /// Allow consent within the caller's own tenant.
    /// </summary>
    private async Task<IReadOnlyList<TableMetadata>> FilterTablesForPrincipalAsync(
        IReadOnlyList<TableMetadata> allTables,
        System.Security.Claims.ClaimsPrincipal? principal,
        CancellationToken ct)
    {
        if (principal == null || _consentRepo == null)
        {
            return Array.Empty<TableMetadata>();
        }

        var userSid = principal.GetUserSid();
        bool isAnonymous = userSid == null || string.Equals(userSid.Value.Value, "ANONYMOUS_MCP_CLIENT", StringComparison.OrdinalIgnoreCase);
        if (isAnonymous)
        {
            return Array.Empty<TableMetadata>();
        }

        var roles = principal.GetUserRoles();
        bool isGlobalAdmin = roles.Contains("GovernanceAdmin") || roles.Contains("ClusterAdmin");
        if (isGlobalAdmin)
        {
            return allTables;
        }

        var groupSids = principal.GetGroupSids();
        var tenantId = principal.GetTenantId();
        var allSubjects = groupSids.Append(userSid!.Value).ToList();
        var activeConsents = await _consentRepo.GetAllActiveConsentsForSubjectsAsync(
            allSubjects, roles, DateTimeOffset.UtcNow, tenantId, ct).ConfigureAwait(false);

        // MCP-1: same table and column visibility as the GraphQL catalog (Deny consents, column grants).
        return CatalogVisibility.FilterForSubject(allTables, activeConsents, tenantId);
    }

    private static string MapDataTypeToJsonType(string? dataType)
    {
        if (string.IsNullOrWhiteSpace(dataType)) return "string";
        var dt = dataType.Trim().ToLowerInvariant();
        if (dt.Contains("int") || dt.Contains("long") || dt.Contains("serial")) return "integer";
        if (dt.Contains("float") || dt.Contains("double") || dt.Contains("decimal") || dt.Contains("numeric") || dt.Contains("money")) return "number";
        if (dt.Contains("bool")) return "boolean";
        return "string";
    }
}
