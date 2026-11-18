namespace Autheris.Application.Docs.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Discovery;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Docs.Interfaces;
using Autheris.Application.Tenancy;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;

/// <summary>Caps of the linked-document output of one discovery object.</summary>
public static class DocLinkDiscoveryLimits
{
    public const int MaxDocumentsPerObject = 10;
    public const int MaxDocumentsPerColumn = 5;
    public const int SummaryRunes = 300;
    public const int TitleRunes = 200;
}

/// <summary>Linked documents and applications of one discovery object, already filtered for the caller.</summary>
public sealed record DocLinkView(
    IReadOnlyList<DiscoveryDocumentRef> Documents,
    bool DocumentsTruncated,
    int DocumentCount,
    IReadOnlyList<DiscoveryApplicationRef> Applications,
    IReadOnlyDictionary<string, IReadOnlyList<DiscoveryDocumentRef>> ColumnDocuments)
{
    /// <summary>Returns <paramref name="obj"/> with the linked documents and applications attached; columns the caller cannot see stay untouched.</summary>
    public DiscoveryObject ApplyTo(DiscoveryObject obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        var columns = ColumnDocuments.Count == 0
            ? obj.Columns
            : obj.Columns.Select(c => c.State != DiscoveryColumnState.NoAccess && ColumnDocuments.TryGetValue(c.Name, out var docs) ? c with { Documents = docs } : c).ToList();
        return obj with
        {
            Columns = columns,
            Documents = Documents,
            Applications = Applications,
            DocumentsTruncated = DocumentsTruncated,
            DocumentCount = DocumentCount
        };
    }
}

/// <summary>
/// Resolves the documents and applications linked to catalog objects for one caller. A link is returned only when the document
/// is readable (official or the caller's tenant), is not hidden from discovery, and its target is discoverable for the caller.
/// Everything else is omitted silently.
/// </summary>
public interface IDocLinkDiscoveryReader
{
    /// <summary>Visible links of <paramref name="tables"/>; tables without a visible link are absent from the result.</summary>
    Task<IReadOnlyDictionary<TableIdentifier, DocLinkView>> ForTablesAsync(string tenantId, IReadOnlyList<TableMetadata> tables, CancellationToken ct = default);

    /// <summary>
    /// Keys of the documents linked to <paramref name="table"/> (or to <paramref name="column"/> of it) that the caller may see;
    /// <c>null</c> when the target is not discoverable for the caller.
    /// </summary>
    Task<IReadOnlySet<DocKey>?> DocumentsLinkedToAsync(string tenantId, TableIdentifier table, string? column, CancellationToken ct = default);

    /// <summary>The discoverable targets (as <c>domain.schema.table[.column]</c>) of the document <paramref name="key"/>.</summary>
    Task<IReadOnlyList<string>> VisibleTargetsOfAsync(string tenantId, DocKey key, CancellationToken ct = default);
}

public sealed class DocLinkDiscoveryReader : IDocLinkDiscoveryReader
{
    private readonly ITableMetadataRepository _metadata;
    private readonly IDocLinkRepository _links;
    private readonly IDocumentRepository _documents;
    private readonly IUniversalDocStore _store;
    private readonly IOptions<GatewayOptions> _gatewayOptions;

    public DocLinkDiscoveryReader(
        ITableMetadataRepository metadata,
        IDocLinkRepository links,
        IDocumentRepository documents,
        IUniversalDocStore store,
        IOptions<GatewayOptions> gatewayOptions)
    {
        _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        _links = links ?? throw new ArgumentNullException(nameof(links));
        _documents = documents ?? throw new ArgumentNullException(nameof(documents));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _gatewayOptions = gatewayOptions ?? throw new ArgumentNullException(nameof(gatewayOptions));
    }

    public async Task<IReadOnlyDictionary<TableIdentifier, DocLinkView>> ForTablesAsync(string tenantId, IReadOnlyList<TableMetadata> tables, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tables);
        var result = new Dictionary<TableIdentifier, DocLinkView>();
        if (string.IsNullOrWhiteSpace(tenantId)) return result;
        var visible = tables.Where(static t => t.Table.IsActive && !t.Table.DiscoveryHidden).ToDictionary(static t => t.Identifier);
        if (visible.Count == 0) return result;

        var records = new List<DocLinkRecord>();
        foreach (var batch in visible.Keys.Chunk(DocLinkLimits.MaxTargetsPerSource))
        {
            var targets = batch.Select(static id => new DocLinkTarget(DocLinkTargetKind.Table, id.Domain, id.Schema, id.TableName)).ToList();
            records.AddRange(await _links.ListForTargetsAsync(tenantId, targets, ct).ConfigureAwait(false));
        }

