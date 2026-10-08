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
    IOptions<GatewayOptions>? options = null) : IMcpDatasetCatalog
{
    public const int MaxListedDatasets = 200;
    public const int DefaultSampleRows = 5;
    public const int MaxSampleRows = 20;

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

        var datasets = matches
            .Take(MaxListedDatasets)
            .Select(t => new McpDatasetSummary(DatasetId(t.Identifier), t.Table.Description, t.Table.Sensitivity, t.Columns.Count))
            .ToList();

        return new McpDatasetList(datasets, matches.Count, matches.Count > datasets.Count);
    }

    public async Task<McpDatasetDescription> DescribeDatasetAsync(ClaimsPrincipal principal, string dataset, CancellationToken ct = default)
    {
        var table = await FindVisibleAsync(principal, dataset, ct).ConfigureAwait(false);
        var id = table.Identifier;

        var primaryKeys = new HashSet<string>(table.PrimaryKeyColumns, StringComparer.OrdinalIgnoreCase);
        var columns = table.Columns
            .Select(c => new McpDatasetColumn(c.ColumnName, c.DataType, c.Description, c.IsSensitive, primaryKeys.Contains(c.ColumnName)))
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
            examples);
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
