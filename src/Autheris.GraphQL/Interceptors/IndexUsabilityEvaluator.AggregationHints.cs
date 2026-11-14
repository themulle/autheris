namespace Autheris.GraphQL.Interceptors;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Domain.Model;
using HotChocolate.Language;

public static partial class IndexUsabilityEvaluator
{
    /// <summary>
    /// Extracts ordered grouping columns from aggregation <c>groupBy</c> and <c>timeBuckets</c> arguments as ascending order entries.
    /// </summary>
    public static List<(string FieldName, string Direction)> ExtractGroupByAsOrder(
        IValueNode? groupByArg,
        IValueNode? timeBucketsArg,
        IReadOnlyDictionary<string, object?>? variableValues = null,
        IReadOnlyList<VariableDefinitionNode>? variableDefinitions = null)
    {
        var columns = new List<(string FieldName, string Direction)>();
        CollectGroupByEntries(groupByArg, columns, variableValues, variableDefinitions);
        CollectTimeBucketEntries(timeBucketsArg, columns, variableValues, variableDefinitions);
        return columns;
    }

    /// <summary>
    /// Builds canonical helpful index columns from <paramref name="filterColumns"/> followed by <paramref name="orderOrGroupColumns"/>.
    /// Fails closed to an empty array if any candidate column is unresolved, missing from metadata, or outside <paramref name="visibleColumns"/>.
    /// </summary>
    public static IReadOnlyList<string> BuildHelpfulIndexColumns(
        TableMetadata tableMeta,
        IReadOnlySet<string>? visibleColumns,
        IReadOnlyList<string>? filterColumns,
        IReadOnlyList<(string FieldName, string Direction)>? orderOrGroupColumns)
    {
        ArgumentNullException.ThrowIfNull(tableMeta);

        var canonicalByName = new Dictionary<string, string>(tableMeta.Columns.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var col in tableMeta.Columns)
        {
            canonicalByName.TryAdd(col.ColumnName, col.ColumnName);
        }

        var helpful = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (filterColumns is not null)
        {
            foreach (var raw in filterColumns)
            {
                if (string.Equals(raw, UnusableFilterMarker, StringComparison.Ordinal))
                {
                    continue;
                }

                var col = StripOrBranchPrefix(raw);
                if (!TryAppendHelpfulColumn(col, canonicalByName, visibleColumns, seen, helpful))
                {
                    return Array.Empty<string>();
                }
            }
        }

        if (orderOrGroupColumns is not null)
        {
            foreach (var (fieldName, direction) in orderOrGroupColumns)
            {
                if (string.Equals(direction, UnresolvedVariableMarker, StringComparison.Ordinal) ||
                    !TryAppendHelpfulColumn(fieldName, canonicalByName, visibleColumns, seen, helpful))
                {
                    return Array.Empty<string>();
                }
            }
        }

        return helpful.Count == 0 ? Array.Empty<string>() : helpful;
    }

    private static string StripOrBranchPrefix(string value)
    {
        if (!value.StartsWith(OrBranchPrefix, StringComparison.Ordinal))
        {
            return value;
        }

        var rest = value.AsSpan(OrBranchPrefix.Length);
        var colonIdx = rest.IndexOf(':');
        return colonIdx > 0 ? rest[(colonIdx + 1)..].ToString() : value;
    }

    private static bool TryAppendHelpfulColumn(
        string candidate,
        IReadOnlyDictionary<string, string> canonicalByName,
        IReadOnlySet<string>? visibleColumns,
        HashSet<string> seen,
        List<string> helpful)
    {
        if (string.IsNullOrWhiteSpace(candidate) ||
            string.Equals(candidate, UnresolvedVariableMarker, StringComparison.Ordinal) ||
            string.Equals(candidate, UnusableFilterMarker, StringComparison.Ordinal))
        {
            return false;
        }

        if (!canonicalByName.TryGetValue(candidate, out var canonical))
        {
            return false;
        }

        if (visibleColumns is not null && !IsColumnVisible(canonical, visibleColumns))
        {
            return false;
        }

        if (seen.Add(canonical))
        {
            helpful.Add(canonical);
        }

        return true;
    }

