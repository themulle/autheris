namespace Autheris.GraphQL.Interceptors;

using System;
using System.Collections.Generic;
using Autheris.Application.Analytics;
using Autheris.GraphQL.Catalog;
using HotChocolate;
using HotChocolate.Language;
using HotChocolate.Types;

public sealed partial class QueryCostAnalyzerRule
{
    /// <summary>A generated <c>T_groups</c> root field: name suffix and a <c>T_group_row</c> result type.</summary>
    private static bool IsFlatGroupsField(string fieldName, IOutputFieldDefinition definition) =>
        fieldName.EndsWith(AggregateTypeNames.GroupsFieldSuffix, StringComparison.Ordinal) &&
        UnwrapType(definition.Type) is INameProvider { Name: var typeName } &&
        typeName.EndsWith(AggregateTypeNames.GroupRowSuffix, StringComparison.Ordinal);

    /// <summary>
    /// Prices a flat aggregation root field from its row selection like the nested shape: distinct group columns, distinct aggregate
    /// fields (<c>*_count_distinct</c> with the count-distinct weight). Fragments are resolved, duplicates count once, and
    /// <c>@skip/@include</c> are ignored so the price is an upper bound. Without the table the worst case applies. Returns false when
    /// the distinct response keys under the row exceed twice the group and aggregate limits (output alias amplification).
    /// </summary>
    private bool TryFlatAggregateCost(
        FieldNode field,
        IReadOnlyDictionary<string, FragmentDefinitionNode>? fragments,
        CatalogTableType? table,
        out double cost,
        out IValueNode? groupByValue)
    {
        var responseKeys = new HashSet<string>(StringComparer.Ordinal);
        var columns = new Dictionary<string, string>(StringComparer.Ordinal);
        var aggregates = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (field.SelectionSet is not null)
        {
            CollectFlatRowFields(field.SelectionSet, fragments, new HashSet<string>(StringComparer.Ordinal), responseKeys, table, columns, aggregates);
        }

        cost = 0;
        groupByValue = null;
        if (responseKeys.Count > 2 * (_maxGroupByColumns + _maxAggregates))
        {
            return false;
        }

        if (table is null)
        {
            cost = (_maxGroupByColumns * AggregationCostCalculator.GroupByColumnWeight)
                   + (_maxAggregates * AggregationCostCalculator.CountDistinctWeight);
            return true;
        }

        cost += columns.Count * AggregationCostCalculator.GroupByColumnWeight;
        foreach (var distinct in aggregates.Values)
        {
            cost += distinct ? AggregationCostCalculator.CountDistinctWeight : AggregationCostCalculator.AggregateSelectionWeight;
        }

        var items = new List<IValueNode>(columns.Count);
        foreach (var columnField in columns.Values)
        {
            items.Add(new EnumValueNode(columnField));
        }

        groupByValue = new ListValueNode(items);
        return true;
    }

    private static void CollectFlatRowFields(
        SelectionSetNode selectionSet,
        IReadOnlyDictionary<string, FragmentDefinitionNode>? fragments,
        HashSet<string> activeFragments,
        HashSet<string> responseKeys,
        CatalogTableType? table,
        Dictionary<string, string> columns,
        Dictionary<string, bool> aggregates)
    {
        foreach (var selection in selectionSet.Selections)
        {
            switch (selection)
            {
                case FieldNode row:
                    responseKeys.Add(row.Alias?.Value ?? row.Name.Value);
                    if (table is not null && FlatAggregateRowFields.Find(table, row.Name.Value) is { } rowField)
                    {
                        switch (rowField.Role)
                        {
                            case FlatAggregateRowFieldRole.Column:
                                columns[row.Name.Value] = rowField.Column!.FieldName;
                                break;
                            case FlatAggregateRowFieldRole.Count or FlatAggregateRowFieldRole.Aggregate:
                                aggregates[row.Name.Value] = rowField.Function == AggregateFunction.CountDistinct;
                                break;
                        }
                    }

                    break;
                case InlineFragmentNode inline:
                    CollectFlatRowFields(inline.SelectionSet, fragments, activeFragments, responseKeys, table, columns, aggregates);
                    break;
                case FragmentSpreadNode spread:
                    if (fragments is not null && fragments.TryGetValue(spread.Name.Value, out var fragment) && activeFragments.Add(spread.Name.Value))
                    {
                        CollectFlatRowFields(fragment.SelectionSet, fragments, activeFragments, responseKeys, table, columns, aggregates);
                        activeFragments.Remove(spread.Name.Value);
                    }

                    break;
            }
        }
    }
}
