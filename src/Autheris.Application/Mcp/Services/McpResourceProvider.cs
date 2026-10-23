namespace Autheris.Application.Mcp.Services;

using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>
/// Default <see cref="IMcpResourceProvider"/>: renders the native catalog, governance and API resources from the
/// catalog discovery and API dispatcher services for the calling session.
/// </summary>
public sealed class McpResourceProvider : IMcpResourceProvider
{
    private const string CatalogSummaryUri = "autheris://catalog/summary";
    private const string CatalogDatasourcesUri = "autheris://catalog/datasources";
    private const string MyAccessUri = "autheris://governance/my-access";
    private const string OpenApiUri = "autheris://api/openapi.json";
    private const string EndpointsDocUri = "autheris://api/docs/endpoints";
    private const string McpToolsDocUri = "autheris://api/docs/mcp-tools";
    private const string SchemaPrefix = "autheris://catalog/datasets/";
    private const string SchemaSuffix = "/schema";
    private const string Markdown = "text/markdown";
    private const string Json = "application/json";

    private readonly ICatalogDiscoveryService _discovery;
    private readonly IApiDispatcherService _dispatcher;

    private readonly IReadPathSwitches? _switches;

    public McpResourceProvider(ICatalogDiscoveryService discovery, IApiDispatcherService dispatcher, IReadPathSwitches? switches = null)
    {
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _switches = switches;
    }

    public bool IsResourceEnabled(string uri) => _switches?.IsResourceEnabled(uri) ?? true;

    private static readonly TableIdentifier DatasourcesTable = new("governance", "catalog", "datasources");
    private static readonly TableIdentifier ApiDescribeTable = new("governance", "api", "describe");
    private static readonly TableIdentifier MyAccessTable = new("mcp", "tool", McpDatasetTools.DescribeMyAccess);

