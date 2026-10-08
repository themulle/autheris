namespace Autheris.Application.Mcp.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Microsoft.Extensions.Options;
using Autheris.Domain.Options;
using static Autheris.Application.Mcp.Services.McpDatasetTools;

/// <summary>
/// MCP dataset tools (list_datasets, describe_dataset, sample_rows). Visibility follows the GraphQL catalog
/// (<see cref="McpCatalogVisibility"/>); rows are read only through <see cref="IGatewayExecutionService"/>, so consent,
/// ReBAC, Casbin, row filters and masking apply exactly as for REST and GraphQL.
/// </summary>
public sealed class McpDatasetCatalog(
    ITableMetadataRepository metadataRepository,
    IConsentRepository consentRepository,
    IGatewayExecutionService gatewayExecutionService,
    IGoldenQueryService? goldenQueryService = null,
    IOptions<GatewayOptions>? options = null,
    IGraphQlCatalogMap? graphQlMap = null) : IMcpDatasetCatalog
{
    public const int MaxListedDatasets = 200;
    public const int DefaultSampleRows = 5;
    public const int MaxSampleRows = 20;
    private const int ExampleColumns = 10;

    /// <summary>Returned with every dataset list: GraphQL is the preferred query path.</summary>
    public const string Guidance =
        "Query data with GraphQL: call the query_graphql tool (or POST the query to the GraphQL endpoint). " +
        "describe_dataset returns the GraphQL field, the filter and sort types and an example query for each dataset. " +
        "The other endpoints are listed for clients that need a specific protocol.";

    private readonly ITableMetadataRepository _metadataRepository = metadataRepository ?? throw new ArgumentNullException(nameof(metadataRepository));
    private readonly IConsentRepository _consentRepository = consentRepository ?? throw new ArgumentNullException(nameof(consentRepository));
    private readonly IGatewayExecutionService _gatewayExecutionService = gatewayExecutionService ?? throw new ArgumentNullException(nameof(gatewayExecutionService));

    public async Task<McpDatasetList> ListDatasetsAsync(ClaimsPrincipal principal, string? search, string? domain, CancellationToken ct = default)
    {
        var visible = await VisibleTablesAsync(principal, ct).ConfigureAwait(false);

        var matches = visible
            .Where(t => string.IsNullOrWhiteSpace(domain) || string.Equals(t.Identifier.Domain, domain.Trim(), StringComparison.OrdinalIgnoreCase))
            .Where(t => string.IsNullOrWhiteSpace(search) || Matches(t, search.Trim()))
            .OrderBy(t => DatasetId(t.Identifier), StringComparer.Ordinal)
            .ToList();

        var datasets = new List<McpDatasetSummary>();
        foreach (var t in matches.Take(MaxListedDatasets))
        {
            var graphQl = graphQlMap == null ? null : await graphQlMap.GetTableAsync(t.Identifier, ct).ConfigureAwait(false);
            datasets.Add(new McpDatasetSummary(DatasetId(t.Identifier), t.Table.Description, t.Table.Sensitivity, t.Columns.Count, graphQl?.QueryField));
        }

        return new McpDatasetList(datasets, matches.Count, matches.Count > datasets.Count, Guidance, Endpoints());
    }

    public async Task<McpDatasetDescription> DescribeDatasetAsync(ClaimsPrincipal principal, string dataset, CancellationToken ct = default)
    {
        var table = await FindVisibleAsync(principal, dataset, ct).ConfigureAwait(false);
        var id = table.Identifier;

        var graphQl = graphQlMap == null ? null : await graphQlMap.GetTableAsync(id, ct).ConfigureAwait(false);
        var graphQlColumns = (graphQl?.Columns ?? []).ToDictionary(c => c.ColumnName, StringComparer.OrdinalIgnoreCase);

        var primaryKeys = new HashSet<string>(table.PrimaryKeyColumns, StringComparer.OrdinalIgnoreCase);
        var columns = table.Columns
            .Select(c =>
            {
                graphQlColumns.TryGetValue(c.ColumnName, out var field);
                return new McpDatasetColumn(c.ColumnName, c.DataType, c.Description, c.IsSensitive, primaryKeys.Contains(c.ColumnName), field?.Field, field?.GraphQlType);
            })
            .ToList();

        IReadOnlyList<McpDatasetExample> examples = [];
        if (goldenQueryService != null)
        {
            var goldens = await goldenQueryService.GetGoldenQueriesAsync(id.Domain, id.TableName, ct).ConfigureAwait(false);
            examples = goldens
                .Select(g => new McpDatasetExample(g.Title, g.Description, g.QueryText, g.VariablesJson))
                .ToList();
        }

        return new McpDatasetDescription(
            DatasetId(id),
            id.Domain,
            id.Schema,
            id.TableName,
            table.Table.Description,
            table.Table.LongDescription,
            table.Table.Sensitivity,
            table.Table.RequiresFourEyes,
            columns,
            examples,
            graphQl == null ? null : await GraphQlAccessAsync(principal, graphQl, columns, ct).ConfigureAwait(false),
            DatasetAccess(id));
    }

    public async Task<McpDatasetSample> SampleRowsAsync(ClaimsPrincipal principal, string dataset, int? count, CancellationToken ct = default)
    {
        var table = await FindVisibleAsync(principal, dataset, ct).ConfigureAwait(false);
        var rowCount = Math.Clamp(count ?? DefaultSampleRows, 1, MaxSampleRows);

        var (rows, decision) = await _gatewayExecutionService.ExecuteTableQueryAsync(
            principal,
            table.Identifier,
            first: rowCount,
            after: 0,
            queryArguments: null,
            requestedFields: null,
            requestHeaders: null,
            ct: ct).ConfigureAwait(false);

        if (!decision.IsAllowed)
        {
            throw new GatewayForbiddenException("Access denied by data governance policy.");
        }

        return new McpDatasetSample(DatasetId(table.Identifier), rows.Count, rows);
    }

    private string GraphQlEndpoint => options?.Value.GraphQL.EndpointPath is { Length: > 0 } path ? path : "/graphql";

    private async Task<McpGraphQlAccess> GraphQlAccessAsync(
        ClaimsPrincipal principal,
        GraphQlTableMapping graphQl,
        IReadOnlyList<McpDatasetColumn> columns,
        CancellationToken ct)
    {
        // Relations are offered only to datasets the caller can see, so hidden table names do not leak.
        var visibleIds = (await VisibleTablesAsync(principal, ct).ConfigureAwait(false))
            .Select(t => DatasetId(t.Identifier))
            .ToHashSet(StringComparer.Ordinal);
        var relations = graphQl.Relations
            .Select(r => new McpGraphQlRelation(r.Field, DatasetId(r.Target), r.IsList))
            .Where(r => visibleIds.Contains(r.Dataset))
            .ToList();

        var exampleFields = columns.Where(c => c.GraphQlField != null).Take(ExampleColumns).Select(c => c.GraphQlField);
        var example = $"query {{ {graphQl.QueryField}(first: 10) {{ {string.Join(' ', exampleFields)} }} }}";
        var arguments =
            $"where: {graphQl.FilterType} – per field eq, neq, gt, gte, lt, lte, in, nin, contains, startsWith, endsWith, isNull; " +
            $"combine with and/or (lists) and not. orderBy: [{graphQl.OrderByType}] – {{ field: ASC | DESC }}. " +
            "first: Int (default 100), offset: Int. Always pass a small first: queries above the complexity budget are rejected. " +
            "Filter and sort only on columns you can read in clear text.";

        return new McpGraphQlAccess(GraphQlEndpoint, graphQl.QueryField, graphQl.TypeName, graphQl.FilterType, graphQl.OrderByType, arguments, example, relations);
    }

    /// <summary>The non-GraphQL ways to read one dataset.</summary>
    private IReadOnlyList<McpAccessPath> DatasetAccess(TableIdentifier id)
    {
        var o = options?.Value ?? new GatewayOptions();
        var paths = new List<McpAccessPath>
        {
            new("OData v4", "GET", $"/odata/v4/{id.Domain}/{id.Schema}/{id.TableName}",
                "OData query options $select, $filter, $orderby, $top, $skip. JSON with OData annotations."),
            new("OpenAPI (REST)", "GET", $"/odata/v4/{id.Domain}/openapi.json",
                "OpenAPI 3.1 description of the REST routes of this domain."),
            new("MCP sample_rows", "MCP", SampleRows, "A few rows of this dataset, for a first look.")
        };

        if (o.Arrow.Enabled)
        {
            paths.Add(new("Arrow IPC export", "POST", "/api/v1/export/arrow",
                $"Body {{\"table\": \"{id.Domain}.{id.Schema}.{id.TableName}\"}}: columnar bulk export (application/vnd.apache.arrow.stream)."));
        }

        return paths;
    }

    /// <summary>All enabled protocols of the gateway, GraphQL first.</summary>
    private IReadOnlyList<McpAccessPath> Endpoints()
    {
        var o = options?.Value ?? new GatewayOptions();
        var endpoints = new List<McpAccessPath>
        {
            new("GraphQL", "POST", GraphQlEndpoint,
                "Preferred query path: query, filter, sort, page and follow relations between datasets in one request. Use the query_graphql tool.",
                Preferred: true),
            new("OData v4", "GET", "/odata/v4",
                "Service document; /odata/v4/$metadata is the CSDL schema, /odata/v4/{domain}/{schema}/{table} the entity sets."),
            new("OpenAPI (REST)", "GET", "/api/v1/openapi/index", "Index of the OpenAPI 3.1 documents per domain.")
        };

        if (o.WebSql.Enabled)
        {
            endpoints.Add(new("WebSQL", "POST", "/api/v1/sql",
                "Body {\"sql\", \"parameters\", \"dataSource\"}: governed SQL in Trino syntax with 3-part names (<catalog>.<schema>.<table>) or 2-part names (<schema>.<table>)."));
            endpoints.Add(new("Trino Protocol", "POST", "/v1/statement",
                "Standard Trino REST Client protocol: query in body or JSON, X-Trino-Wait-Timeout for sync/async execution, continuation via /v1/statement/queued/{id}."));
        }

        if (o.SqlEndpoints.Enabled)
        {
            endpoints.Add(new("Saved SQL queries", "GET", "/api/v1/queries", "Lists the curated SQL endpoints; call one with GET or POST /api/v1/queries/{name}."));
        }

        if (o.SqlEndpoints.Procedures.Enabled)
        {
            endpoints.Add(new("Stored procedures", "GET", "/api/v1/procedures", "Lists the published procedures; call one with GET or POST /api/v1/procedures/{name}."));
        }

        if (o.DuckDbOlap.Enabled)
        {
            endpoints.Add(new("DuckDB OLAP", "POST", "/api/v1/olap/query",
                "Body {\"sql\", \"tableNames\", \"limit\"}: analytical SQL across several datasets."));
        }

        if (o.Arrow.Enabled)
        {
            endpoints.Add(new("Arrow IPC export", "POST", "/api/v1/export/arrow", "Columnar bulk export of one dataset."));
        }

        return endpoints;
    }

    /// <summary>Dataset ids use the same <c>domain.schema.table</c> form as ReBAC objects.</summary>
    public static string DatasetId(TableIdentifier id) => $"{id.Domain}.{id.Schema}.{id.TableName}".ToLowerInvariant();

    /// <summary>Parses a <c>domain.schema.table</c> dataset id.</summary>
    public static bool TryParseDatasetId(string? dataset, out TableIdentifier id)
    {
        id = default;
        var parts = (dataset ?? string.Empty).Trim().Split('.');
        if (parts.Length != 3 || parts.Any(string.IsNullOrWhiteSpace))
        {
            return false;
        }

        id = new TableIdentifier(parts[0], parts[1], parts[2]);
        return true;
    }

    private async Task<TableMetadata> FindVisibleAsync(ClaimsPrincipal principal, string dataset, CancellationToken ct)
    {
        if (!TryParseDatasetId(dataset, out var requested))
        {
            throw new ArgumentException("The dataset id must have the form 'domain.schema.table'.", nameof(dataset));
        }

        var visible = await VisibleTablesAsync(principal, ct).ConfigureAwait(false);
        var requestedId = DatasetId(requested);

        // A table the caller may not see is reported exactly like one that does not exist (no enumeration oracle).
        return visible.FirstOrDefault(t => string.Equals(DatasetId(t.Identifier), requestedId, StringComparison.Ordinal))
            ?? throw new TableNotFoundException(requested);
    }

    private async Task<IReadOnlyList<TableMetadata>> VisibleTablesAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        var allTables = await _metadataRepository.GetAllTablesAsync(ct).ConfigureAwait(false);
        // SEC H-02: only the explicit (production-blocked) MCP auth bypass opens the whole catalog.
        return options?.Value.IsMcpAuthBypassed == true
            ? allTables
            : await McpCatalogVisibility.VisibleTablesAsync(allTables, principal, _consentRepository, ct).ConfigureAwait(false);
    }

    private static bool Matches(TableMetadata table, string search) =>
        DatasetId(table.Identifier).Contains(search, StringComparison.OrdinalIgnoreCase) ||
        (table.Table.Description?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
        table.Columns.Any(c =>
            c.ColumnName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            (c.Description?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false));
}
