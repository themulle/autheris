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

    public McpResourceProvider(ICatalogDiscoveryService discovery, IApiDispatcherService dispatcher)
    {
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public bool IsNativeResource(string uri) =>
        uri.StartsWith("autheris://catalog/", StringComparison.OrdinalIgnoreCase) ||
        uri.StartsWith("autheris://governance/", StringComparison.OrdinalIgnoreCase) ||
        uri.StartsWith("autheris://api/", StringComparison.OrdinalIgnoreCase);

    public async Task<McpResourceContent> ReadAsync(string uri, McpSessionContext session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentNullException.ThrowIfNull(session);

        if (Is(uri, CatalogSummaryUri))
        {
            var datasets = await _discovery.ListDatasetsAsync(BuildRequestContext(session), ct).ConfigureAwait(false);
            var sb = new StringBuilder();
            sb.AppendLine("# Autheris Data Catalog Summary\n");
            sb.AppendLine("| Dataset | Domain | Schema | Table | Type | Sensitivity | Description |");
            sb.AppendLine("|---|---|---|---|---|---|---|");
            foreach (var d in datasets)
            {
                sb.AppendLine($"| `{d.DatasetId}` | {d.Domain} | {d.Schema} | {d.Table} | {d.SourceType} | {d.Sensitivity} | {d.Description ?? "-"} |");
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
                        sb.AppendLine($"| `{c.Name}` | {c.Type} | {c.Sensitivity} | {c.MaskingState} | {(c.IsPrimaryKey ? "✓" : "-")} | {(c.IsPiiIndicator ? "✓" : "-")} |");
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
                sb.AppendLine($"| `{ds.Id}` | {ds.Name} | {ds.Domain} | {ds.Type} | {ds.Status} | {(ds.IsConfigured ? "✓" : "-")} |");
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
            sb.AppendLine($"- **Groups:** {(session.GroupSids != null && session.GroupSids.Count > 0 ? string.Join(", ", session.GroupSids) : "None")}");
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

    private static RequestContext BuildRequestContext(McpSessionContext session)
    {
        var roles = session.Roles ?? [];
        return RequestContext.FromCaller(
            new CallerSecurityContext(
                UserSid: new Sid(session.UserSid ?? session.ServicePrincipalId),
                GroupSids: (session.GroupSids ?? []).Select(g => new Sid(g)).ToArray(),
                Roles: roles,
                Tenant: new TenantId(session.TenantId),
                IsGovernanceAdmin: roles.Contains("GovernanceAdmin", StringComparer.OrdinalIgnoreCase),
                IsClusterAdmin: roles.Contains("ClusterAdmin", StringComparer.OrdinalIgnoreCase)),
            session.SessionId);
    }
}
