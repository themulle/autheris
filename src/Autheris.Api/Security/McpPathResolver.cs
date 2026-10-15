namespace Autheris.Api.Security;

using System;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Http;

/// <summary>
/// Centralizes MCP base path resolution and request path matching across middleware and endpoint wiring (WP-ARC-C-04 / ARC-A-19).
/// </summary>
public interface IMcpPathResolver
{
    string ResolveBasePath(GatewayOptions? options);
    bool IsMcpRequest(PathString path, GatewayOptions? options);
}

public sealed class McpPathResolver : IMcpPathResolver
{
    public static readonly McpPathResolver Instance = new();

    public string ResolveBasePath(GatewayOptions? options) => Resolve(options);

    bool IMcpPathResolver.IsMcpRequest(PathString path, GatewayOptions? options) => IsMcpRequest(path, options);

    public static string Resolve(GatewayOptions? gatewayOptions)
    {
        var raw = gatewayOptions?.Mcp?.EndpointPath;
        var path = string.IsNullOrWhiteSpace(raw)
            ? "/mcp"
            : raw.Trim().TrimEnd('/');
        if (path.Length == 0)
        {
            return "/mcp";
        }

        return path.StartsWith('/') ? path : "/" + path;
    }

    public static bool IsMcpRequest(PathString path, GatewayOptions? options)
    {
        var basePath = Resolve(options);
        return path.StartsWithSegments(basePath, StringComparison.OrdinalIgnoreCase);
    }
}
