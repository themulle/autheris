namespace Autheris.GraphQL.Interceptors;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Application.Analytics;
using Autheris.Domain.Model;
using Autheris.GraphQL.Catalog;
using HotChocolate.Language;
using HotChocolate.Types;

public sealed partial class QueryCostAnalyzerRule
{
    /// <summary>
    /// #112 the largest number of typed aggregate and<c>queryMetrics</c> root fields (aliases counted separately, fragments
    /// resolved) over the operations of the document. Stops once <paramref name="stopAfter"/> is exceeded.
    /// </summary>
    public static int CountAggregateRootFields(DocumentNode document, ISchemaDefinition schema, int stopAfter = int.MaxValue)
    {
        var fragments = document.Definitions
            .OfType<FragmentDefinitionNode>()
            .GroupBy(f => f.Name.Value, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var max = 0;
        foreach (var operation in document.Definitions.OfType<OperationDefinitionNode>())
        {
            if (operation.Operation != OperationType.Query)
            {
                continue;
            }

            var count = 0;
            CountAggregateFields(operation.SelectionSet, schema.QueryType, schema, fragments, new HashSet<string>(StringComparer.Ordinal), ref count, stopAfter);
            max = Math.Max(max, count);
            if (max > stopAfter)
            {
                break;
            }
        }

        return max;
    }

    private static void CountAggregateFields(
        SelectionSetNode selectionSet,
        IObjectTypeDefinition rootType,
        ISchemaDefinition schema,
        IReadOnlyDictionary<string, FragmentDefinitionNode> fragments,
        HashSet<string> activeFragments,
        ref int count,
        int stopAfter)
    {
        foreach (var selection in selectionSet.Selections)
        {
            if (count > stopAfter)
            {
                return;
            }

            switch (selection)
            {
                case FieldNode field:
                    if (rootType.Fields.TryGetField(field.Name.Value, out var def) && IsTypedAggregateField(field.Name.Value, def))
                    {
                        count++;
                    }

                    break;
                case InlineFragmentNode inline:
                    CountAggregateFields(inline.SelectionSet, rootType, schema, fragments, activeFragments, ref count, stopAfter);
                    break;
                case FragmentSpreadNode spread:
                    if (fragments.TryGetValue(spread.Name.Value, out var fragment) && activeFragments.Add(fragment.Name.Value))
                    {
                        CountAggregateFields(fragment.SelectionSet, rootType, schema, fragments, activeFragments, ref count, stopAfter);
                        activeFragments.Remove(fragment.Name.Value);
                    }

                    break;
            }
        }
    }

    /// <summary>A generated <c>T_aggregate</c> field (name and result type suffix) or the shared <c>queryMetrics</c> field.</summary>
    private static bool IsTypedAggregateField(string fieldName, IOutputFieldDefinition definition)
    {
        if (string.Equals(fieldName, "queryMetrics", StringComparison.Ordinal))
        {
            return true;
        }

        return fieldName.EndsWith("_aggregate", StringComparison.Ordinal) &&
               UnwrapType(definition.Type) is INameProvider { Name: var typeName } &&
               typeName.EndsWith("_aggregate_result", StringComparison.Ordinal);
    }

    private TableMetadata? ResolveTableMetadata(string primaryName, string? fallbackName = null)
    {
        var meta = _tableMetadataLookup?.Invoke(primaryName);
        if (meta is null && !string.IsNullOrEmpty(fallbackName) && !string.Equals(fallbackName, primaryName, StringComparison.Ordinal))
        {
            meta = _tableMetadataLookup?.Invoke(fallbackName);
        }

        if (meta is not null)
        {
            return meta;
        }

        meta = CatalogGraphQlTypeModule.CurrentLookup?.Invoke(primaryName);
        if (meta is null && !string.IsNullOrEmpty(fallbackName) && !string.Equals(fallbackName, primaryName, StringComparison.Ordinal))
        {
            meta = CatalogGraphQlTypeModule.CurrentLookup?.Invoke(fallbackName);
        }

        return meta;
    }

    private IReadOnlySet<string>? ResolveVisibleColumns(string fieldKey, TableMetadata tableMeta) =>
        _visibleColumnsLookup is null
            ? null
            : _visibleColumnsLookup.Invoke(fieldKey)
                ?? _visibleColumnsLookup.Invoke(tableMeta.Table.TableName)
                ?? _visibleColumnsLookup.Invoke(tableMeta.Identifier.ToQualifiedName());

    private int AggregateFieldCost(
        FieldNode field,
        IReadOnlyList<VariableDefinitionNode>? variableDefinitions,
        IReadOnlyDictionary<string, object?>? variableValues,
        int currentDepth = 1,
        IObjectTypeDefinition? currentType = null,
        ISchemaDefinition? schema = null)
    {
        // queryMetrics has no typed groupBy/aggregates list: it keeps the generic cost of a payload field via the base weight only.
        double cost = _aggregateFieldCostWeight;

        var groupBy = field.Arguments.FirstOrDefault(a => string.Equals(a.Name.Value, "groupBy", StringComparison.Ordinal));
        cost += (groupBy is null ? 0 : (TryListItems(groupBy.Value, variableDefinitions, variableValues, out var groupItems) ? groupItems.Count : _maxGroupByColumns))
                * AggregationCostCalculator.GroupByColumnWeight;

        var aggregates = field.Arguments.FirstOrDefault(a => string.Equals(a.Name.Value, "aggregates", StringComparison.Ordinal));
        if (aggregates is null)
        {
            if (!string.Equals(field.Name.Value, "queryMetrics", StringComparison.Ordinal))
            {
                cost += AggregationCostCalculator.AggregateSelectionWeight;
            }
        }
        else if (TryListItems(aggregates.Value, variableDefinitions, variableValues, out var items))
        {
            foreach (var item in items)
            {
                cost += IsCountDistinct(item, variableValues)
                    ? AggregationCostCalculator.CountDistinctWeight
                    : AggregationCostCalculator.AggregateSelectionWeight;
            }
        }
        else
        {
            cost += _maxAggregates * AggregationCostCalculator.CountDistinctWeight;
        }

        int totalCost = (int)Math.Min(int.MaxValue, Math.Ceiling(cost));
        bool isRootField = currentDepth <= 1 || (currentType != null && schema != null && currentType == schema.QueryType);

        if (isRootField &&
            !string.Equals(field.Name.Value, "queryMetrics", StringComparison.OrdinalIgnoreCase) &&
            _costOptions is not null &&
            _costOptions.UnindexedRootListSurchargeMultiplier > 1.0)
        {
            var baseName = field.Name.Value.EndsWith("_aggregate", StringComparison.OrdinalIgnoreCase)
                ? field.Name.Value[..^"_aggregate".Length]
                : field.Name.Value;
            var tableMeta = ResolveTableMetadata(baseName, field.Name.Value);
            if (tableMeta is not null)
            {
                var visibleSet = ResolveVisibleColumns(baseName, tableMeta);
                var visibleList = visibleSet?.ToList();
                var timeBuckets = field.Arguments.FirstOrDefault(a => string.Equals(a.Name.Value, "timeBuckets", StringComparison.Ordinal));
                var whereArg = field.Arguments.FirstOrDefault(a => string.Equals(a.Name.Value, "where", StringComparison.Ordinal));
                var groupByAsOrder = IndexUsabilityEvaluator.ExtractGroupByAsOrder(groupBy?.Value, timeBuckets?.Value, variableValues, variableDefinitions);
                var filterColumns = IndexUsabilityEvaluator.ExtractFilterColumns(whereArg, variableValues, variableDefinitions);

                bool hasUsableIndex = (groupByAsOrder.Count > 0 || (filterColumns is not null && filterColumns.Count > 0)) &&
                    IndexUsabilityEvaluator.HasUsableIndex(tableMeta, visibleList, groupByAsOrder, filterColumns);

                if (!hasUsableIndex)
                {
                    totalCost = (int)Math.Min((long)int.MaxValue, (long)Math.Ceiling(totalCost * _costOptions.UnindexedRootListSurchargeMultiplier));
                    if (_indexHints is not null)
                    {
                        var rootKey = field.Alias?.Value ?? field.Name.Value;
                        var helpfulCols = IndexUsabilityEvaluator.BuildHelpfulIndexColumns(tableMeta, visibleSet, filterColumns, groupByAsOrder);
                        _indexHints.Add(new GraphQlIndexHint(rootKey, GraphQlIndexHint.NoUsableIndexReason, helpfulCols));
                    }
                }
            }
        }

        return totalCost;
    }

    private static bool TryListItems(
        IValueNode? value,
        IReadOnlyList<VariableDefinitionNode>? variableDefinitions,
        IReadOnlyDictionary<string, object?>? variableValues,
        out IReadOnlyList<object?> items)
    {
        items = [];
        switch (value)
        {
            case null or NullValueNode:
                return true;
            case ListValueNode list:
                items = list.Items.Cast<object?>().ToList();
                return true;
            case VariableNode variable:
                if (variableValues != null && variableValues.TryGetValue(variable.Name.Value, out var runtime))
                {
                    return TryRuntimeItems(runtime, out items);
                }

                var definition = variableDefinitions?.FirstOrDefault(v => string.Equals(v.Variable.Name.Value, variable.Name.Value, StringComparison.Ordinal));
                if (definition?.DefaultValue is { } defaultValue and not VariableNode)
                {
                    return TryListItems(defaultValue, variableDefinitions, variableValues, out items);
                }

                return false;
            default:
                // GraphQL list coercion: a single value stands for a one-element list.
                items = [value];
                return true;
        }
    }

    private static bool TryRuntimeItems(object? runtime, out IReadOnlyList<object?> items)
    {
        items = [];
        switch (runtime)
        {
            case null or NullValueNode:
                return true;
            case ListValueNode list:
                items = list.Items.Cast<object?>().ToList();
                return true;
            case IValueNode single:
                items = [single];
                return true;
            case System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.Array } json:
                items = json.EnumerateArray().Cast<object?>().ToList();
                return true;
            case string:
                return false;
            case System.Collections.IEnumerable enumerable:
                items = enumerable.Cast<object?>().ToList();
                return true;
            default:
                return false;
        }
    }

