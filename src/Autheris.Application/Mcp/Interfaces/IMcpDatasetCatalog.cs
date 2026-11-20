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

    /// <summary>
    /// Issue #682: as above, filtering to the dataset with the exact case-sensitive GraphQL root field <paramref name="graphQlField"/>.
    /// </summary>
    Task<McpDatasetList> ListDatasetsAsync(ClaimsPrincipal principal, string? search, string? domain, int? offset, int? limit, string? tag, string? language, bool? overview, string? format, string? accessStatus, string? graphQlField, CancellationToken ct = default)
        => graphQlField is null
            ? ListDatasetsAsync(principal, search, domain, offset, limit, tag, language, overview, format, accessStatus, ct)
            : Task.FromException<McpDatasetList>(new System.NotSupportedException("The graphQlField filter is not supported by this dataset catalog."));

    Task<McpDatasetDescription> DescribeDatasetAsync(ClaimsPrincipal principal, string dataset, CancellationToken ct = default);

    Task<McpDatasetDescription> DescribeDatasetAsync(ClaimsPrincipal principal, string dataset, string? language, CancellationToken ct = default);

    Task<McpDatasetDescription> DescribeDatasetAsync(ClaimsPrincipal principal, string dataset, string? language, bool brief, CancellationToken ct = default)
        => DescribeDatasetAsync(principal, dataset, language, ct);

    /// <summary>Issue #682: describes a dataset by <paramref name="dataset"/> id or exact case-sensitive <paramref name="graphQlField"/>.</summary>
    Task<McpDatasetDescription> DescribeDatasetAsync(ClaimsPrincipal principal, string? dataset, string? graphQlField, string? language, bool brief, CancellationToken ct = default)
        => graphQlField is null
            ? DescribeDatasetAsync(principal, dataset ?? string.Empty, language, brief, ct)
            : Task.FromException<McpDatasetDescription>(new System.NotSupportedException("Lookup by graphQlField is not supported by this dataset catalog."));

    /// <summary>Issue #710: describes a dataset by <paramref name="dataset"/> id, exact case-sensitive <paramref name="graphQlField"/>, or three-part <paramref name="sqlName"/>.</summary>
    Task<McpDatasetDescription> DescribeDatasetAsync(ClaimsPrincipal principal, string? dataset, string? graphQlField, string? sqlName, string? language, bool brief, CancellationToken ct = default)
        => sqlName is null
            ? DescribeDatasetAsync(principal, dataset, graphQlField, language, brief, ct)
            : Task.FromException<McpDatasetDescription>(new System.NotSupportedException("Lookup by sqlName is not supported by this dataset catalog."));

    Task<EffectiveTableAccessDto> DescribeMyAccessAsync(ClaimsPrincipal principal, string dataset, CancellationToken ct = default);

    /// <summary>Issue #682: explains caller access by <paramref name="dataset"/> id or exact case-sensitive <paramref name="graphQlField"/>.</summary>
    Task<EffectiveTableAccessDto> DescribeMyAccessAsync(ClaimsPrincipal principal, string? dataset, string? graphQlField, CancellationToken ct = default)
        => graphQlField is null
            ? DescribeMyAccessAsync(principal, dataset ?? string.Empty, ct)
            : Task.FromException<EffectiveTableAccessDto>(new System.NotSupportedException("Lookup by graphQlField is not supported by this dataset catalog."));

    /// <summary>Issue #710: explains caller access by <paramref name="dataset"/> id, exact case-sensitive <paramref name="graphQlField"/>, or three-part <paramref name="sqlName"/>.</summary>
    Task<EffectiveTableAccessDto> DescribeMyAccessAsync(ClaimsPrincipal principal, string? dataset, string? graphQlField, string? sqlName, CancellationToken ct = default)
        => sqlName is null
            ? DescribeMyAccessAsync(principal, dataset, graphQlField, ct)
            : Task.FromException<EffectiveTableAccessDto>(new System.NotSupportedException("Lookup by sqlName is not supported by this dataset catalog."));

    Task<McpDatasetSample> SampleRowsAsync(ClaimsPrincipal principal, string dataset, int? count, CancellationToken ct = default);
}
