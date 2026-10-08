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
    Task<McpDatasetList> ListDatasetsAsync(ClaimsPrincipal principal, string? search, string? domain, CancellationToken ct = default);

    Task<McpDatasetDescription> DescribeDatasetAsync(ClaimsPrincipal principal, string dataset, CancellationToken ct = default);

    Task<McpDatasetSample> SampleRowsAsync(ClaimsPrincipal principal, string dataset, int? count, CancellationToken ct = default);
}
