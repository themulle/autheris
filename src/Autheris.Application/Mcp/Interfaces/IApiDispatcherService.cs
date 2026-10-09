namespace Autheris.Application.Mcp.Interfaces;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Model;

/// <summary>
/// Dynamic API dispatcher and endpoint documentation service for MCP tools (describe_api, invoke_api) and resources.
/// </summary>
public interface IApiDispatcherService
{
    Task<string> DescribeApiAsync(string endpoint, string? method = null, CancellationToken ct = default);

    Task<string> InvokeApiAsync(
        string endpoint,
        string method,
        IReadOnlyDictionary<string, object?>? parameters = null,
        object? body = null,
        RequestContext? context = null,
        CancellationToken ct = default);

    Task<string> GetOpenApiJsonAsync(CancellationToken ct = default);

    Task<string> GetEndpointsDocumentationAsync(CancellationToken ct = default);

    Task<string> GetMcpToolsDocumentationAsync(CancellationToken ct = default);
}
