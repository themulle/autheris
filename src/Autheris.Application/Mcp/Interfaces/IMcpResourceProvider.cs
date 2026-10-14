namespace Autheris.Application.Mcp.Interfaces;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Model;

/// <summary>Content of a native MCP resource (SDK-independent).</summary>
public sealed record McpResourceContent(string Uri, string MimeType, string Text);

/// <summary>Raised when a native MCP resource does not exist or is not visible to the caller.</summary>
public sealed class McpResourceNotFoundException : Exception
{
    public McpResourceNotFoundException(string uri)
        : base($"Resource '{uri}' not found.")
    {
        Uri = uri;
    }

    public string Uri { get; }
}

/// <summary>
/// Reads the gateway's native MCP resources (<c>autheris://catalog/*</c>, <c>autheris://governance/*</c>,
/// <c>autheris://api/*</c>) for a caller. The MCP adapter only maps the result to the SDK types.
/// </summary>
public interface IMcpResourceProvider
{
    /// <summary>True if <paramref name="uri"/> belongs to a native resource scheme handled by this provider.</summary>
    bool IsNativeResource(string uri);

    /// <exception cref="McpResourceNotFoundException">The resource does not exist or is not visible to the caller.</exception>
    Task<McpResourceContent> ReadAsync(string uri, McpSessionContext session, CancellationToken ct = default);
}
