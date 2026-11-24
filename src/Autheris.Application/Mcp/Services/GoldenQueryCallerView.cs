namespace Autheris.Application.Mcp.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
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
    private readonly IReadOnlyDictionary<string, DocumentNode>? _parsedDocuments;

    private GoldenQueryCallerView(
        HashSet<(string, string)> visible,
        Dictionary<(string, string), GraphQlTableMapping> mappings,
        Dictionary<(string, string), IReadOnlySet<string>> hidden,
        IReadOnlyDictionary<string, DocumentNode>? parsedDocuments = null)
    {
        _visible = visible;
        _mappings = mappings;
        _hidden = hidden;
        _parsedDocuments = parsedDocuments;
    }

    /// <summary>A dataset to start from; <paramref name="Mapping"/> is optional and loaded through the catalog map when null.</summary>
    public readonly record struct Seed(string Domain, string Table, TableIdentifier? Id = null, GraphQlTableMapping? Mapping = null);

    /// <summary>Number of datasets resolved into this caller view (diagnostics only).</summary>
    internal int ResolvedDatasetCount => _visible.Count;

    /// <param name="seeds">Datasets that are visible to the caller and may own golden queries.</param>
    /// <param name="map">GraphQL catalog map; without it no relation can be resolved and only the owning dataset is checked.</param>
    /// <param name="filterVisible">The subset of relation targets that is visible to the caller.</param>
    /// <param name="hiddenColumnsOf">Column names of a dataset that the caller must not see; <c>null</c> marks the dataset unusable (not visible).</param>
    /// <param name="ct">Cancellation token.</param>
    public static Task<GoldenQueryCallerView> BuildAsync(
        IEnumerable<Seed> seeds,
        IGraphQlCatalogMap? map,
        Func<IReadOnlyList<TableIdentifier>, CancellationToken, Task<IReadOnlySet<TableIdentifier>>> filterVisible,
        Func<TableIdentifier?, string, string, GraphQlTableMapping?, CancellationToken, Task<IReadOnlySet<string>?>> hiddenColumnsOf,
        CancellationToken ct) =>
        BuildAsync(seeds, map, filterVisible, hiddenColumnsOf, candidateQueries: null, ct);

    /// <summary>
    /// Builds a caller view scoped to relation paths referenced by <paramref name="candidateQueries"/> when non-null, avoiding
    /// blind graph walks over unrelated relations in densely connected schemas while keeping full closure verification fail-closed.
    /// </summary>
    public static Task<GoldenQueryCallerView> BuildAsync(
        IEnumerable<Seed> seeds,
        IGraphQlCatalogMap? map,
        Func<IReadOnlyList<TableIdentifier>, CancellationToken, Task<IReadOnlySet<TableIdentifier>>> filterVisible,
        Func<TableIdentifier?, string, string, GraphQlTableMapping?, CancellationToken, Task<IReadOnlySet<string>?>> hiddenColumnsOf,
        IReadOnlyCollection<GoldenQuery>? candidateQueries,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(seeds);
        ArgumentNullException.ThrowIfNull(filterVisible);
        ArgumentNullException.ThrowIfNull(hiddenColumnsOf);

        return candidateQueries is null
            ? BuildUnscopedAsync(seeds, map, filterVisible, hiddenColumnsOf, ct)
            : BuildForCandidatesAsync(seeds, map, filterVisible, hiddenColumnsOf, candidateQueries, ct);
    }

    private static async Task<GoldenQueryCallerView> BuildUnscopedAsync(
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
                var mapping = await ResolveSeedAsync(seed, key, map, hiddenColumnsOf, visible, mappings, hidden, ct).ConfigureAwait(false);
                if (mapping is null)
                {
                    continue;
                }

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

    private static async Task<GoldenQueryCallerView> BuildForCandidatesAsync(
        IEnumerable<Seed> seeds,
        IGraphQlCatalogMap? map,
        Func<IReadOnlyList<TableIdentifier>, CancellationToken, Task<IReadOnlySet<TableIdentifier>>> filterVisible,
        Func<TableIdentifier?, string, string, GraphQlTableMapping?, CancellationToken, Task<IReadOnlySet<string>?>> hiddenColumnsOf,
        IReadOnlyCollection<GoldenQuery> candidateQueries,
        CancellationToken ct)
    {
        if (candidateQueries.Count == 0)
        {
            return new GoldenQueryCallerView([], [], []);
        }

        var queriesByOwner = new Dictionary<(string, string), List<GoldenQuery>>();
        var parsedDocuments = new Dictionary<string, DocumentNode>(StringComparer.Ordinal);
        foreach (var q in candidateQueries)
        {
            var ownerKey = Key(q.Domain, q.TableName);
            if (!queriesByOwner.TryGetValue(ownerKey, out var list))
            {
                list = [];
                queriesByOwner[ownerKey] = list;
            }

            list.Add(q);
            if (!string.IsNullOrWhiteSpace(q.QueryText) && !parsedDocuments.ContainsKey(q.QueryText))
            {
                try
                {
                    parsedDocuments[q.QueryText] = Utf8GraphQLParser.Parse(q.QueryText);
                }
                catch (Exception ex) when (ex is SyntaxException or ArgumentException or InvalidOperationException)
                {
                    // Non-parseable queries are handled identically by Referenced(GoldenQuery).
                }
            }
        }

        var seedList = seeds.Where(s => queriesByOwner.ContainsKey(Key(s.Domain, s.Table))).ToList();
        if (seedList.Count == 0)
        {
            return new GoldenQueryCallerView([], [], [], parsedDocuments);
        }

        var visible = new HashSet<(string, string)>();
        var mappings = new Dictionary<(string, string), GraphQlTableMapping>();
        var hidden = new Dictionary<(string, string), IReadOnlySet<string>>();

        var pendingWork = new Queue<((string Domain, string Table) DatasetKey, SelectionSetNode Set, int Depth)>();
        var deferredWork = new Dictionary<(string, string), List<(SelectionSetNode Set, int Depth)>>();
        var expandedSelections = new HashSet<((string Domain, string Table) Key, SelectionSetNode Set)>(SelectionWorkComparer.Instance);
        var attemptedTargets = new HashSet<(string, string)>(seedList.Select(s => Key(s.Domain, s.Table)));

        foreach (var seed in seedList)
        {
            if (visible.Count >= MaxDatasets)
            {
                break;
            }

            var ownerKey = Key(seed.Domain, seed.Table);
            var mapping = await ResolveSeedAsync(seed, ownerKey, map, hiddenColumnsOf, visible, mappings, hidden, ct).ConfigureAwait(false);
            if (mapping is null || !queriesByOwner.TryGetValue(ownerKey, out var ownerQueries))
            {
                continue;
            }

            EnqueueOwnerRootSelections(ownerQueries, parsedDocuments, ownerKey, mapping, seed.Table, pendingWork);
        }

        while (pendingWork.Count > 0)
        {
            var nextTargets = new List<TableIdentifier>();
            var queuedTargetKeys = new HashSet<(string, string)>();

            while (pendingWork.Count > 0)
            {
                var (datasetKey, set, depth) = pendingWork.Dequeue();
                if (depth > MaxDepth || !mappings.TryGetValue(datasetKey, out var dsMap))
                {
                    continue;
                }

                if (!expandedSelections.Add((datasetKey, set)))
                {
                    continue;
                }

                var relations = dsMap.Relations;
                if (relations.Count == 0)
                {
                    continue;
                }

                foreach (var selection in set.Selections)
                {
                    if (selection is not FieldNode field)
                    {
                        continue;
                    }

                    var name = field.Name.Value;
                    if (string.Equals(name, "__typename", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (dsMap.Columns.Any(c =>
                            string.Equals(c.Field, name, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(c.ColumnName, name, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    var relation = relations.FirstOrDefault(r => string.Equals(r.Field, name, StringComparison.OrdinalIgnoreCase));
                    if (relation is null)
                    {
                        continue;
                    }

                    var targetKey = Key(relation.Target.Domain, relation.Target.TableName);
                    if (attemptedTargets.Add(targetKey) && queuedTargetKeys.Add(targetKey))
                    {
                        nextTargets.Add(relation.Target);
                    }

                    if (field.SelectionSet is not null && depth + 1 <= MaxDepth)
                    {
                        if (mappings.ContainsKey(targetKey))
                        {
                            pendingWork.Enqueue((targetKey, field.SelectionSet, depth + 1));
                        }
                        else if (visible.Count < MaxDatasets)
                        {
                            if (!deferredWork.TryGetValue(targetKey, out var deferredList))
                            {
                                deferredList = [];
                                deferredWork[targetKey] = deferredList;
                            }

                            deferredList.Add((field.SelectionSet, depth + 1));
                        }
                    }
                }
            }

            if (nextTargets.Count == 0 || visible.Count >= MaxDatasets)
            {
                break;
            }

            var visibleTargets = await filterVisible(nextTargets, ct).ConfigureAwait(false);
            foreach (var target in nextTargets)
            {
                if (!visibleTargets.Contains(target))
                {
                    continue;
                }

                if (visible.Count >= MaxDatasets)
                {
                    break;
                }

                var targetKey = Key(target.Domain, target.TableName);
                var resolvedMapping = await ResolveSeedAsync(
                    new Seed(target.Domain, target.TableName, target),
                    targetKey,
                    map,
                    hiddenColumnsOf,
                    visible,
                    mappings,
                    hidden,
                    ct).ConfigureAwait(false);

                if (resolvedMapping is not null && deferredWork.Remove(targetKey, out var readySelections))
                {
                    foreach (var (childSet, childDepth) in readySelections)
                    {
                        pendingWork.Enqueue((targetKey, childSet, childDepth));
                    }
                }
            }
        }

        return new GoldenQueryCallerView(visible, mappings, hidden, parsedDocuments);
    }

    private static async Task<GraphQlTableMapping?> ResolveSeedAsync(
        Seed seed,
        (string, string) key,
        IGraphQlCatalogMap? map,
        Func<TableIdentifier?, string, string, GraphQlTableMapping?, CancellationToken, Task<IReadOnlySet<string>?>> hiddenColumnsOf,
        HashSet<(string, string)> visible,
        Dictionary<(string, string), GraphQlTableMapping> mappings,
        Dictionary<(string, string), IReadOnlySet<string>> hidden,
        CancellationToken ct)
    {
        var mapping = seed.Mapping;
        if (mapping is null && map is not null && seed.Id is { } id)
        {
            mapping = await map.GetTableAsync(id, ct).ConfigureAwait(false);
        }

        var hiddenColumns = await hiddenColumnsOf(seed.Id, seed.Domain, seed.Table, mapping, ct).ConfigureAwait(false);
        if (hiddenColumns is null)
        {
            return null;
        }

        visible.Add(key);
        var byColumn = mapping?.Columns.ToDictionary(c => c.ColumnName, c => c, StringComparer.OrdinalIgnoreCase);
        hidden[key] = GoldenQueryService.BuildHiddenIdentifierSet(hiddenColumns, byColumn);
        if (mapping is not null)
        {
            mappings[key] = mapping;
        }

        return mapping;
    }

    private static void EnqueueOwnerRootSelections(
        List<GoldenQuery> ownerQueries,
        Dictionary<string, DocumentNode> parsedDocuments,
        (string, string) ownerKey,
        GraphQlTableMapping ownerMapping,
        string ownerTableName,
        Queue<((string Domain, string Table) DatasetKey, SelectionSetNode Set, int Depth)> pendingWork)
    {
        foreach (var query in ownerQueries)
        {
            if (!parsedDocuments.TryGetValue(query.QueryText, out var document))
            {
                continue;
            }

            foreach (var definition in document.Definitions)
            {
                if (definition is not OperationDefinitionNode { Operation: OperationType.Query } operation)
                {
                    continue;
                }

                foreach (var selection in operation.SelectionSet.Selections)
                {
                    if (selection is FieldNode { SelectionSet: { } rootSet } root &&
                        (string.Equals(root.Name.Value, ownerMapping.QueryField, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(root.Name.Value, query.TableName, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(root.Name.Value, ownerTableName, StringComparison.OrdinalIgnoreCase)))
                    {
                        pendingWork.Enqueue((ownerKey, rootSet, 1));
                    }
                }
            }
        }
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

        DocumentNode? document;
        if (_parsedDocuments is not null && _parsedDocuments.TryGetValue(query.QueryText, out var cachedDocument))
        {
            document = cachedDocument;
        }
        else
        {
            try
            {
                document = Utf8GraphQLParser.Parse(query.QueryText);
            }
            catch (Exception ex) when (ex is SyntaxException or ArgumentException or InvalidOperationException)
            {
                // Configured entries may be administrator-authored SQL or non-GraphQL text; stored entries fail closed.
                return query.FromStore ? null : [];
            }
        }

        if (!_mappings.TryGetValue(ownerKey, out var owner))
        {
            // Without a mapping of the owner no relation can be resolved: stored entries only allow flat selections.
            return !query.FromStore || IsFlatQuery(document) ? [] : null;
        }

        var reached = new Dictionary<(string, string), (string Domain, string Table)>();
        foreach (var definition in document.Definitions)
        {
            if (definition is not OperationDefinitionNode { Operation: OperationType.Query } operation)
            {
                return query.FromStore ? null : [];
            }

            foreach (var selection in operation.SelectionSet.Selections)
            {
                if (selection is not FieldNode root ||
                    !(string.Equals(root.Name.Value, owner.QueryField, StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(root.Name.Value, query.TableName, StringComparison.OrdinalIgnoreCase)))
                {
                    return query.FromStore ? null : [];
                }

                var outcome = Walk(ownerKey, root.SelectionSet, 1, reached);
                if (outcome == WalkOutcome.PolicyDenied)
                {
                    return null;
                }

                if (outcome == WalkOutcome.Unresolved)
                {
                    return query.FromStore ? null : [];
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

    private enum WalkOutcome
    {
        Ok,
        Unresolved,
        PolicyDenied
    }

    private WalkOutcome Walk(
        (string, string) datasetKey,
        SelectionSetNode? set,
        int depth,
        Dictionary<(string, string), (string Domain, string Table)> reached)
    {
        if (set is null)
        {
            return WalkOutcome.Ok;
        }

        if (depth > MaxDepth || !_mappings.TryGetValue(datasetKey, out var mapping))
        {
            return WalkOutcome.Unresolved;
        }

        _hidden.TryGetValue(datasetKey, out var hiddenIds);
        foreach (var selection in set.Selections)
        {
            if (selection is not FieldNode field)
            {
                return WalkOutcome.Unresolved;
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
                    return WalkOutcome.PolicyDenied;
                }

                continue;
            }

            var relation = mapping.Relations.FirstOrDefault(r => string.Equals(r.Field, name, StringComparison.OrdinalIgnoreCase));
            if (relation is null)
            {
                return WalkOutcome.Unresolved;
            }

            var targetKey = Key(relation.Target.Domain, relation.Target.TableName);
            if (!_visible.Contains(targetKey))
            {
                return WalkOutcome.PolicyDenied;
            }

            if (!_mappings.ContainsKey(targetKey))
            {
                return WalkOutcome.Unresolved;
            }

            reached[targetKey] = (relation.Target.Domain, relation.Target.TableName);
            var childOutcome = Walk(targetKey, field.SelectionSet, depth + 1, reached);
            if (childOutcome != WalkOutcome.Ok)
            {
                return childOutcome;
            }
        }

        return WalkOutcome.Ok;
    }

    private static (string, string) Key(string domain, string table) => (domain.ToLowerInvariant(), table.ToLowerInvariant());

    private sealed class SelectionWorkComparer : IEqualityComparer<((string Domain, string Table) Key, SelectionSetNode Set)>
    {
        public static SelectionWorkComparer Instance { get; } = new();

        public bool Equals(((string Domain, string Table) Key, SelectionSetNode Set) x, ((string Domain, string Table) Key, SelectionSetNode Set) y) =>
            string.Equals(x.Key.Domain, y.Key.Domain, StringComparison.Ordinal) &&
            string.Equals(x.Key.Table, y.Key.Table, StringComparison.Ordinal) &&
            ReferenceEquals(x.Set, y.Set);

        public int GetHashCode(((string Domain, string Table) Key, SelectionSetNode Set) obj) =>
            HashCode.Combine(
                StringComparer.Ordinal.GetHashCode(obj.Key.Domain),
                StringComparer.Ordinal.GetHashCode(obj.Key.Table),
                RuntimeHelpers.GetHashCode(obj.Set));
    }
}