        if (records.Count == 0) return result;
        var docCache = new Dictionary<DocKey, DocRecord?>();
        IReadOnlyList<DocAppSummary>? apps = null;
        foreach (var group in records.GroupBy(static r => new TableIdentifier(r.Target.Domain, r.Target.Schema, r.Target.Name)))
        {
            if (!visible.TryGetValue(group.Key, out var table)) continue;
            var tableDocs = new Dictionary<DocKey, DiscoveryDocumentRef>();
            var columnDocs = new Dictionary<string, Dictionary<DocKey, DiscoveryDocumentRef>>(StringComparer.OrdinalIgnoreCase);
            var applications = new Dictionary<string, DiscoveryApplicationRef>(StringComparer.OrdinalIgnoreCase);
            foreach (var link in group)
            {
                if (link.Target.Kind == DocLinkTargetKind.Procedure) continue;
                var column = link.Target.Kind == DocLinkTargetKind.Column
                    ? table.Columns.FirstOrDefault(c => string.Equals(c.ColumnName, link.Target.Column, StringComparison.OrdinalIgnoreCase))
                    : null;
                if (link.Target.Kind == DocLinkTargetKind.Column && (column is null || column.DiscoveryHidden)) continue;
                if (link.Source.Kind == DocLinkSourceKind.Application)
                {
                    if (column is not null || applications.ContainsKey(link.Source.AppId)) continue;
                    apps ??= await _store.ListApplicationsAsync(DocAccessPolicy.ResolveDiscoveryReader(tenantId), ct: ct).ConfigureAwait(false);
                    var app = apps.FirstOrDefault(a => string.Equals(a.AppId, link.Source.AppId, StringComparison.OrdinalIgnoreCase));
                    if (app is not null)
                    {
                        applications[app.AppId] = new DiscoveryApplicationRef(app.AppId, DiscoveryTextSanitizer.SanitizeText(app.Name, DocLinkDiscoveryLimits.TitleRunes), app.DocumentCount);
                    }

                    continue;
                }

                var key = KeyOf(link.Source);
                if (key is null) continue;
                var reference = await ResolveRefAsync(tenantId, key, docCache, ct).ConfigureAwait(false);
                if (reference is null) continue;
                if (column is null)
                {
                    tableDocs[key] = reference;
                }
                else
                {
                    if (!columnDocs.TryGetValue(column.ColumnName, out var perColumn)) columnDocs[column.ColumnName] = perColumn = [];
                    perColumn[key] = reference;
                }
            }

            if (tableDocs.Count == 0 && columnDocs.Count == 0 && applications.Count == 0) continue;
            var ordered = Newest(tableDocs.Values);
            result[group.Key] = new DocLinkView(
                ordered.Take(DocLinkDiscoveryLimits.MaxDocumentsPerObject).ToList(),
                ordered.Count > DocLinkDiscoveryLimits.MaxDocumentsPerObject,
                ordered.Count,
                applications.Values.OrderBy(static a => a.AppId, StringComparer.Ordinal).ToList(),
                columnDocs.ToDictionary(
                    static kv => kv.Key,
                    static kv => (IReadOnlyList<DiscoveryDocumentRef>)Newest(kv.Value.Values).Take(DocLinkDiscoveryLimits.MaxDocumentsPerColumn).ToList(),
                    StringComparer.OrdinalIgnoreCase));
        }