    /// <summary>
    /// The ABAC objects a native resource discloses, the same as the equivalent tool call: the dataset of a schema resource
    /// (<c>get_table_schema</c>), the catalog for the summary (<c>list_datasets</c>), the data sources object (<c>list_datasources</c>),
    /// the self-access object (<c>describe_my_access</c>) and the API description object (<c>describe_api</c>). Null for a URI that names
    /// no native resource (including a schema URI without a valid dataset id): such a read is answered as not found without reading.
    /// </summary>
    public static IReadOnlyList<TableIdentifier>? GuardTargets(string uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
        {
            return null;
        }

        if (Is(uri, CatalogSummaryUri))
        {
            return [McpDatasetTools.CatalogTable];
        }

        if (uri.StartsWith(SchemaPrefix, StringComparison.OrdinalIgnoreCase) && uri.EndsWith(SchemaSuffix, StringComparison.OrdinalIgnoreCase) &&
            uri.Length > SchemaPrefix.Length + SchemaSuffix.Length)
        {
            try
            {
                return McpDatasetCatalog.TryParseDatasetId(uri[SchemaPrefix.Length..^SchemaSuffix.Length], out var tableId) ? [tableId] : null;
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        if (Is(uri, CatalogDatasourcesUri))
        {
            return [DatasourcesTable];
        }

        if (Is(uri, MyAccessUri))
        {
            return [MyAccessTable];
        }

        if (IsStaticDocumentation(uri))
        {
            return [ApiDescribeTable];
        }

        return null;
    }

    /// <summary>
    /// The static API documentation resources (OpenAPI document, endpoint and tool references). They carry no catalog data, so the token
    /// budget, which would cut a JSON document in half, does not apply to them; ABAC, PII scrubbing and audit do.
    /// </summary>
    public static bool IsStaticDocumentation(string uri) =>
        Is(uri, OpenApiUri) || Is(uri, EndpointsDocUri) || Is(uri, McpToolsDocUri);

    public bool IsNativeResource(string uri) =>
        uri.StartsWith("autheris://catalog/", StringComparison.OrdinalIgnoreCase) ||
        uri.StartsWith("autheris://governance/", StringComparison.OrdinalIgnoreCase) ||
        uri.StartsWith("autheris://api/", StringComparison.OrdinalIgnoreCase);

    public async Task<McpResourceContent> ReadAsync(string uri, McpSessionContext session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentNullException.ThrowIfNull(session);

        // PLAN-SEC-115 C-115-6: first statement; a closed resource never reaches a catalog or dispatcher call and answers like an unknown URI.
        if (!IsResourceEnabled(uri))
        {
            throw new McpResourceNotFoundException(uri);
        }

        if (Is(uri, CatalogSummaryUri))
        {
            var datasets = await _discovery.ListDatasetsAsync(BuildRequestContext(session), ct).ConfigureAwait(false);
            var sb = new StringBuilder();
            sb.AppendLine("# Autheris Data Catalog Summary\n");

            if (datasets.Count > 50)
            {
                var domains = datasets.GroupBy(d => d.Domain).OrderByDescending(g => g.Count()).ToList();
                sb.AppendLine($"Total Datasets: {datasets.Count} across {domains.Count} business domains.\n");
                sb.AppendLine("| Domain | Table Count | Sample Tables |");
                sb.AppendLine("|---|---|---|");
                foreach (var g in domains)
                {
                    var sample = string.Join(", ", g.Take(3).Select(d => $"`{McpMarkdown.EscapeCell(d.Table)}`"));
                    sb.AppendLine($"| **{McpMarkdown.EscapeCell(g.Key)}** | {g.Count()} | {sample} |");
                }
                sb.AppendLine("\n> **Tip:** Use `find_relevant_tables` or `search_catalog` to discover tables for a specific use case, or use `get_table_schema` / `describe_dataset(brief: true)` for token-efficient schema retrieval.");
            }
            else
            {
                sb.AppendLine("| Dataset | Domain | Schema | Table | Type | Sensitivity | Description |");
                sb.AppendLine("|---|---|---|---|---|---|---|");
                foreach (var d in datasets)
                {
                    sb.AppendLine($"| `{McpMarkdown.EscapeCell(d.DatasetId)}` | {McpMarkdown.EscapeCell(d.Domain)} | {McpMarkdown.EscapeCell(d.Schema)} | {McpMarkdown.EscapeCell(d.Table)} | {McpMarkdown.EscapeCell(d.SourceType)} | {McpMarkdown.EscapeCell(d.Sensitivity)} | {McpMarkdown.EscapeCell(d.Description)} |");
                }
            }
            return new McpResourceContent(uri, Markdown, sb.ToString());
        }

        if (uri.StartsWith(SchemaPrefix, StringComparison.OrdinalIgnoreCase) && uri.EndsWith(SchemaSuffix, StringComparison.OrdinalIgnoreCase))
        {
            var datasetId = uri[SchemaPrefix.Length..^SchemaSuffix.Length];
            if (McpDatasetCatalog.TryParseDatasetId(datasetId, out var tableId))
            {
                var detail = await _discovery.GetDatasetDetailAsync(tableId, BuildRequestContext(session), ct).ConfigureAwait(false);
                if (detail != null)
                {
                    var sb = new StringBuilder();
                    sb.AppendLine($"# Schema for `{detail.DatasetId}`\n");
                    sb.AppendLine($"- **Domain:** {detail.Domain}");
                    sb.AppendLine($"- **Table:** {detail.Table}");
                    sb.AppendLine($"- **Sensitivity:** {detail.Sensitivity}");
                    sb.AppendLine($"- **Active:** {detail.IsActive}\n");
                    sb.AppendLine("| Column | Type | Sensitivity | Masking | PK | PII |");
                    sb.AppendLine("|---|---|---|---|---|---|");
                    foreach (var c in detail.Columns)
                    {
                        sb.AppendLine($"| `{McpMarkdown.EscapeCell(c.Name)}` | {McpMarkdown.EscapeCell(c.Type)} | {McpMarkdown.EscapeCell(c.Sensitivity)} | {McpMarkdown.EscapeCell(c.MaskingState)} | {(c.IsPrimaryKey ? "✓" : "-")} | {(c.IsPiiIndicator ? "✓" : "-")} |");
                    }
                    return new McpResourceContent(uri, Markdown, sb.ToString());
                }
            }
            throw new McpResourceNotFoundException(uri);
        }

        if (Is(uri, CatalogDatasourcesUri))
        {
            var datasources = await _discovery.ListDatasourcesAsync(BuildRequestContext(session), ct).ConfigureAwait(false);
            var sb = new StringBuilder();
            sb.AppendLine("# Registered Enterprise Datasources\n");
            sb.AppendLine("| Datasource ID | Name | Domain | Type | Status | Configured |");
            sb.AppendLine("|---|---|---|---|---|---|");
            foreach (var ds in datasources)
            {
                sb.AppendLine($"| `{McpMarkdown.EscapeCell(ds.Id)}` | {McpMarkdown.EscapeCell(ds.Name)} | {McpMarkdown.EscapeCell(ds.Domain)} | {McpMarkdown.EscapeCell(ds.Type)} | {McpMarkdown.EscapeCell(ds.Status)} | {(ds.IsConfigured ? "✓" : "-")} |");
            }
            return new McpResourceContent(uri, Markdown, sb.ToString());
        }

        if (Is(uri, MyAccessUri))
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Autheris Governance - Current Session Access Profile\n");
            sb.AppendLine($"- **Principal:** `{session.UserSid ?? session.ServicePrincipalId}`");
            sb.AppendLine($"- **Tenant:** `{session.TenantId}`");
            sb.AppendLine($"- **Roles:** {(session.Roles != null && session.Roles.Count > 0 ? string.Join(", ", session.Roles) : "None")}");
            var groups = EffectiveGroupSids(session);
            sb.AppendLine($"- **Groups:** {(groups.Count > 0 ? string.Join(", ", groups) : "None")}");
            sb.AppendLine($"- **Read-Only:** {session.IsReadOnly}");
            return new McpResourceContent(uri, Markdown, sb.ToString());
        }

        if (Is(uri, OpenApiUri))
        {
            return new McpResourceContent(uri, Json, await _dispatcher.GetOpenApiJsonAsync(ct).ConfigureAwait(false));
        }

        if (Is(uri, EndpointsDocUri))
        {
            return new McpResourceContent(uri, Markdown, await _dispatcher.GetEndpointsDocumentationAsync(ct).ConfigureAwait(false));
        }

        if (Is(uri, McpToolsDocUri))
        {
            return new McpResourceContent(uri, Markdown, await _dispatcher.GetMcpToolsDocumentationAsync(ct).ConfigureAwait(false));
        }

        throw new McpResourceNotFoundException(uri);
    }

    private static bool Is(string uri, string expected) => string.Equals(uri, expected, StringComparison.OrdinalIgnoreCase);

    /// <summary>SR-M-08: an app-only session (no user SID) has no group SIDs, exactly like its principal.</summary>
    private static IReadOnlyList<string> EffectiveGroupSids(McpSessionContext session) =>
        string.IsNullOrWhiteSpace(session.UserSid) ? [] : session.GroupSids ?? [];

    private static RequestContext BuildRequestContext(McpSessionContext session)
    {
        var roles = session.Roles ?? [];
        var context = RequestContext.FromCaller(
            new CallerSecurityContext(
                UserSid: new Sid(!string.IsNullOrWhiteSpace(session.UserSid) ? session.UserSid : session.ServicePrincipalId),
                GroupSids: EffectiveGroupSids(session).Select(g => new Sid(g)).ToArray(),
                Roles: roles,
                Tenant: new TenantId(session.TenantId),
                IsGovernanceAdmin: false,
                IsClusterAdmin: false),
            session.SessionId);

        // The administrator flags above are deliberately false: the discovery service decides the caller class from the principal only, so the
        // principal is built exactly as for the MCP tools. The synthetic anonymous MCP client keeps the empty principal (nothing visible).
        var sid = session.UserSid ?? session.ServicePrincipalId;
        if (string.Equals(sid, AnonymousMcpClientSid, StringComparison.OrdinalIgnoreCase))
        {
            return context;
        }

        return context with { User = McpProtocolHandler.BuildPrincipalFromSession(session)! };
    }

    private const string AnonymousMcpClientSid = "ANONYMOUS_MCP_CLIENT";
}
