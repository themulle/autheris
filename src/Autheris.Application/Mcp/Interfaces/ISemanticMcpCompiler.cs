namespace Autheris.Application.Mcp.Interfaces;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>
/// Semantic MCP compiler fusing dbt documentation and catalog glossaries into AI tool definitions and resources (F-AI-02).
/// </summary>
public interface ISemanticMcpCompiler
{
    /// <summary>
    /// Compiles an MCP tool definition enriched with semantic contracts and compact descriptions.
    /// </summary>
    Task<McpToolDefinition> CompileToolAsync(
        string toolName,
        TableIdentifier targetTable,
        CancellationToken ct = default);

    /// <summary>
    /// Retrieves semantic resources (glossary://, dbt://) for on-demand LLM context grounding with optional user consent filtering.
    /// </summary>
    Task<IReadOnlyList<McpResourceItem>> GetSemanticResourcesAsync(
        string? domainScope = null,
        System.Security.Claims.ClaimsPrincipal? principal = null,
        CancellationToken ct = default);

    /// <summary>
    /// One page of semantic resource descriptors (URI, name, description, MIME type; <c>Text</c> is empty) for the tables the caller may
    /// see, including ReBAC. Nothing is rendered and no golden query is loaded. <paramref name="cursor"/> is the opaque
    /// <see cref="McpResourcePage.NextCursor"/> of the previous page; an invalid cursor yields an empty page.
    /// </summary>
    Task<McpResourcePage> ListSemanticResourceUrisAsync(
        System.Security.Claims.ClaimsPrincipal? principal,
        string? cursor = null,
        int pageSize = 100,
        CancellationToken ct = default);

    /// <summary>
    /// Reads one semantic resource: the URI is parsed, visibility is checked for that table only, and only that resource is rendered.
    /// Null for an unknown URI, an unknown table and an invisible table alike.
    /// </summary>
    Task<McpResourceItem?> ReadSemanticResourceAsync(
        System.Security.Claims.ClaimsPrincipal? principal,
        string uri,
        CancellationToken ct = default);
}

/// <summary>A page of resource descriptors; <see cref="NextCursor"/> is null on the last page.</summary>
public sealed record McpResourcePage(IReadOnlyList<McpResourceItem> Items, string? NextCursor);
