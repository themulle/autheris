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
    IOptions<GatewayOptions>? options = null,
    IReadPathSwitches? switches = null,
    Autheris.Application.Catalog.Interfaces.ICatalogVisibilityService? catalogVisibility = null) : ISemanticMcpCompiler
{
    private const int DefaultPageSize = 100;
    private const int MaxPageSize = 100;

    private readonly Autheris.Application.Catalog.Interfaces.ICatalogVisibilityService? _catalogVisibility = catalogVisibility;
    private readonly ITableMetadataRepository _metadataRepo = metadataRepo ?? throw new ArgumentNullException(nameof(metadataRepo));
    private readonly ILogger<SemanticMcpCompiler> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IGoldenQueryService? _goldenQueryService = goldenQueryService;
    private readonly IConsentRepository? _consentRepo = consentRepo;
    private readonly IOptions<GatewayOptions>? _options = options;
    private static readonly JsonSerializerOptions CachedIndentedOptions = new() { WriteIndented = true };

    /// <summary>First <paramref name="length"/> UTF-16 units of <paramref name="text"/>, never ending on a lone high surrogate.</summary>
    private static string CutOnRuneBoundary(string text, int length) =>
        length > 0 && char.IsHighSurrogate(text[length - 1]) ? text[..(length - 1)] : text[..length];

    public async Task<McpToolDefinition> CompileToolAsync(
        string toolName,
        TableIdentifier targetTable,
        CancellationToken ct = default)
    {
        var meta = await _metadataRepo.GetTableMetadataAsync(targetTable, ct).ConfigureAwait(false);
        var tableDesc = !string.IsNullOrWhiteSpace(meta?.Table.Description)
            ? meta.Table.Description
            : (meta is null
                ? $"Dataset for {targetTable.Domain}.{targetTable.TableName}"
                : McpCatalogText.DisplayName(meta.Table.DisplayName, targetTable.TableName)); // SEC-CAT146A-01: externally authored text, never raw

        // Enforce Dynamic Compactor constraint: Short description < 120 chars for LLM Tool Picker
        var shortDesc = tableDesc.Length > 110 ? CutOnRuneBoundary(tableDesc, 107) + "..." : tableDesc;
        shortDesc = $"{shortDesc} ({targetTable.Domain}.{targetTable.TableName})";
        if (shortDesc.Length > 120)
        {
            shortDesc = CutOnRuneBoundary(shortDesc, 117) + "...";
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
        // PLAN-SEC-115 A-6: semantic resources render catalog metadata, so they follow the Catalog group (no repository call when off).
        if (switches is not null && !switches.IsGroupEnabled(McpToolGroup.Catalog))
        {
            return [];
        }

        var allTables = await _metadataRepo.GetAllTablesAsync(ct).ConfigureAwait(false);
        var visible = await VisibleTablesAsync(allTables, principal, ct).ConfigureAwait(false);

        var filtered = string.IsNullOrWhiteSpace(domainScope)
            ? visible
            : visible.Where(t => string.Equals(t.Identifier.Domain, domainScope, StringComparison.OrdinalIgnoreCase)).ToList();

        var resources = new List<McpResourceItem>();
        foreach (var t in filtered)
        {
            resources.Add(RenderGlossary(t));
            foreach (var col in VisibleColumns(t))
            {
                if (RenderColumnDocs(t, col) is { } colDoc)
                {
                    resources.Add(colDoc);
                }
            }

            if (await RenderExamplesAsync(t, ct).ConfigureAwait(false) is { } examples)
            {
                resources.Add(examples);
            }
        }

        return resources;
    }

    public async Task<McpResourcePage> ListSemanticResourceUrisAsync(
        System.Security.Claims.ClaimsPrincipal? principal,
        string? cursor = null,
        int pageSize = DefaultPageSize,
        CancellationToken ct = default)
    {
        if (switches is not null && !switches.IsGroupEnabled(McpToolGroup.Catalog))
        {
            return new McpResourcePage([], null);
        }

        // An unreadable cursor restarts nothing: it is rejected, so a crafted value cannot skip the visibility checks.
        var offset = 0;
        if (!string.IsNullOrEmpty(cursor) && (!int.TryParse(cursor, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out offset) || offset < 0))
        {
            return new McpResourcePage([], null);
        }

        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        var allTables = await _metadataRepo.GetAllTablesAsync(ct).ConfigureAwait(false);
        var visible = await VisibleTablesAsync(allTables, principal, ct).ConfigureAwait(false);

        // Descriptor items only (no Text): nothing is rendered and no golden query is loaded to list the URIs.
        var items = new List<McpResourceItem>();
        foreach (var t in visible.OrderBy(t => t.Identifier.ToString(), StringComparer.Ordinal))
        {
            var domain = t.Identifier.Domain;
            var table = t.Identifier.TableName;
            items.Add(new McpResourceItem(
                $"glossary://{domain}/{table}", $"{domain}_{table}_glossary",
                $"Business glossary and semantic column definitions for {domain}.{table}", "text/markdown", string.Empty));
            foreach (var col in VisibleColumns(t))
            {
                if (HasColumnDocs(col))
                {
                    items.Add(new McpResourceItem(
                        $"dbt://models/{table}/columns/{col.ColumnName}/docs", $"{table}_{col.ColumnName}_docs",
                        $"Documentation and metadata for {table}.{col.ColumnName}", "text/markdown", string.Empty));
                }
            }

            if (_goldenQueryService != null)
            {
                items.Add(new McpResourceItem(
                    $"examples://{domain}/{table}", $"{domain}_{table}_golden_queries",
                    $"Verified golden GraphQL queries and few-shot examples for {domain}.{table}", "text/markdown", string.Empty));
            }
        }

        var page = items.Skip(offset).Take(pageSize).ToList();
        var next = offset + page.Count < items.Count ? (offset + page.Count).ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
        return new McpResourcePage(page, next);
    }

    public async Task<McpResourceItem?> ReadSemanticResourceAsync(
        System.Security.Claims.ClaimsPrincipal? principal,
        string uri,
        CancellationToken ct = default)
    {
        if (switches is not null && !switches.IsGroupEnabled(McpToolGroup.Catalog))
        {
            return null;
        }

        if (!TryParseSemanticUri(uri, out var kind, out var domain, out var table, out var column))
        {
            return null;
        }

        // Only the requested table is checked and rendered; unknown and invisible tables answer alike (null).
        var allTables = await _metadataRepo.GetAllTablesAsync(ct).ConfigureAwait(false);
        var candidates = allTables.Where(t =>
            string.Equals(t.Identifier.TableName, table, StringComparison.OrdinalIgnoreCase) &&
            (domain is null || string.Equals(t.Identifier.Domain, domain, StringComparison.OrdinalIgnoreCase))).ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        var visible = await VisibleTablesAsync(candidates, principal, ct).ConfigureAwait(false);
        foreach (var t in visible.OrderBy(t => t.Identifier.ToString(), StringComparer.Ordinal))
        {
            switch (kind)
            {
                case SemanticUriKind.Glossary:
                    return RenderGlossary(t);
                case SemanticUriKind.Examples:
                    return await RenderExamplesAsync(t, ct).ConfigureAwait(false);
                default:
                    var col = VisibleColumns(t).FirstOrDefault(c => string.Equals(c.ColumnName, column, StringComparison.OrdinalIgnoreCase));
                    if (col != null && RenderColumnDocs(t, col) is { } doc)
                    {
                        return doc;
                    }

                    break;
            }
        }

        return null;
    }

    private enum SemanticUriKind
    {
        Glossary,
        ColumnDocs,
        Examples
    }

    private static bool TryParseSemanticUri(string? uri, out SemanticUriKind kind, out string? domain, out string table, out string? column)
    {
        kind = SemanticUriKind.Glossary;
        domain = null;
        table = string.Empty;
        column = null;
        if (string.IsNullOrWhiteSpace(uri) || uri.Length > 1024)
        {
            return false;
        }

        if (uri.StartsWith("glossary://", StringComparison.OrdinalIgnoreCase) || uri.StartsWith("examples://", StringComparison.OrdinalIgnoreCase))
        {
            kind = uri.StartsWith("glossary://", StringComparison.OrdinalIgnoreCase) ? SemanticUriKind.Glossary : SemanticUriKind.Examples;
            var parts = uri["glossary://".Length..].Split('/');
            if (parts.Length != 2 || parts.Any(string.IsNullOrWhiteSpace))
            {
                return false;
            }

            domain = parts[0];
            table = parts[1];
            return true;
        }

        const string dbtPrefix = "dbt://models/";
        if (uri.StartsWith(dbtPrefix, StringComparison.OrdinalIgnoreCase) && uri.EndsWith("/docs", StringComparison.OrdinalIgnoreCase))
        {
            var parts = uri[dbtPrefix.Length..^"/docs".Length].Split('/');
            if (parts.Length != 3 || !string.Equals(parts[1], "columns", StringComparison.OrdinalIgnoreCase) || parts.Any(string.IsNullOrWhiteSpace))
            {
                return false;
            }

            kind = SemanticUriKind.ColumnDocs;
            table = parts[0];
            column = parts[2];
            return true;
        }

        return false;
    }

    /// <summary>
    /// The tables the caller may see: consent, tier and column rules intersected with the catalog visibility service (ReBAC, MCP-14).
    /// The explicit (production-blocked) MCP auth bypass opens the whole catalog (SEC H-02).
    /// </summary>
    private async Task<IReadOnlyList<TableMetadata>> VisibleTablesAsync(
        IReadOnlyList<TableMetadata> allTables,
        System.Security.Claims.ClaimsPrincipal? principal,
        CancellationToken ct)
    {
        if (_options?.Value.IsMcpAuthBypassed == true)
        {
            return allTables;
        }

        if (_consentRepo != null)
        {
            return await McpCatalogVisibility.VisibleTablesAsync(allTables, principal, _consentRepo, _options?.Value, _catalogVisibility, ct).ConfigureAwait(false);
        }

        if (principal != null)
        {
            return Array.Empty<TableMetadata>();
        }

        // No principal and no consent repository (tests and tools without a caller); a visibility service never opens this case.
        return _catalogVisibility is null ? allTables : Array.Empty<TableMetadata>();
    }

    private static List<TableColumn> VisibleColumns(TableMetadata t) => t.Columns.Where(col =>
    {
        if (t.ColumnMaskingRules != null &&
            t.ColumnMaskingRules.TryGetValue(col.ColumnName, out var rule) &&
            string.Equals(rule.RuleType, "DENY", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }).ToList();

    private static McpResourceItem RenderGlossary(TableMetadata t)
    {
        var domain = t.Identifier.Domain;
        var table = t.Identifier.TableName;
        var visibleColumns = VisibleColumns(t);

        var glossaryText = $"# Business Glossary: {domain}.{table}\n\n" +
                           (!string.IsNullOrWhiteSpace(t.Table.Description) ? $"**Description**: {t.Table.Description}\n\n" : "") +
                           $"* **Domain**: {domain}\n" +
                           $"* **Table**: {table}\n" +
                           $"* **Sensitivity**: {t.Table.Sensitivity}\n" +
                           $"* **Columns**:\n" +
                           string.Join("\n", visibleColumns.Select(c =>
                           {
                               var sensitivityTag = c.IsSensitive ? " [SENSITIVE/MASKED]" : "";
                               var descTag = !string.IsNullOrWhiteSpace(c.Description) ? $": {c.Description}" : "";
                               return $"  - `{c.ColumnName}` ({c.DataType}){sensitivityTag}{descTag}";
                           }));

        return new McpResourceItem(
            Uri: $"glossary://{domain}/{table}",
            Name: $"{domain}_{table}_glossary",
            Description: $"Business glossary and semantic column definitions for {domain}.{table}",
            MimeType: "text/markdown",
            Text: glossaryText);
    }

    /// <summary>Issue #127: the resource is not level aware, so only descriptive meta keys are published (no accepted values, ranges, examples).</summary>
    private static Dictionary<string, string> DescriptiveMeta(TableColumn col)
    {
        var docMeta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (metaKey, metaValue) in col.Meta ?? new Dictionary<string, string>())
        {
            if (McpDatasetCatalog.DescriptiveMetaKeys.Contains(metaKey))
            {
                docMeta[metaKey] = metaValue;
            }
        }

        return docMeta;
    }

    private static bool HasColumnDocs(TableColumn col) =>
        !string.IsNullOrWhiteSpace(col.Description) || !string.IsNullOrWhiteSpace(col.LongDescription) || DescriptiveMeta(col).Count > 0;

    private static McpResourceItem? RenderColumnDocs(TableMetadata t, TableColumn col)
    {
        var domain = t.Identifier.Domain;
        var table = t.Identifier.TableName;
        var hasDesc = !string.IsNullOrWhiteSpace(col.Description);
        var hasLongDesc = !string.IsNullOrWhiteSpace(col.LongDescription);
        var docMeta = DescriptiveMeta(col);
        var hasMeta = docMeta.Count > 0;
        if (!hasDesc && !hasLongDesc && !hasMeta)
        {
            return null;
        }

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
            sb.AppendLine(JsonSerializer.Serialize(docMeta, CachedIndentedOptions));
            sb.AppendLine("```");
            sb.AppendLine();
        }

        return new McpResourceItem(
            Uri: $"dbt://models/{table}/columns/{col.ColumnName}/docs",
            Name: $"{table}_{col.ColumnName}_docs",
            Description: $"Documentation and metadata for {table}.{col.ColumnName}",
            MimeType: "text/markdown",
            Text: sb.ToString().TrimEnd());
    }

    /// <summary>Golden queries / few-shot examples of one table (examples://{domain}/{table}); loaded only when this table is rendered.</summary>
    private async Task<McpResourceItem?> RenderExamplesAsync(TableMetadata t, CancellationToken ct)
    {
        if (_goldenQueryService == null)
        {
            return null;
        }

        var domain = t.Identifier.Domain;
        var table = t.Identifier.TableName;
        var goldens = await _goldenQueryService.GetGoldenQueriesAsync(domain, table, ct).ConfigureAwait(false);
        if (goldens.Count == 0)
        {
            return null;
        }

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

        return new McpResourceItem(
            Uri: $"examples://{domain}/{table}",
            Name: $"{domain}_{table}_golden_queries",
            Description: $"Verified golden GraphQL queries and few-shot examples for {domain}.{table}",
            MimeType: "text/markdown",
            Text: sbExamples.ToString().TrimEnd());
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