    private static bool IsColumnVisible(string canonical, IReadOnlySet<string> visibleColumns)
    {
        if (visibleColumns.Contains(canonical))
        {
            return true;
        }

        foreach (var visible in visibleColumns)
        {
            if (string.Equals(visible, canonical, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void CollectGroupByEntries(
        IValueNode? node,
        List<(string FieldName, string Direction)> columns,
        IReadOnlyDictionary<string, object?>? variableValues,
        IReadOnlyList<VariableDefinitionNode>? variableDefinitions)
    {
        switch (node)
        {
            case null or NullValueNode:
                return;
            case VariableNode vn:
                if (variableValues is not null && variableValues.TryGetValue(vn.Name.Value, out var runtime))
                {
                    CollectGroupByRuntime(runtime, columns, variableValues, variableDefinitions);
                }
                else if (TryGetDefaultVariableNode(vn.Name.Value, variableDefinitions, out var defNode))
                {
                    CollectGroupByEntries(defNode, columns, variableValues, variableDefinitions);
                }
                else
                {
                    columns.Add((UnresolvedVariableMarker, "ASC"));
                }
                return;
            case ListValueNode list:
                foreach (var item in list.Items)
                {
                    if (item is NullValueNode)
                    {
                        columns.Add((UnresolvedVariableMarker, "ASC"));
                    }
                    else
                    {
                        CollectGroupBySingleItem(item, columns, variableValues, variableDefinitions);
                    }
                }
                return;
            default:
                CollectGroupBySingleItem(node, columns, variableValues, variableDefinitions);
                return;
        }
    }

    private static void CollectGroupBySingleItem(
        IValueNode item,
        List<(string FieldName, string Direction)> columns,
        IReadOnlyDictionary<string, object?>? variableValues,
        IReadOnlyList<VariableDefinitionNode>? variableDefinitions)
    {
        switch (item)
        {
            case EnumValueNode e:
                AddGroupColumn(e.Value, columns);
                return;
            case StringValueNode s:
                AddGroupColumn(s.Value, columns);
                return;
            case ObjectValueNode obj:
                AddTimeBucketObjectColumn(obj, columns, variableValues, variableDefinitions);
                return;
            case VariableNode vn:
                if (variableValues is not null && variableValues.TryGetValue(vn.Name.Value, out var runtime))
                {
                    CollectGroupByRuntime(runtime, columns, variableValues, variableDefinitions);
                }
                else if (TryGetDefaultVariableNode(vn.Name.Value, variableDefinitions, out var defNode))
                {
                    CollectGroupBySingleItem(defNode!, columns, variableValues, variableDefinitions);
                }
                else
                {
                    columns.Add((UnresolvedVariableMarker, "ASC"));
                }
                return;
            default:
                columns.Add((UnresolvedVariableMarker, "ASC"));
                return;
        }
    }

    private static void CollectGroupByRuntime(
        object? runtime,
        List<(string FieldName, string Direction)> columns,
        IReadOnlyDictionary<string, object?>? variableValues,
        IReadOnlyList<VariableDefinitionNode>? variableDefinitions)
    {
        switch (runtime)
        {
            case null or NullValueNode:
                return;
            case IValueNode ivn:
                CollectGroupByEntries(ivn, columns, variableValues, variableDefinitions);
                return;
            case string s:
                AddGroupColumn(s, columns);
                return;
            case System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.Array } arr:
                foreach (var el in arr.EnumerateArray())
                {
                    CollectGroupByRuntimeElement(el, columns);
                }
                return;
            case System.Collections.IEnumerable seq:
                foreach (var el in seq)
                {
                    CollectGroupByRuntimeElement(el, columns);
                }
                return;
            default:
                CollectGroupByRuntimeElement(runtime, columns);
                return;
        }
    }

    private static void CollectGroupByRuntimeElement(object? el, List<(string FieldName, string Direction)> columns)
    {
        switch (el)
        {
            case string s:
                AddGroupColumn(s, columns);
                return;
            case EnumValueNode ev:
                AddGroupColumn(ev.Value, columns);
                return;
            case StringValueNode sv:
                AddGroupColumn(sv.Value, columns);
                return;
            case System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.String } js when js.GetString() is { } text:
                AddGroupColumn(text, columns);
                return;
            case System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.Object } jo
                when jo.TryGetProperty("column", out var jCol) && jCol.GetString() is { } colText:
                AddGroupColumn(colText, columns);
                return;
            case IDictionary<string, object?> dict when dict.TryGetValue("column", out var colObj) && colObj is string colStr:
                AddGroupColumn(colStr, columns);
                return;
            case IReadOnlyDictionary<string, object?> roDict when roDict.TryGetValue("column", out var roColObj) && roColObj is string roColStr:
                AddGroupColumn(roColStr, columns);
                return;
            default:
                columns.Add((UnresolvedVariableMarker, "ASC"));
                return;
        }
    }

    private static void CollectTimeBucketEntries(
        IValueNode? node,
        List<(string FieldName, string Direction)> columns,
        IReadOnlyDictionary<string, object?>? variableValues,
        IReadOnlyList<VariableDefinitionNode>? variableDefinitions)
    {
        switch (node)
        {
            case null or NullValueNode:
                return;
            case VariableNode vn:
                if (variableValues is not null && variableValues.TryGetValue(vn.Name.Value, out var runtime))
                {
                    CollectTimeBucketRuntime(runtime, columns, variableValues, variableDefinitions);
                }
                else if (TryGetDefaultVariableNode(vn.Name.Value, variableDefinitions, out var defNode))
                {
                    CollectTimeBucketEntries(defNode, columns, variableValues, variableDefinitions);
                }
                else
                {
                    columns.Add((UnresolvedVariableMarker, "ASC"));
                }
                return;
            case ListValueNode list:
                foreach (var item in list.Items)
                {
                    CollectTimeBucketItem(item, columns, variableValues, variableDefinitions);
                }
                return;
            case ObjectValueNode obj:
                AddTimeBucketObjectColumn(obj, columns, variableValues, variableDefinitions);
                return;
            default:
                columns.Add((UnresolvedVariableMarker, "ASC"));
                return;
        }
    }

    private static void CollectTimeBucketItem(
        IValueNode item,
        List<(string FieldName, string Direction)> columns,
        IReadOnlyDictionary<string, object?>? variableValues,
        IReadOnlyList<VariableDefinitionNode>? variableDefinitions)
    {
        switch (item)
        {
            case ObjectValueNode obj:
                AddTimeBucketObjectColumn(obj, columns, variableValues, variableDefinitions);
                return;
            case VariableNode vn:
                if (variableValues is not null && variableValues.TryGetValue(vn.Name.Value, out var runtime))
                {
                    CollectTimeBucketRuntime(runtime, columns, variableValues, variableDefinitions);
                }
                else if (TryGetDefaultVariableNode(vn.Name.Value, variableDefinitions, out var defNode))
                {
                    CollectTimeBucketItem(defNode!, columns, variableValues, variableDefinitions);
                }
                else
                {
                    columns.Add((UnresolvedVariableMarker, "ASC"));
                }
                return;
            default:
                columns.Add((UnresolvedVariableMarker, "ASC"));
                return;
        }
    }

    private static void CollectTimeBucketRuntime(
        object? runtime,
        List<(string FieldName, string Direction)> columns,
        IReadOnlyDictionary<string, object?>? variableValues,
        IReadOnlyList<VariableDefinitionNode>? variableDefinitions)
    {
        switch (runtime)
        {
            case null or NullValueNode:
                return;
            case IValueNode ivn:
                CollectTimeBucketEntries(ivn, columns, variableValues, variableDefinitions);
                return;
            case System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.Array } arr:
                foreach (var el in arr.EnumerateArray())
                {
                    CollectTimeBucketRuntimeElement(el, columns);
                }
                return;
            case System.Collections.IEnumerable seq when runtime is not string:
                foreach (var el in seq)
                {
                    CollectTimeBucketRuntimeElement(el, columns);
                }
                return;
            default:
                CollectTimeBucketRuntimeElement(runtime, columns);
                return;
        }
    }

