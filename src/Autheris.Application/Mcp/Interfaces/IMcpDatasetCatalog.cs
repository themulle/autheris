namespace Autheris.Application.Mcp.Interfaces;

using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Model;

/// <summary>
/// Catalog discovery for MCP agents: which datasets the caller may see, what they contain and a few governed sample rows.
/// A dataset the caller may not see is reported exactly like one that does not exist.
/// </summary>
public interface IMcpDatasetCatalog
{
    Task<McpDatasetList> ListDatasetsAsync(ClaimsPrincipal principal, string? search, string? domain, CancellationToken ct);

    Task<McpDatasetList> ListDatasetsAsync(ClaimsPrincipal principal, string? search, string? domain, int? offset, int? limit, CancellationToken ct);

    Task<McpDatasetList> ListDatasetsAsync(ClaimsPrincipal principal, string? search, string? domain, int? offset, int? limit, string? tag, CancellationToken ct);

    Task<McpDatasetList> ListDatasetsAsync(ClaimsPrincipal principal, string? search = null, string? domain = null, int? offset = null, int? limit = null, string? tag = null, string? language = null, CancellationToken ct = default);

    Task<McpDatasetList> ListDatasetsAsync(ClaimsPrincipal principal, string? search, string? domain, int? offset, int? limit, string? tag, string? language, bool? overview, string? format, CancellationToken ct)
        => ListDatasetsAsync(principal, search, domain, offset, limit, tag, language, ct);

    /// <summary>
    /// Issue #798: as above, keeping only datasets the caller has in <paramref name="accessStatus"/> (null: no filter), before the total and
    /// the paging. A catalog without the filter rejects a value instead of ignoring it.
    /// </summary>
    Task<McpDatasetList> ListDatasetsAsync(ClaimsPrincipal principal, string? search, string? domain, int? offset, int? limit, string? tag, string? language, bool? overview, string? format, string? accessStatus, CancellationToken ct)
        => accessStatus is null
            ? ListDatasetsAsync(principal, search, domain, offset, limit, tag, language, overview, format, ct)
            : Task.FromException<McpDatasetList>(new System.NotSupportedException("The accessStatus filter is not supported by this dataset catalog."));

    Task<McpDatasetDescription> DescribeDatasetAsync(ClaimsPrincipal principal, string dataset, CancellationToken ct = default);

    Task<McpDatasetDescription> DescribeDatasetAsync(ClaimsPrincipal principal, string dataset, string? language, CancellationToken ct = default);

    Task<McpDatasetDescription> DescribeDatasetAsync(ClaimsPrincipal principal, string dataset, string? language, bool brief, CancellationToken ct = default)
        => DescribeDatasetAsync(principal, dataset, language, ct);

    Task<EffectiveTableAccessDto> DescribeMyAccessAsync(ClaimsPrincipal principal, string dataset, CancellationToken ct = default);

    Task<McpDatasetSample> SampleRowsAsync(ClaimsPrincipal principal, string dataset, int? count, CancellationToken ct = default);
}