        return result;
    }

    public async Task<IReadOnlySet<DocKey>?> DocumentsLinkedToAsync(string tenantId, TableIdentifier table, string? column, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) return null;
        var target = new DocLinkTarget(column is null ? DocLinkTargetKind.Table : DocLinkTargetKind.Column, table.Domain, table.Schema, table.TableName, column);
        if (await ResolveDiscoverableAsync(tenantId, target, ct).ConfigureAwait(false) is not { } resolved) return null;
        var links = await _links.ListForTargetsAsync(tenantId, [new DocLinkTarget(DocLinkTargetKind.Table, table.Domain, table.Schema, table.TableName)], ct).ConfigureAwait(false);
        var keys = new HashSet<DocKey>();
        var cache = new Dictionary<DocKey, DocRecord?>();
        foreach (var link in links)
        {
            if (link.Source.Kind != DocLinkSourceKind.Document || KeyOf(link.Source) is not { } key) continue;
            var matches = column is null
                ? link.Target.Kind != DocLinkTargetKind.Column
                : link.Target.Kind == DocLinkTargetKind.Column && string.Equals(link.Target.Column, resolved.Column, StringComparison.OrdinalIgnoreCase);
            if (!matches) continue;
            if (await ResolveRefAsync(tenantId, key, cache, ct).ConfigureAwait(false) is not null) keys.Add(key);
        }

        return keys;
    }

    public async Task<IReadOnlyList<string>> VisibleTargetsOfAsync(string tenantId, DocKey key, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) return [];
        var links = await _links.ListForSourceAsync(tenantId, DocLinkSource.ForDocument(key), ct).ConfigureAwait(false);
        var targets = new List<string>();
        foreach (var link in links.Take(DocLinkLimits.MaxTargetsPerSource))
        {
            if (link.Target.Kind == DocLinkTargetKind.Procedure) continue;
            if (await ResolveDiscoverableAsync(tenantId, link.Target, ct).ConfigureAwait(false) is not { } resolved) continue;
            targets.Add($"{resolved.Id.Domain}.{resolved.Id.Schema}.{resolved.Id.TableName}{(resolved.Column is null ? string.Empty : "." + resolved.Column)}");
        }

        return targets;
    }

    private async Task<DiscoveryDocumentRef?> ResolveRefAsync(string tenantId, DocKey key, Dictionary<DocKey, DocRecord?> cache, CancellationToken ct)
    {
        if (key.TenantId is not null && !string.Equals(key.TenantId, tenantId, StringComparison.Ordinal)) return null;
        if (!cache.TryGetValue(key, out var record))
        {
            record = await _documents.GetAsync(key, ct).ConfigureAwait(false);
            cache[key] = record;
        }

        if (record is null || record.IsDeleted || record.DiscoveryHidden) return null;
        var official = record.TenantId is null;
        var title = DiscoveryTextSanitizer.SanitizeText(record.Title, DocLinkDiscoveryLimits.TitleRunes) ?? record.DocId;
        var summary = DiscoveryTextSanitizer.SanitizeText(record.Content, DocLinkDiscoveryLimits.SummaryRunes);
        return new DiscoveryDocumentRef(
            record.AppId, record.Category.ToString(), record.DocId, title, string.IsNullOrEmpty(summary) ? null : summary,
            official, official ? "official" : "untrusted", record.UpdatedAt);
    }

    private async Task<(TableIdentifier Id, string? Column)?> ResolveDiscoverableAsync(string tenantId, DocLinkTarget target, CancellationToken ct)
    {
        if (target.Kind == DocLinkTargetKind.Procedure) return null;
        if (!TableIdentifier.TryCreate(target.Domain, target.Schema, target.Name, out var id)) return null;
        TenantId tenant;
        try
        {
            tenant = new TenantId(tenantId);
        }
        catch (ArgumentException)
        {
            return null;
        }

        var table = await _metadata.GetTableMetadataAsync(id, ct).ConfigureAwait(false);
        if (table is null || !table.Table.IsActive || table.Table.DiscoveryHidden) return null;
        var catalogOwner = TenantResolver.EffectiveDefaultOf(_gatewayOptions.Value.Tenancy);
        var shared = DiscoveryTenantScope.ParseSharedTables(_gatewayOptions.Value.Catalog.SharedTables);
        if (DiscoveryTenantScope.DropForeignVirtualTables([table], tenant).Count == 0 ||
            !DiscoveryTenantScope.IsInScope(table, tenant, catalogOwner, shared, new HashSet<TableIdentifier>())) return null;
        if (target.Kind != DocLinkTargetKind.Column) return (table.Identifier, null);
        var col = table.Columns.FirstOrDefault(c => string.Equals(c.ColumnName, target.Column, StringComparison.OrdinalIgnoreCase));
        return col is null || col.DiscoveryHidden ? null : (table.Identifier, col.ColumnName);
    }

    private static DocKey? KeyOf(DocLinkSource source) =>
        source.Kind == DocLinkSourceKind.Document && source.Category is { } category && source.DocId is { } docId
            ? new DocKey(source.DocTenantId, source.AppId, category, docId)
            : null;

    private static List<DiscoveryDocumentRef> Newest(IEnumerable<DiscoveryDocumentRef> refs) =>
        refs.OrderByDescending(static r => r.UpdatedAt)
            .ThenBy(static r => r.AppId, StringComparer.Ordinal)
            .ThenBy(static r => r.DocId, StringComparer.Ordinal)
            .ToList();
}
