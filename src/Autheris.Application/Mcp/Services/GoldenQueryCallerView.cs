namespace Autheris.Application.Mcp.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using HotChocolate.Language;

/// <summary>
/// The caller's view of the datasets that golden queries can reach: which datasets are visible, which identifiers of each dataset are
/// hidden (Deny, discovery-hidden or hidden catalog text, including GraphQL field aliases) and the GraphQL relation mappings.
/// One builder serves every golden query consumer (<c>describe_dataset</c> and <c>get_golden_queries</c>), so both apply the same rules.
/// At read time every selection of an entry is walked transitively over the relation mappings: every reached dataset must be visible, no
/// selected field may be a hidden identifier of the dataset it belongs to and a field that cannot be resolved drops the entry.
/// </summary>
public sealed class GoldenQueryCallerView
{
    /// <summary>Upper bound of datasets loaded per view; the walk depth is bounded likewise.</summary>
    public const int MaxDatasets = 500;
    public const int MaxDepth = 12;

    private readonly HashSet<(string, string)> _visible;
    private readonly Dictionary<(string, string), GraphQlTableMapping> _mappings;
    private readonly Dictionary<(string, string), IReadOnlySet<string>> _hidden;

    private GoldenQueryCallerView(
        HashSet<(string, string)> visible,
        Dictionary<(string, string), GraphQlTableMapping> mappings,
        Dictionary<(string, string), IReadOnlySet<string>> hidden)
    {
        _visible = visible;
        _mappings = mappings;
        _hidden = hidden;
    }

    /// <summary>A dataset to start from; <paramref name="Mapping"/> is optional and loaded through the catalog map when null.</summary>
    public readonly record struct Seed(string Domain, string Table, TableIdentifier? Id = null, GraphQlTableMapping? Mapping = null);

    /// <param name="seeds">Datasets that are visible to the caller and may own golden queries.</param>
    /// <param name="map">GraphQL catalog map; without it no relation can be resolved and only the owning dataset is checked.</param>
    /// <param name="filterVisible">The subset of relation targets that is visible to the caller.</param>
    /// <param name="hiddenColumnsOf">Column names of a dataset that the caller must not see; <c>null</c> marks the dataset unusable (not visible).</param>
    public static async Task<GoldenQueryCallerView> BuildAsync(
        IEnumerable<Seed> seeds,
        IGraphQlCatalogMap? map,
        Func<IReadOnlyList<TableIdentifier>, CancellationToken, Task<IReadOnlySet<TableIdentifier>>> filterVisible,
        Func<TableIdentifier?, string, string, GraphQlTableMapping?, CancellationToken, Task<IReadOnlySet<string>?>> hiddenColumnsOf,
        CancellationToken ct)
    {
        var visible = new HashSet<(string, string)>();
        var mappings = new Dictionary<(string, string), GraphQlTableMapping>();
        var hidden = new Dictionary<(string, string), IReadOnlySet<string>>();

        var frontier = new List<Seed>(seeds);
        var seen = new HashSet<(string, string)>(frontier.Select(s => Key(s.Domain, s.Table)));
        for (var depth = 0; frontier.Count > 0 && depth <= MaxDepth && mappings.Count + visible.Count < MaxDatasets * 2; depth++)
        {
            var next = new List<TableIdentifier>();
            foreach (var seed in frontier)
            {
                if (visible.Count >= MaxDatasets)
                {
                    // Beyond the cap a dataset stays unresolved and invisible: stored entries of it are dropped (fail closed).
                    break;
                }

                var key = Key(seed.Domain, seed.Table);
                var mapping = seed.Mapping;
                if (mapping is null && map is not null && seed.Id is { } id)
                {
                    mapping = await map.GetTableAsync(id, ct).ConfigureAwait(false);
                }

                var hiddenColumns = await hiddenColumnsOf(seed.Id, seed.Domain, seed.Table, mapping, ct).ConfigureAwait(false);
                if (hiddenColumns is null)
                {
                    continue;
                }

                visible.Add(key);
                var byColumn = mapping?.Columns.ToDictionary(c => c.ColumnName, c => c, StringComparer.OrdinalIgnoreCase);
                hidden[key] = GoldenQueryService.BuildHiddenIdentifierSet(hiddenColumns, byColumn);
                if (mapping is null)
                {
                    continue;
                }

                mappings[key] = mapping;
                foreach (var relation in mapping.Relations)
                {
                    if (seen.Add(Key(relation.Target.Domain, relation.Target.TableName)))
                    {
                        next.Add(relation.Target);
                    }
                }
            }

            if (next.Count == 0)
            {
                break;
            }

            var visibleTargets = await filterVisible(next, ct).ConfigureAwait(false);
            frontier = next.Where(visibleTargets.Contains).Select(t => new Seed(t.Domain, t.TableName, t)).ToList();
        }

        return new GoldenQueryCallerView(visible, mappings, hidden);
    }

