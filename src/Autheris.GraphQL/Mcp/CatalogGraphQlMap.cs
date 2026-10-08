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
        var tables = new List<TableIdentifier>();
        return CollectRoot(operations[0].SelectionSet, model, fragments, tables, depth: 0) ? tables.Distinct().ToList() : null;
    }

    private static bool CollectRoot(
        SelectionSetNode set,
        CatalogSchemaModel model,
        Dictionary<string, FragmentDefinitionNode> fragments,
        List<TableIdentifier> tables,
        int depth)
    {
        if (depth > MaxSelectionDepth)
        {
            return false;
        }

        foreach (var selection in set.Selections)
        {
            switch (selection)
            {
                case FieldNode field when field.Name.Value.StartsWith("__", StringComparison.Ordinal):
                    // Introspection (__schema, __type, __typename) reads no table.
                    break;
                case FieldNode field:
                    if (!model.TablesByQueryFieldName.TryGetValue(field.Name.Value, out var table))
                    {
                        return false;
                    }

                    tables.Add(table.Identifier);
                    if (field.SelectionSet != null && !CollectTable(field.SelectionSet, table, model, fragments, tables, depth + 1))
                    {
                        return false;
                    }

                    break;
                case InlineFragmentNode inline:
                    if (!CollectRoot(inline.SelectionSet, model, fragments, tables, depth + 1))
                    {
                        return false;
                    }

                    break;
                case FragmentSpreadNode spread:
                    if (!fragments.TryGetValue(spread.Name.Value, out var fragment) ||
                        !CollectRoot(fragment.SelectionSet, model, fragments, tables, depth + 1))
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

    private static bool CollectTable(
        SelectionSetNode set,
        CatalogTableType table,
        CatalogSchemaModel model,
        Dictionary<string, FragmentDefinitionNode> fragments,
        List<TableIdentifier> tables,
        int depth)
    {
        if (depth > MaxSelectionDepth)
        {
            return false;
        }

        foreach (var selection in set.Selections)
        {
            switch (selection)
            {
                case FieldNode field:
                    var relation = table.Relations.FirstOrDefault(r => string.Equals(r.FieldName, field.Name.Value, StringComparison.Ordinal));
                    if (relation == null)
                    {
                        // Column or __typename: no further table.
                        break;
                    }

                    if (!model.TablesByIdentifier.TryGetValue(relation.TargetTableIdentifier, out var target))
                    {
                        return false;
                    }

                    tables.Add(target.Identifier);
                    if (field.SelectionSet != null && !CollectTable(field.SelectionSet, target, model, fragments, tables, depth + 1))
                    {
                        return false;
                    }

                    break;
                case InlineFragmentNode inline:
                    if (!CollectTable(inline.SelectionSet, table, model, fragments, tables, depth + 1))
                    {
                        return false;
                    }

                    break;
                case FragmentSpreadNode spread:
                    if (!fragments.TryGetValue(spread.Name.Value, out var fragment) ||
                        !CollectTable(fragment.SelectionSet, table, model, fragments, tables, depth + 1))
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
