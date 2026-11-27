using Autheris.Application.Analytics;
using HotChocolate.Language;

namespace Autheris.GraphQL.Catalog;

/// <summary>One group column of a flat selection; <see cref="Bucket"/>/<see cref="TimeZone"/> are the resolved time bucket arguments.</summary>
internal sealed record FlatGroupColumn(CatalogColumnField Column, string EnumValue, string? Bucket, string? TimeZone);

/// <summary>One distinct aggregate of a flat selection under its engine alias.</summary>
internal sealed record FlatAggregateItem(string Alias, AggregateFunction Function, CatalogColumnField? Column, string? ColumnEnum);

/// <summary>The group set and distinct aggregates read from the sub-selection of a flat root field.</summary>
internal sealed record FlatAggregateSelection(
    IReadOnlyList<FlatGroupColumn> GroupColumns,
    IReadOnlyList<FlatAggregateItem> Aggregates,
    IReadOnlyDictionary<string, string> AliasByResponseKey,
    IReadOnlyDictionary<string, string> AliasByFieldName);

/// <summary>
/// Turns the selection of a flat root field into the argument nodes of the shared <see cref="AggregateArgumentParser"/>. It is pure and
/// fail-closed: an unknown field or an inconsistent selection is ARGUMENT_INVALID. Engine aliases are generated here and never derived
/// from client text; response keys only key the lookup tables.
/// </summary>
internal static class FlatAggregateSelectionReader
{
    private const int MaxAliasLength = 64;

    public static FlatAggregateSelection Read(
        IReadOnlyList<(string ResponseKey, string FieldName, IValueNode? Bucket, IValueNode? TimeZone)> items,
        CatalogTableType table,
        Func<string, IValueNode?> variable)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(variable);