    /// <summary>The read context for <paramref name="tenant"/>; a <c>null</c> tenant reads no stored entries.</summary>
    public GoldenQueryReadContext ToContext(TenantId? tenant) => new(
        tenant,
        IsDatasetVisible: (d, t) => _visible.Contains(Key(d, t)),
        HiddenIdentifiers: (d, t) => _hidden.TryGetValue(Key(d, t), out var set) ? set : null,
        ReferencedDatasets: Referenced);

    /// <summary>
    /// The datasets other than the owner that <paramref name="query"/> reaches; <c>null</c> when the entry cannot be proven safe (it does not parse,
    /// has fragments, selects a hidden field or reaches an invisible or unresolvable dataset). Stored entries are always verified: with a mapping of the owner by the full walk, without one only a flat selection is served.
    /// </summary>
    private IEnumerable<(string Domain, string Table)>? Referenced(GoldenQuery query)
    {
        var ownerKey = Key(query.Domain, query.TableName);
        // Configured entries are administrator-authored and may use any shape; they keep the identifier match of the read context.
        if (!query.FromStore)
        {
            return [];
        }

        DocumentNode document;
        try
        {
            document = Utf8GraphQLParser.Parse(query.QueryText);
        }
        catch (Exception ex) when (ex is SyntaxException or ArgumentException or InvalidOperationException)
        {
            return null;
        }

        if (!_mappings.TryGetValue(ownerKey, out var owner))
        {
            // Without a mapping of the owner no relation can be resolved: only a flat selection of the owner's own fields may be served.
            return IsFlatQuery(document) ? [] : null;
        }

        var reached = new Dictionary<(string, string), (string Domain, string Table)>();
        foreach (var definition in document.Definitions)
        {
            if (definition is not OperationDefinitionNode { Operation: OperationType.Query } operation)
            {
                return null;
            }

            foreach (var selection in operation.SelectionSet.Selections)
            {
                if (selection is not FieldNode root ||
                    !(string.Equals(root.Name.Value, owner.QueryField, StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(root.Name.Value, query.TableName, StringComparison.OrdinalIgnoreCase)) ||
                    !Walk(ownerKey, root.SelectionSet, 1, reached))
                {
                    return null;
                }
            }
        }

        return reached.Values.ToList();
    }

    private static bool IsFlatQuery(DocumentNode document)
    {
        foreach (var definition in document.Definitions)
        {
            if (definition is not OperationDefinitionNode { Operation: OperationType.Query } operation)
            {
                return false;
            }

            foreach (var selection in operation.SelectionSet.Selections)
            {
                if (selection is not FieldNode { SelectionSet: { } fields } ||
                    fields.Selections.Any(f => f is not FieldNode { SelectionSet: null }))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private bool Walk(
        (string, string) datasetKey,
        SelectionSetNode? set,
        int depth,
        Dictionary<(string, string), (string Domain, string Table)> reached)
    {
        if (set is null)
        {
            return true;
        }

        if (depth > MaxDepth || !_mappings.TryGetValue(datasetKey, out var mapping))
        {
            return false;
        }

        _hidden.TryGetValue(datasetKey, out var hiddenIds);
        foreach (var selection in set.Selections)
        {
            if (selection is not FieldNode field)
            {
                return false;
            }

            var name = field.Name.Value;
            if (string.Equals(name, "__typename", StringComparison.Ordinal))
            {
                continue;
            }

            var column = mapping.Columns.FirstOrDefault(c =>
                string.Equals(c.Field, name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(c.ColumnName, name, StringComparison.OrdinalIgnoreCase));
            if (column is not null)
            {
                if (hiddenIds is not null && (hiddenIds.Contains(name) || hiddenIds.Contains(column.ColumnName) || hiddenIds.Contains(column.Field)))
                {
                    return false;
                }

                continue;
            }

            var relation = mapping.Relations.FirstOrDefault(r => string.Equals(r.Field, name, StringComparison.OrdinalIgnoreCase));
            if (relation is null)
            {
                return false;
            }

            var targetKey = Key(relation.Target.Domain, relation.Target.TableName);
            if (!_visible.Contains(targetKey) || !_mappings.ContainsKey(targetKey))
            {
                return false;
            }

            reached[targetKey] = (relation.Target.Domain, relation.Target.TableName);
            if (!Walk(targetKey, field.SelectionSet, depth + 1, reached))
            {
                return false;
            }
        }

        return true;
    }

    private static (string, string) Key(string domain, string table) => (domain.ToLowerInvariant(), table.ToLowerInvariant());
}
