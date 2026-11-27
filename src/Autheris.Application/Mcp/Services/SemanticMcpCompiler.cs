namespace Autheris.Application.Mcp.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
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
    Autheris.Application.Catalog.Interfaces.ICatalogVisibilityService? catalogVisibility = null,
    IGraphQlCatalogMap? graphQlCatalogMap = null,
    Autheris.Application.Policy.Pdp.IAccessDecisionPoint? decisionPoint = null,
    IOptions<Autheris.Application.Catalog.Services.CatalogDiscoveryOptions>? discoveryOptions = null) : ISemanticMcpCompiler
{
    private const int DefaultPageSize = 100;
    private const int MaxPageSize = 100;

    private readonly Autheris.Application.Catalog.Interfaces.ICatalogVisibilityService? _catalogVisibility = catalogVisibility;
    private readonly ITableMetadataRepository _metadataRepo = metadataRepo ?? throw new ArgumentNullException(nameof(metadataRepo));
    private readonly ILogger<SemanticMcpCompiler> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IGoldenQueryService? _goldenQueryService = goldenQueryService;
    private readonly IConsentRepository? _consentRepo = consentRepo;
    private readonly IOptions<Autheris.Application.Catalog.Services.CatalogDiscoveryOptions>? _discoveryOptions = discoveryOptions;
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
                : McpCatalogText.DisplayName(meta.Table.DisplayName, targetTable.TableName)); // externally authored text, never raw

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
            var visibleCols = VisibleColumns(meta);
            var colDict = new Dictionary<string, object>();
            foreach (var col in visibleCols)
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
        // 115 A-6: semantic resources render catalog metadata, so they follow the Catalog group (no repository call when off).
        if (switches is not null && !switches.IsGroupEnabled(McpToolGroup.Catalog))
        {
            return [];
        }

        var scopeFilter = string.IsNullOrWhiteSpace(domainScope) ||
                          domainScope.Length > Autheris.Application.Catalog.CatalogTableQuery.MaxFilterLength ||
                          domainScope.AsSpan().IndexOfAny('\0', '\r', '\n') >= 0
            ? null
            : domainScope;
        var allTables = await Autheris.Application.Catalog.CatalogTablePageReader.ReadAllAsync(
            _metadataRepo,
            new Autheris.Application.Catalog.CatalogTableQuery(
                DataSource: scopeFilter,
                Limit: Autheris.Application.Catalog.CatalogTableQuery.MaxLimit),
            ct: ct).ConfigureAwait(false);
        var visible = await VisibleTablesAsync(allTables, principal, ct).ConfigureAwait(false);

        var filtered = string.IsNullOrWhiteSpace(domainScope)
            ? visible
            : visible.Where(t => string.Equals(t.Identifier.Domain, domainScope, StringComparison.OrdinalIgnoreCase)).ToList();

        var hiddenText = await HiddenTextColumnsAsync(filtered, principal, ct).ConfigureAwait(false);
        var resources = new List<McpResourceItem>();
        foreach (var t in filtered)
        {
            var hiddenCols = HiddenFor(hiddenText, t);
            resources.Add(RenderGlossary(t, hiddenCols));
            foreach (var col in VisibleColumns(t))
            {
                if (RenderColumnDocs(t, col, hiddenCols) is { } colDoc)
                {
                    resources.Add(colDoc);
                }
            }

            if (await RenderExamplesAsync(t, visible, principal, ct).ConfigureAwait(false) is { } examples)
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
        var allTables = await Autheris.Application.Catalog.CatalogTablePageReader.ReadAllAsync(
            _metadataRepo,
            new Autheris.Application.Catalog.CatalogTableQuery(Limit: Autheris.Application.Catalog.CatalogTableQuery.MaxLimit),
            ct: ct).ConfigureAwait(false);
        var visible = await VisibleTablesAsync(allTables, principal, ct).ConfigureAwait(false);

        // Descriptor items only (no Text): nothing is rendered and no golden query is loaded to list the URIs.
        var hiddenText = await HiddenTextColumnsAsync(visible, principal, ct).ConfigureAwait(false);
        var items = new List<McpResourceItem>();
        foreach (var t in visible.OrderBy(t => t.Identifier.ToString(), StringComparer.Ordinal))
        {
            var domain = t.Identifier.Domain;
            var table = t.Identifier.TableName;
            var hiddenCols = HiddenFor(hiddenText, t);
            items.Add(new McpResourceItem(
                GlossaryUri(t.Identifier), $"{domain}_{table}_glossary",
                $"Business glossary and semantic column definitions for {domain}.{table}", "text/markdown", string.Empty));
            foreach (var col in VisibleColumns(t))
            {
                if (!hiddenCols.Contains(col.ColumnName) && HasColumnDocs(col))
                {
                    items.Add(new McpResourceItem(
                        ColumnDocsUri(t.Identifier, col.ColumnName), $"{table}_{col.ColumnName}_docs",
                        $"Documentation and metadata for {table}.{col.ColumnName}", "text/markdown", string.Empty));
                }
            }

            if (_goldenQueryService != null)
            {
                items.Add(new McpResourceItem(
                    ExamplesUri(t.Identifier), $"{domain}_{table}_golden_queries",
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

        if (!TryParseSemanticUri(uri, out var kind, out var domain, out var schema, out var table, out var column))
        {
            return null;
        }

        // Only the requested table is checked and rendered; unknown and invisible tables answer alike (null).
        if (string.IsNullOrWhiteSpace(table) ||
            table.Length > Autheris.Application.Catalog.CatalogTableQuery.MaxFilterLength ||
            table.AsSpan().IndexOfAny('\0', '\r', '\n') >= 0 ||
            (domain is not null && (domain.Length > Autheris.Application.Catalog.CatalogTableQuery.MaxFilterLength ||
                                    domain.AsSpan().IndexOfAny('\0', '\r', '\n') >= 0)) ||
            (schema is not null && (schema.Length > Autheris.Application.Catalog.CatalogTableQuery.MaxFilterLength ||
                                    schema.AsSpan().IndexOfAny('\0', '\r', '\n') >= 0)))
        {
            return null;
        }

        var candidates = await Autheris.Application.Catalog.CatalogTablePageReader.ReadAllAsync(
            _metadataRepo,
            new Autheris.Application.Catalog.CatalogTableQuery(
                DataSource: domain,
                NamePrefix: table,
                Limit: Autheris.Application.Catalog.CatalogTableQuery.MaxLimit),
            t => string.Equals(t.Identifier.TableName, table, StringComparison.OrdinalIgnoreCase) &&
                 (domain is null || string.Equals(t.Identifier.Domain, domain, StringComparison.OrdinalIgnoreCase)) &&
                 (schema is null || string.Equals(t.Identifier.Schema, schema, StringComparison.OrdinalIgnoreCase)),
            ct: ct).ConfigureAwait(false);
        if (candidates.Count == 0)
        {
            return null;
        }

        var visible = await VisibleTablesAsync(candidates, principal, ct).ConfigureAwait(false);
        IReadOnlyList<TableMetadata>? allVisibleForExamples = null;
        if (kind == SemanticUriKind.Examples && visible.Count > 0)
        {
            var allActive = await Autheris.Application.Catalog.CatalogTablePageReader.ReadAllAsync(
                _metadataRepo,
                new Autheris.Application.Catalog.CatalogTableQuery(
                    IncludeInactive: false,
                    Limit: Autheris.Application.Catalog.CatalogTableQuery.MaxLimit),
                static x => x.Table.IsActive,
                ct: ct).ConfigureAwait(false);
            allVisibleForExamples = await VisibleTablesAsync(allActive, principal, ct).ConfigureAwait(false);
        }

        // A URI without the schema (earlier form) that matches tables of different schemas is ambiguous and answers like an unknown one.
        if (schema is null && visible.Select(t => (t.Identifier.Domain, t.Identifier.Schema)).Distinct().Count() > 1)
        {
            return null;
        }

        var hiddenText = await HiddenTextColumnsAsync(visible, principal, ct).ConfigureAwait(false);
        foreach (var t in visible.OrderBy(t => t.Identifier.ToString(), StringComparer.Ordinal))
        {
            var hiddenCols = HiddenFor(hiddenText, t);
            switch (kind)
            {
                case SemanticUriKind.Glossary:
                    return RenderGlossary(t, hiddenCols);
                case SemanticUriKind.Examples:
                    return await RenderExamplesAsync(t, allVisibleForExamples ?? visible, principal, ct).ConfigureAwait(false);
                default:
                    var col = VisibleColumns(t).FirstOrDefault(c => string.Equals(c.ColumnName, column, StringComparison.OrdinalIgnoreCase));
                    if (col != null && RenderColumnDocs(t, col, hiddenCols) is { } doc)
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

    private static string GlossaryUri(TableIdentifier id) => $"glossary://{id.Domain}/{id.Schema}/{id.TableName}";

    private static string ExamplesUri(TableIdentifier id) => $"examples://{id.Domain}/{id.Schema}/{id.TableName}";

    private static string ColumnDocsUri(TableIdentifier id, string column) =>
        $"dbt://models/{id.Domain}/{id.Schema}/{id.TableName}/columns/{column}/docs";

    /// <summary>
    /// Parses the resource URIs: <c>glossary://domain/schema/table</c>, <c>examples://domain/schema/table</c> and
    /// <c>dbt://models/domain/schema/table/columns/column/docs</c>. The earlier forms without the schema are still accepted (schema is null).
    /// </summary>
    private static bool TryParseSemanticUri(string? uri, out SemanticUriKind kind, out string? domain, out string? schema, out string table, out string? column)
    {
        kind = SemanticUriKind.Glossary;
        domain = null;
        schema = null;
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
            if (parts.Length is not (2 or 3) || parts.Any(string.IsNullOrWhiteSpace))
            {
                return false;
            }

            domain = parts[0];
            schema = parts.Length == 3 ? parts[1] : null;
            table = parts[^1];
            return true;
        }

        const string dbtPrefix = "dbt://models/";
        if (uri.StartsWith(dbtPrefix, StringComparison.OrdinalIgnoreCase) && uri.EndsWith("/docs", StringComparison.OrdinalIgnoreCase))
        {
            var parts = uri[dbtPrefix.Length..^"/docs".Length].Split('/');
            if (parts.Length is not (3 or 5) || parts.Any(string.IsNullOrWhiteSpace) ||
                !string.Equals(parts[^2], "columns", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            kind = SemanticUriKind.ColumnDocs;
            if (parts.Length == 5)
            {
                domain = parts[0];
                schema = parts[1];
            }

            table = parts[^3];
            column = parts[^1];
            return true;
        }

        return false;
    }

    private static readonly IReadOnlySet<string> EmptyColumnSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private static IReadOnlySet<string> HiddenFor(IReadOnlyDictionary<TableIdentifier, IReadOnlySet<string>> hiddenText, TableMetadata t) =>
        hiddenText.TryGetValue(t.Identifier, out var set) ? set : EmptyColumnSet;

    /// <summary>
    /// The columns of each table whose catalog text (description, long description, meta) must not reach the caller: the same rule the
    /// discovery path applies (a column that is not clear for the caller and whose text is hidden, or masked for a restricted scope when
    /// the tenant option is on). Fails closed: when the decision cannot be made every column of the table is treated as hidden.
    /// </summary>
    private async Task<IReadOnlyDictionary<TableIdentifier, IReadOnlySet<string>>> HiddenTextColumnsAsync(
        IReadOnlyList<TableMetadata> tables,
        System.Security.Claims.ClaimsPrincipal? principal,
        CancellationToken ct)
    {
        var result = new Dictionary<TableIdentifier, IReadOnlySet<string>>();
        if (decisionPoint is null || tables.Count == 0 || _options?.Value.IsMcpAuthBypassed == true)
        {
            return result;
        }

        Dictionary<TableIdentifier, Autheris.Domain.Interfaces.TableAccessDecision> byTable = [];
        try
        {
            if (principal is null)
            {
                throw new InvalidOperationException("No caller identity.");
            }

            var decisions = await decisionPoint.DecideManyAsync(
                Autheris.Domain.Security.SecurityPrincipalContext.FromPrincipal(principal, McpCatalogVisibility.TryTenantId(principal)),
                tables,
                Autheris.Application.Policy.Pdp.AccessOperation.Discover,
                Autheris.Application.Policy.Pdp.AccessTransport.Mcp,
                ct).ConfigureAwait(false);
            foreach (var d in decisions ?? [])
            {
                if (d is not null)
                {
                    byTable[d.Table] = d;
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Semantic resource text visibility could not be decided; catalog text of all columns is withheld");
            byTable.Clear();
        }

        var hideForRestrictedScopes = _discoveryOptions?.Value.Discovery?.HideMaskedColumnCatalogTextForRestrictedScopes == true;
        foreach (var t in tables)
        {
            var hidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!byTable.TryGetValue(t.Identifier, out var decision) || !decision.IsAllowed)
            {
                foreach (var col in t.Columns)
                {
                    hidden.Add(col.ColumnName);
                }
            }
            else
            {
                foreach (var col in t.Columns)
                {
                    var (state, _) = Autheris.Application.Catalog.Discovery.DiscoveryAccessMapper.MapColumnState(t, decision, col.ColumnName);
                    if (state != DiscoveryColumnState.Clear &&
                        (decision.IsCatalogTextHidden(col.ColumnName, t) || (hideForRestrictedScopes && decision.ScopeClearance is not null)))
                    {
                        hidden.Add(col.ColumnName);
                    }
                }
            }

            if (hidden.Count > 0)
            {
                result[t.Identifier] = hidden;
            }
        }

        return result;
    }

    /// <summary>
    /// The tables the caller may see: consent, tier and column rules intersected with the catalog visibility service (MCP-14).
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

        // Fail closed when caller identity is missing; only the explicit (production-blocked) auth bypass above opens the catalog.
        if (principal is null)
        {
            return Array.Empty<TableMetadata>();
        }

        if (_consentRepo != null)
        {
            return await McpCatalogVisibility.VisibleTablesAsync(allTables, principal, _consentRepo, _options?.Value, _catalogVisibility, ct).ConfigureAwait(false);
        }

        // A principal without a consent repository sees nothing; a visibility service never opens this case.
        return Array.Empty<TableMetadata>();
    }

    private static List<TableColumn> VisibleColumns(TableMetadata t) => t.Columns.Where(col =>
    {
        if (col.DiscoveryHidden)
        {
            return false;
        }

        if (t.ColumnMaskingRules != null &&
            t.ColumnMaskingRules.TryGetValue(col.ColumnName, out var rule) &&
            string.Equals(rule.RuleType, "DENY", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }).ToList();

    private static McpResourceItem RenderGlossary(TableMetadata t, IReadOnlySet<string> hiddenText)
    {
        var domain = t.Identifier.Domain;
        var table = t.Identifier.TableName;
        var visibleColumns = VisibleColumns(t);

        var glossaryText = $"# Business Glossary: {domain}.{table}\n\n" +
                           (!string.IsNullOrWhiteSpace(t.Table.Description) ? $"**Description**: {t.Table.Description}\n\n" : "") +
                           $"* **Domain**: {domain}\n" +
                           $"* **Table**: {table}\n" +
                           $"* **Sensitivity**: {(ClassificationMetadata.IsTableSensitive(t) ? "Sensitive" : "Standard")}\n" +
                           $"* **Columns**:\n" +
                           string.Join("\n", visibleColumns.Select(c =>
                           {
                               var sensitivityTag = ClassificationMetadata.IsColumnSensitive(t, c) ? " [SENSITIVE/MASKED]" : "";
                               var descTag = !hiddenText.Contains(c.ColumnName) && !string.IsNullOrWhiteSpace(c.Description) ? $": {c.Description}" : "";
                               return $"  - `{c.ColumnName}` ({c.DataType}){sensitivityTag}{descTag}";
                           }));

        return new McpResourceItem(
            Uri: GlossaryUri(t.Identifier),
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

    private static McpResourceItem? RenderColumnDocs(TableMetadata t, TableColumn col, IReadOnlySet<string> hiddenText)
    {
        if (hiddenText.Contains(col.ColumnName))
        {
            return null;
        }

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
        sb.AppendLine($"* **Sensitivity**: {(ClassificationMetadata.IsColumnSensitive(t, col) ? "Sensitive/Masked" : "Standard")}");
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
            Uri: ColumnDocsUri(t.Identifier, col.ColumnName),
            Name: $"{table}_{col.ColumnName}_docs",
            Description: $"Documentation and metadata for {table}.{col.ColumnName}",
            MimeType: "text/markdown",
            Text: sb.ToString().TrimEnd());
    }

    /// <summary>Golden queries / few-shot examples of one table (examples://{domain}/{table}); loaded only when this table is rendered and filtered via caller visibility.</summary>
    private async Task<McpResourceItem?> RenderExamplesAsync(
        TableMetadata t,
        IReadOnlyList<TableMetadata> allVisibleTables,
        System.Security.Claims.ClaimsPrincipal? principal,
        CancellationToken ct)
    {
        if (_goldenQueryService == null)
        {
            return null;
        }

        var domain = t.Identifier.Domain;
        var table = t.Identifier.TableName;
        var callerTenant = principal is null ? (TenantId?)null : McpCatalogVisibility.TryTenantId(principal);
        var candidateSet = await _goldenQueryService.GetCandidateQueriesAsync(callerTenant, domain, table, ct).ConfigureAwait(false);
        if (candidateSet.HasKnownCandidates && candidateSet.Queries.Count == 0)
        {
            return null;
        }

        IReadOnlyList<GoldenQuery> goldens;

        if (_options?.Value.IsMcpAuthBypassed == true)
        {
            goldens = await _goldenQueryService.GetGoldenQueriesAsync(
                GoldenQueryReadContext.AllVisible(callerTenant), domain, table, ct).ConfigureAwait(false);
        }
        else if (principal is null || _consentRepo is null)
        {
            return null;
        }
        else
        {
            var hiddenByTable = new Dictionary<TableIdentifier, IReadOnlySet<string>>();
            foreach (var vt in allVisibleTables)
            {
                var hidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var col in vt.Columns)
                {
                    if (col.DiscoveryHidden ||
                        (vt.ColumnMaskingRules != null &&
                         vt.ColumnMaskingRules.TryGetValue(col.ColumnName, out var rule) &&
                         string.Equals(rule.RuleType, "DENY", StringComparison.OrdinalIgnoreCase)))
                    {
                        hidden.Add(col.ColumnName);
                    }
                }

                if (hidden.Count > 0)
                {
                    hiddenByTable[vt.Identifier] = hidden;
                }
            }

            if (decisionPoint is not null && allVisibleTables.Count > 0)
            {
                try
                {
                    var decisions = await decisionPoint.DecideManyAsync(
                        Autheris.Domain.Security.SecurityPrincipalContext.FromPrincipal(principal, McpCatalogVisibility.TryTenantId(principal)),
                        allVisibleTables,
                        Autheris.Application.Policy.Pdp.AccessOperation.Discover,
                        Autheris.Application.Policy.Pdp.AccessTransport.Mcp,
                        ct).ConfigureAwait(false);
                    foreach (var d in decisions ?? [])
                    {
                        if (d is null) continue;
                        if (!hiddenByTable.TryGetValue(d.Table, out var existing))
                        {
                            existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        }

                        var mergedHidden = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
                        foreach (var col in d.HiddenCatalogTextColumns ?? new HashSet<string>())
                        {
                            if (d.IsCatalogTextHidden(col)) mergedHidden.Add(col);
                        }

                        foreach (var (col, level) in d.ColumnAccess)
                        {
                            if (level == Autheris.Domain.Interfaces.ColumnAccessLevel.Deny) mergedHidden.Add(col);
                        }

                        if (mergedHidden.Count > 0)
                        {
                            hiddenByTable[d.Table] = mergedHidden;
                        }
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
                {
                    return null;
                }
            }

            var visibleSet = allVisibleTables.Select(vt => vt.Identifier).ToHashSet();
            var view = await GoldenQueryCallerView.BuildAsync(
                [new GoldenQueryCallerView.Seed(domain, table, t.Identifier)],
                graphQlCatalogMap,
                (targets, _) => Task.FromResult<IReadOnlySet<TableIdentifier>>(targets.Where(visibleSet.Contains).ToHashSet()),
                (id, _, _, _, _) => Task.FromResult<IReadOnlySet<string>?>(id is { } ident && hiddenByTable.TryGetValue(ident, out var h)
                    ? h
                    : new HashSet<string>(StringComparer.OrdinalIgnoreCase)),
                candidateSet.HasKnownCandidates ? candidateSet.Queries : null,
                ct).ConfigureAwait(false);
            goldens = await _goldenQueryService.GetGoldenQueriesAsync(
                view.ToContext(callerTenant), domain, table, ct).ConfigureAwait(false);
        }

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
            Uri: ExamplesUri(t.Identifier),
            Name: $"{domain}_{table}_golden_queries",
            Description: $"Verified golden GraphQL queries and few-shot examples for {domain}.{table}",
            MimeType: "text/markdown",
            Text: sbExamples.ToString().TrimEnd());
    }

    private static string MapDataTypeToJsonType(string? dataType)
    {
        if (string.IsNullOrWhiteSpace(dataType)) return "string";
        var dt = dataType.Trim().ToLowerInvariant();
        if (dt.Contains("int", StringComparison.Ordinal) || dt.Contains("long", StringComparison.Ordinal) || dt.Contains("serial", StringComparison.Ordinal)) return "integer";
        if (dt.Contains("float", StringComparison.Ordinal) || dt.Contains("double", StringComparison.Ordinal) || dt.Contains("decimal", StringComparison.Ordinal) || dt.Contains("numeric", StringComparison.Ordinal) || dt.Contains("money", StringComparison.Ordinal)) return "number";
        if (dt.Contains("bool", StringComparison.Ordinal)) return "boolean";
        return "string";
    }
}
