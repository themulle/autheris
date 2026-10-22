namespace Autheris.GraphQL.Mcp;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.GraphQL.Catalog;
using HotChocolate.Language;

/// <summary>
/// MCP view of the catalog GraphQL schema. Names come from <see cref="CatalogSchemaModel"/>, the same model the schema is
/// generated from, so they match the live <c>/graphql</c> API. The model is rebuilt at most every few seconds.
/// </summary>
public sealed class CatalogGraphQlMap(
    ITableMetadataRepository metadataRepository,
    ITableRelationRepository relationRepository) : IGraphQlCatalogMap
{
    private static readonly TimeSpan ModelLifetime = TimeSpan.FromSeconds(30);
    private const int MaxSelectionDepth = 32;
    private const int MaxVisitedSelections = 10_000;
    private const int MaxDocumentLength = 64 * 1024;
    private const string RootContext = "<root>";

    private readonly ITableMetadataRepository _metadataRepository = metadataRepository ?? throw new ArgumentNullException(nameof(metadataRepository));
    private readonly ITableRelationRepository _relationRepository = relationRepository ?? throw new ArgumentNullException(nameof(relationRepository));
    private (CatalogSchemaModel Model, DateTime BuiltAtUtc)? _cached;

    public async Task<GraphQlTableMapping?> GetTableAsync(TableIdentifier table, CancellationToken ct = default)
    {
        var model = await ModelAsync(ct).ConfigureAwait(false);
        var entry = model.Tables.FirstOrDefault(t => SameTable(t.Identifier, table));
        if (entry == null)
        {
            return null;
        }

        return new GraphQlTableMapping(
            entry.Identifier,
            entry.QueryFieldName,
            entry.TypeName,
            entry.FilterTypeName,
            entry.OrderByTypeName,
            entry.Columns.Select(c => new GraphQlColumnMapping(c.ColumnName, c.FieldName, CatalogGraphQlTypeModule.GetScalarName(c.FieldType))).ToList(),
            entry.Relations.Select(r => new GraphQlRelationMapping(r.FieldName, r.TargetTableIdentifier, r.IsList)).ToList());
    }

    public async Task<IReadOnlyList<TableIdentifier>?> ResolveDocumentTablesAsync(string document, CancellationToken ct = default)
    {
        // MCP-11: bounded before parsing; an oversized document cannot be resolved, so the guardrail denies the call.
        if (document is null || document.Length > MaxDocumentLength)
        {
            return null;
        }

        DocumentNode doc;
        try
        {
            doc = Utf8GraphQLParser.Parse(document);
        }
        catch (Exception)
        {
            return null;
        }

        var operations = doc.Definitions.OfType<OperationDefinitionNode>().ToList();
        if (operations.Count != 1 || operations[0].Operation != OperationType.Query)
        {
            return null;
        }

        var model = await ModelAsync(ct).ConfigureAwait(false);
        var fragments = doc.Definitions.OfType<FragmentDefinitionNode>().ToDictionary(f => f.Name.Value, StringComparer.Ordinal);
        var walk = new Walk(model, fragments);
        return CollectRoot(operations[0].SelectionSet, walk, depth: 0) ? walk.Tables.ToList() : null;
    }

    /// <summary>
    /// State of one document walk (MCP-11). A fragment is expanded once per context (root or one table type): the tables it reaches
    /// are already in <see cref="Tables"/>, so a second spread adds nothing. A fragment that is still being expanded is a cycle.
    /// The number of visited selections is capped, so a document cannot cost more than a fixed amount of work.
    /// </summary>
    private sealed class Walk(CatalogSchemaModel model, Dictionary<string, FragmentDefinitionNode> fragments)
    {
        private readonly HashSet<(string Fragment, string Context)> _done = [];
        private readonly HashSet<(string Fragment, string Context)> _inProgress = [];
        private int _visited;

        public CatalogSchemaModel Model { get; } = model;

        public HashSet<TableIdentifier> Tables { get; } = [];

        /// <summary>Counts a selection; false once the cap is exceeded.</summary>
        public bool Visit() => ++_visited <= MaxVisitedSelections;

        /// <summary>Runs <paramref name="expand"/> once per fragment and context; false for an unknown fragment, a cycle or a failure.</summary>
        public bool Spread(string name, string context, Func<FragmentDefinitionNode, bool> expand)
        {
            if (!fragments.TryGetValue(name, out var fragment))
            {
                return false;
            }

            var key = (name, context);
            if (_done.Contains(key))
            {
                return true;
            }

            if (!_inProgress.Add(key))
            {
                return false;
            }

            var ok = expand(fragment);
            _inProgress.Remove(key);
            if (ok)
            {
                _done.Add(key);
            }

            return ok;
        }
    }

    private static bool CollectRoot(SelectionSetNode set, Walk walk, int depth)
    {
        if (depth > MaxSelectionDepth)
        {
            return false;
        }

        foreach (var selection in set.Selections)
        {
            if (!walk.Visit())
            {
                return false;
            }

            switch (selection)
            {
                case FieldNode field when field.Name.Value.StartsWith("__", StringComparison.Ordinal):
                    // Introspection (__schema, __type, __typename) reads no table.
                    break;
                case FieldNode field:
                    if (!walk.Model.TablesByQueryFieldName.TryGetValue(field.Name.Value, out var table))
                    {
                        return false;
                    }

                    walk.Tables.Add(table.Identifier);
                    if (field.SelectionSet != null && !CollectTable(field.SelectionSet, table, walk, depth + 1))
                    {
                        return false;
                    }

                    break;
                case InlineFragmentNode inline:
                    if (!CollectRoot(inline.SelectionSet, walk, depth + 1))
                    {
                        return false;
                    }

                    break;
                case FragmentSpreadNode spread:
                    if (!walk.Spread(spread.Name.Value, RootContext, fragment => CollectRoot(fragment.SelectionSet, walk, depth + 1)))
                    {
                        return false;
                    }

                    break;
                default:
                    return false;
            }
        }

        return true;
    }

    private static bool CollectTable(SelectionSetNode set, CatalogTableType table, Walk walk, int depth)
    {
        if (depth > MaxSelectionDepth)
        {
            return false;
        }

        foreach (var selection in set.Selections)
        {
            if (!walk.Visit())
            {
                return false;
            }

            switch (selection)
            {
                case FieldNode field:
                    var relation = table.Relations.FirstOrDefault(r => string.Equals(r.FieldName, field.Name.Value, StringComparison.Ordinal));
                    if (relation == null)
                    {
                        // Column or __typename: no further table.
                        break;
                    }

                    if (!walk.Model.TablesByIdentifier.TryGetValue(relation.TargetTableIdentifier, out var target))
                    {
                        return false;
                    }

                    walk.Tables.Add(target.Identifier);
                    if (field.SelectionSet != null && !CollectTable(field.SelectionSet, target, walk, depth + 1))
                    {
                        return false;
                    }

                    break;
                case InlineFragmentNode inline:
                    if (!CollectTable(inline.SelectionSet, table, walk, depth + 1))
                    {
                        return false;
                    }

                    break;
                case FragmentSpreadNode spread:
                    if (!walk.Spread(spread.Name.Value, table.Identifier.ToString(), fragment => CollectTable(fragment.SelectionSet, table, walk, depth + 1)))
                    {
                        return false;
                    }

                    break;
                default:
                    return false;
            }
        }

        return true;
    }

    private async Task<CatalogSchemaModel> ModelAsync(CancellationToken ct)
    {
        var cached = _cached;
        if (cached is { } c && DateTime.UtcNow - c.BuiltAtUtc < ModelLifetime)
        {
            return c.Model;
        }

        var model = await CatalogSchemaModel.BuildAsync(_metadataRepository, _relationRepository, null, ct).ConfigureAwait(false);
        _cached = (model, DateTime.UtcNow);
        return model;
    }

    private static bool SameTable(TableIdentifier a, TableIdentifier b) =>
        string.Equals(a.Domain, b.Domain, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.Schema, b.Schema, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.TableName, b.TableName, StringComparison.OrdinalIgnoreCase);
}