        var enumByField = AggregateColumnEnum.Build(table).ToDictionary(e => e.Column.FieldName, e => e.EnumValue, StringComparer.Ordinal);
        var groupColumns = new List<FlatGroupColumn>();
        var pending = new List<(AggregateFunction Function, CatalogColumnField? Column)>();
        var pendingIndex = new Dictionary<(AggregateFunction, string), int>();
        var indexByResponseKey = new Dictionary<string, int>(StringComparer.Ordinal);
        var indexByFieldName = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var (responseKey, fieldName, bucketNode, timeZoneNode) in items)
        {
            if (string.Equals(fieldName, "__typename", StringComparison.Ordinal))
            {
                continue;
            }

            var field = FlatAggregateRowFields.Find(table, fieldName) ?? throw AggregateErrors.ArgumentInvalid();
            switch (field.Role)
            {
                case FlatAggregateRowFieldRole.MergedOther:
                    break;
                case FlatAggregateRowFieldRole.Column:
                    AddGroupColumn(groupColumns, field.Column!, enumByField, Literal(bucketNode, variable), Literal(timeZoneNode, variable));
                    break;
                case FlatAggregateRowFieldRole.Count or FlatAggregateRowFieldRole.Aggregate:
                    var key = (field.Function!.Value, field.Column?.FieldName ?? string.Empty);
                    if (!pendingIndex.TryGetValue(key, out var index))
                    {
                        index = pending.Count;
                        pendingIndex[key] = index;
                        pending.Add((field.Function.Value, field.Column));
                    }

                    indexByResponseKey[responseKey] = index;
                    indexByFieldName[fieldName] = index;
                    break;
                default:
                    throw AggregateErrors.ArgumentInvalid();
            }
        }

        var aliases = NewAliases(table, pending.Count);
        var aggregates = new List<FlatAggregateItem>(pending.Count);
        for (var i = 0; i < pending.Count; i++)
        {
            var column = pending[i].Column;
            aggregates.Add(new FlatAggregateItem(aliases[i], pending[i].Function, column, column is null ? null : enumByField[column.FieldName]));
        }

        return new FlatAggregateSelection(
            groupColumns,
            aggregates,
            indexByResponseKey.ToDictionary(p => p.Key, p => aliases[p.Value], StringComparer.Ordinal),
            indexByFieldName.ToDictionary(p => p.Key, p => aliases[p.Value], StringComparer.Ordinal));
    }

    public static (IValueNode GroupBy, IValueNode Aggregates, IValueNode? OrderBy) ToArgumentNodes(
        FlatAggregateSelection selection,
        CatalogTableType table,
        IValueNode? orderBy,
        Func<string, IValueNode?> variable)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(variable);

        var groupBy = new List<IValueNode>(selection.GroupColumns.Count);
        foreach (var group in selection.GroupColumns)
        {
            if (group.Bucket is null)
            {
                groupBy.Add(new EnumValueNode(group.EnumValue));
                continue;
            }

            var fields = new List<ObjectFieldNode>
            {
                new("column", new EnumValueNode(group.EnumValue)),
                new("bucket", new EnumValueNode(group.Bucket))
            };
            if (group.TimeZone is not null)
            {
                fields.Add(new ObjectFieldNode("timeZone", new StringValueNode(group.TimeZone)));
            }

            groupBy.Add(new ObjectValueNode(fields));
        }

        var aggregates = new List<IValueNode>(selection.Aggregates.Count);
        foreach (var item in selection.Aggregates)
        {
            var fields = new List<ObjectFieldNode> { new("fn", new EnumValueNode(FunctionName(item.Function))) };
            if (item.ColumnEnum is not null)
            {
                fields.Add(new ObjectFieldNode("column", new EnumValueNode(item.ColumnEnum)));
            }

            fields.Add(new ObjectFieldNode("alias", new StringValueNode(item.Alias)));
            aggregates.Add(new ObjectValueNode(fields));
        }

        return (new ListValueNode(groupBy), new ListValueNode(aggregates), RewriteOrderBy(orderBy, selection, variable));
    }

    private static void AddGroupColumn(
        List<FlatGroupColumn> groupColumns,
        CatalogColumnField column,
        IReadOnlyDictionary<string, string> enumByField,
        string? bucket,
        string? timeZone)
    {
        if (bucket is null && timeZone is not null)
        {
            throw AggregateErrors.ArgumentInvalid();
        }

        var existing = groupColumns.FindIndex(g => string.Equals(g.Column.FieldName, column.FieldName, StringComparison.Ordinal));
        if (existing < 0)
        {
            groupColumns.Add(new FlatGroupColumn(column, enumByField[column.FieldName], bucket, timeZone));
            return;
        }

        if (!string.Equals(groupColumns[existing].Bucket, bucket, StringComparison.Ordinal) ||
            !string.Equals(groupColumns[existing].TimeZone, timeZone, StringComparison.Ordinal))
        {
            throw AggregateErrors.ArgumentInvalid();
        }
    }

    private static string? Literal(IValueNode? node, Func<string, IValueNode?> variable)
    {
        if (node is VariableNode v)
        {
            node = variable(v.Name.Value);
        }

        return node switch
        {
            null or NullValueNode => null,
            EnumValueNode e => e.Value,
            StringValueNode s => s.Value,
            _ => throw AggregateErrors.ArgumentInvalid()
        };
    }

    private static List<string> NewAliases(CatalogTableType table, int count)
    {
        var avoid = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { AggregateTypeNames.CountField };
        foreach (var column in table.Columns)
        {
            avoid.Add(column.FieldName);
            avoid.Add(column.ColumnName);
        }

        var aliases = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var alias = "agg_" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            while (avoid.Contains(alias))
            {
                alias += "_";
            }

            if (alias.Length > MaxAliasLength)
            {
                throw AggregateErrors.ArgumentInvalid();
            }

            avoid.Add(alias);
            aliases.Add(alias);
        }

        return aliases;
    }

    private static IValueNode? RewriteOrderBy(IValueNode? orderBy, FlatAggregateSelection selection, Func<string, IValueNode?> variable)
    {
        var resolved = orderBy is VariableNode v ? variable(v.Name.Value) : orderBy;
        return resolved switch
        {
            null or NullValueNode => resolved,
            ListValueNode list => new ListValueNode(list.Items.Select(i => RewriteOrderItem(i, selection, variable)).ToList()),
            _ => RewriteOrderItem(resolved, selection, variable)
        };
    }

    private static IValueNode RewriteOrderItem(IValueNode item, FlatAggregateSelection selection, Func<string, IValueNode?> variable)
    {
        var resolved = item is VariableNode v ? variable(v.Name.Value) ?? item : item;
        if (resolved is not ObjectValueNode obj)
        {
            return resolved;
        }

        var fields = new List<ObjectFieldNode>(obj.Fields.Count);
        foreach (var field in obj.Fields)
        {
            if (!string.Equals(field.Name.Value, "alias", StringComparison.Ordinal))
            {
                fields.Add(field);
                continue;
            }

            var value = field.Value is VariableNode av ? variable(av.Name.Value) : field.Value;
            if (value is StringValueNode key)
            {
                // An unknown key and the key of a column field both become an alias no aggregate has: ORDER_INVALID.
                var alias = selection.AliasByResponseKey.GetValueOrDefault(key.Value, string.Empty);
                fields.Add(new ObjectFieldNode("alias", new StringValueNode(alias)));
            }
            else
            {
                fields.Add(new ObjectFieldNode("alias", value ?? NullValueNode.Default));
            }
        }

        return new ObjectValueNode(fields);
    }

    private static string FunctionName(AggregateFunction function) => function switch
    {
        AggregateFunction.Count => "COUNT",
        AggregateFunction.CountDistinct => "COUNT_DISTINCT",
        AggregateFunction.Sum => "SUM",
        AggregateFunction.Avg => "AVG",
        AggregateFunction.Min => "MIN",
        _ => "MAX"
    };
}