    private static bool IsCountDistinct(object? item, IReadOnlyDictionary<string, object?>? variableValues)
    {
        switch (item)
        {
            case ObjectValueNode obj:
                var fn = obj.Fields.FirstOrDefault(f => string.Equals(f.Name.Value, "fn", StringComparison.Ordinal))?.Value;
                return FunctionIsCountDistinct(fn, variableValues);
            case VariableNode variable when variableValues != null && variableValues.TryGetValue(variable.Name.Value, out var runtime):
                return IsCountDistinct(runtime, variableValues);
            case System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.Object } json:
                return !json.TryGetProperty("fn", out var jsonFn) || IsDistinctName(jsonFn.GetString());
            case System.Collections.IDictionary dictionary:
                return IsDistinctName(dictionary.Contains("fn") ? dictionary["fn"]?.ToString() : null);
            case IReadOnlyDictionary<string, object?> readOnly:
                return IsDistinctName(readOnly.TryGetValue("fn", out var raw) ? raw?.ToString() : null);
            default:
                // Unresolvable: the worst case.
                return true;
        }
    }

    private static bool FunctionIsCountDistinct(IValueNode? fn, IReadOnlyDictionary<string, object?>? variableValues) => fn switch
    {
        EnumValueNode e => IsDistinctName(e.Value),
        StringValueNode s => IsDistinctName(s.Value),
        VariableNode v when variableValues != null && variableValues.TryGetValue(v.Name.Value, out var runtime) =>
            runtime switch
            {
                EnumValueNode e => IsDistinctName(e.Value),
                StringValueNode s => IsDistinctName(s.Value),
                string text => IsDistinctName(text),
                _ => true
            },
        _ => true
    };

    /// <summary>Only the exact names of the cheap functions are cheap; anything else (including a missing name) is priced as COUNT_DISTINCT.</summary>
    private static bool IsDistinctName(string? name) =>
        name is not ("COUNT" or "SUM" or "AVG" or "MIN" or "MAX");
}