    private static void CollectTimeBucketRuntimeElement(object? el, List<(string FieldName, string Direction)> columns)
    {
        switch (el)
        {
            case System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.Object } jo
                when jo.TryGetProperty("column", out var jCol) && jCol.GetString() is { } colText:
                AddGroupColumn(colText, columns);
                return;
            case IDictionary<string, object?> dict when dict.TryGetValue("column", out var colObj) && colObj is string colStr:
                AddGroupColumn(colStr, columns);
                return;
            case IReadOnlyDictionary<string, object?> roDict when roDict.TryGetValue("column", out var roColObj) && roColObj is string roColStr:
                AddGroupColumn(roColStr, columns);
                return;
            default:
                columns.Add((UnresolvedVariableMarker, "ASC"));
                return;
        }
    }

    private static void AddTimeBucketObjectColumn(
        ObjectValueNode obj,
        List<(string FieldName, string Direction)> columns,
        IReadOnlyDictionary<string, object?>? variableValues,
        IReadOnlyList<VariableDefinitionNode>? variableDefinitions)
    {
        var colField = obj.Fields.FirstOrDefault(f => string.Equals(f.Name.Value, "column", StringComparison.OrdinalIgnoreCase))?.Value;
        switch (colField)
        {
            case EnumValueNode ev:
                AddGroupColumn(ev.Value, columns);
                return;
            case StringValueNode sv:
                AddGroupColumn(sv.Value, columns);
                return;
            case VariableNode vn:
                if (variableValues is not null && variableValues.TryGetValue(vn.Name.Value, out var runtime))
                {
                    var name = runtime switch
                    {
                        EnumValueNode ev => ev.Value,
                        StringValueNode sv => sv.Value,
                        string str => str,
                        _ => null
                    };
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        AddGroupColumn(name, columns);
                        return;
                    }
                }
                else if (TryGetDefaultVariableNode(vn.Name.Value, variableDefinitions, out var defNode))
                {
                    var name = defNode switch
                    {
                        EnumValueNode ev => ev.Value,
                        StringValueNode sv => sv.Value,
                        _ => null
                    };
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        AddGroupColumn(name, columns);
                        return;
                    }
                }

                columns.Add((UnresolvedVariableMarker, "ASC"));
                return;
            default:
                columns.Add((UnresolvedVariableMarker, "ASC"));
                return;
        }
    }

    private static void AddGroupColumn(string name, List<(string FieldName, string Direction)> columns)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            columns.Add((UnresolvedVariableMarker, "ASC"));
            return;
        }

        if (!columns.Any(c => string.Equals(c.FieldName, name, StringComparison.OrdinalIgnoreCase)))
        {
            columns.Add((name, "ASC"));
        }
    }
}
