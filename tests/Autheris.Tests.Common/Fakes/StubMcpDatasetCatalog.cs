using System.Security.Claims;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Domain.Model;

namespace Autheris.Tests.Common.Fakes;

/// <summary>
/// Configurable <see cref="IMcpDatasetCatalog"/> test double for MCP executor and protocol tests.
/// </summary>
public sealed class StubMcpDatasetCatalog : IMcpDatasetCatalog
{
    public static readonly StubMcpDatasetCatalog Empty = new();

    public Func<ClaimsPrincipal, string?, string?, McpDatasetList>? OnList { get; init; }
    public Func<ClaimsPrincipal, string, McpDatasetDescription>? OnDescribe { get; init; }
    public Func<ClaimsPrincipal, string, int?, McpDatasetSample>? OnSample { get; init; }
    public Func<ClaimsPrincipal, string, EffectiveTableAccessDto>? OnDescribeAccess { get; init; }

    public List<(ClaimsPrincipal Principal, string Dataset, int? Count)> SampleCalls { get; } = [];

    public Task<McpDatasetList> ListDatasetsAsync(ClaimsPrincipal principal, string? search, string? domain, CancellationToken ct)
        => ListDatasetsAsync(principal, search, domain, null, null, null, null, ct);

    public Task<McpDatasetList> ListDatasetsAsync(ClaimsPrincipal principal, string? search, string? domain, int? offset, int? limit, CancellationToken ct)
        => ListDatasetsAsync(principal, search, domain, offset, limit, null, null, ct);

    public Task<McpDatasetList> ListDatasetsAsync(ClaimsPrincipal principal, string? search, string? domain, int? offset, int? limit, string? tag, CancellationToken ct)
        => ListDatasetsAsync(principal, search, domain, offset, limit, tag, null, ct);

    public Task<McpDatasetList> ListDatasetsAsync(
        ClaimsPrincipal principal,
        string? search = null,
        string? domain = null,
        int? offset = null,
        int? limit = null,
        string? tag = null,
        string? language = null,
        CancellationToken ct = default)
        => Task.FromResult(OnList is not null ? OnList(principal, search, domain) : new McpDatasetList([], 0, false));

    public Task<McpDatasetDescription> DescribeDatasetAsync(ClaimsPrincipal principal, string dataset, CancellationToken ct = default)
        => DescribeDatasetAsync(principal, dataset, null, ct);

    public Task<McpDatasetDescription> DescribeDatasetAsync(ClaimsPrincipal principal, string dataset, string? language, CancellationToken ct = default)
        => Task.FromResult(OnDescribe is not null
            ? OnDescribe(principal, dataset)
            : new McpDatasetDescription(dataset, dataset, "default", "dbo", dataset, null, null, "INTERNAL", false, [], []));

    public Task<EffectiveTableAccessDto> DescribeMyAccessAsync(ClaimsPrincipal principal, string dataset, CancellationToken ct = default)
        => Task.FromResult(OnDescribeAccess is not null
            ? OnDescribeAccess(principal, dataset)
            : throw new NotSupportedException("DescribeMyAccessAsync not configured."));

    public Task<McpDatasetSample> SampleRowsAsync(ClaimsPrincipal principal, string dataset, int? count, CancellationToken ct = default)
    {
        SampleCalls.Add((principal, dataset, count));
        return Task.FromResult(OnSample is not null
            ? OnSample(principal, dataset, count)
            : new McpDatasetSample(dataset, 0, []));
    }
}
