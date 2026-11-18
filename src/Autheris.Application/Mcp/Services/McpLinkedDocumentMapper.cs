namespace Autheris.Application.Mcp.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autheris.Application.Catalog.Discovery;
using Autheris.Domain.Model;

/// <summary>Maps the linked documents of a discovery object to MCP output under the per-result payload cap.</summary>
internal sealed record McpLinkedDocuments(
    IReadOnlyList<McpLinkedDocument>? Documents,
    IReadOnlyList<McpLinkedApplication>? Applications,
    bool Truncated,
    IReadOnlyDictionary<string, IReadOnlyList<McpLinkedDocument>> ColumnDocuments)
{
    private const int RefOverheadBytes = 160;

    /// <summary>Total budget of all linked-document refs of one MCP tool result.</summary>
    public const int MaxPayloadBytes = 64 * 1024;

    public static McpLinkedDocuments From(DiscoveryObject obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        var budget = MaxPayloadBytes;
        var truncated = obj.DocumentsTruncated;
        var docs = Take(obj.Documents, ref budget, ref truncated);
        var columns = new Dictionary<string, IReadOnlyList<McpLinkedDocument>>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in DiscoveryColumnVisibility.VisibleColumns(obj).Where(static c => c.Documents.Count > 0))
        {
            var taken = Take(column.Documents, ref budget, ref truncated);
            if (taken.Count > 0) columns[column.Name] = taken;
        }

        var apps = obj.Applications.Select(static a => new McpLinkedApplication(a.AppId, a.Name, a.DocumentCount)).ToList();
        return new McpLinkedDocuments(docs.Count == 0 ? null : docs, apps.Count == 0 ? null : apps, truncated, columns);
    }

    private static List<McpLinkedDocument> Take(IReadOnlyList<DiscoveryDocumentRef> refs, ref int budget, ref bool truncated)
    {
        var result = new List<McpLinkedDocument>(refs.Count);
        foreach (var r in refs)
        {
            var cost = RefOverheadBytes + Encoding.UTF8.GetByteCount(r.Title) + (r.Summary is null ? 0 : Encoding.UTF8.GetByteCount(r.Summary));
            if (cost > budget)
            {
                truncated = true;
                break;
            }

            budget -= cost;
            result.Add(new McpLinkedDocument(
                r.AppId, r.Category, r.DocId, r.Title, r.Summary, r.Official,
                r.Official ? DocTrustLevel.Official : DocTrustLevel.UntrustedUserContent, r.UpdatedAt));
        }

        return result;
    }
}
