namespace Autheris.GraphQL.Interceptors;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Application.Data.Services;
using Autheris.Domain.Model;
using HotChocolate.Language;

/// <summary>
/// Issue #421 (WP-D of #417) / HIGH-03: Evaluates whether a GraphQL root-list plan can utilize a database index
/// based on catalog metadata (declared primary keys, secondary/unique indexes, and source physical indexes).
/// Zero live stats, zero-allocation friendly, fail-closed anti-enumeration.
/// </summary>
public static class IndexUsabilityEvaluator
{
    public const string UnresolvedVariableMarker = "__AUTHERIS_UNRESOLVED_VARIABLE__";
    public const string UnusableFilterMarker = "__AUTHERIS_UNUSABLE_FILTER__";
    internal const string OrBranchPrefix = "__OR_BRANCH_";

    private static readonly HashSet<string> SeekableOperators = new(StringComparer.OrdinalIgnoreCase)
    {
        "eq", "in", "gt", "gte", "lt", "lte", "startsWith"
    };

    private static readonly HashSet<string> ScanOperators = new(StringComparer.OrdinalIgnoreCase)
    {
        "contains", "endsWith", "neq", "nin"
    };

    internal readonly record struct CandidateIndex(
        IReadOnlyList<string> Columns,
        IReadOnlyList<KeyColumnDirection>? Directions);

    /// <summary>
    /// Evaluates whether the given root-list query plan can use an index for its ordering and/or filtering.
    /// </summary>
    public static bool HasUsableIndex(
        TableMetadata metadata,
        IReadOnlyList<string>? visibleColumns,
        IReadOnlyList<(string FieldName, string Direction)>? orderBy,
        IReadOnlyList<string>? filterColumns)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        // SEC-COST-421-02: Prune indexes that touch columns unauthorized or hidden from the caller
        var effectiveMeta = visibleColumns is not null
            ? TableKeyVisibility.Restrict(
                metadata,
                metadata.Columns.Where(c => visibleColumns.Contains(c.ColumnName, StringComparer.OrdinalIgnoreCase)).ToList())
            : metadata;

        bool hasExplicitOrder = orderBy is not null && orderBy.Count > 0;
        bool hasFilter = filterColumns is not null && filterColumns.Count > 0;

        if (!hasExplicitOrder && !hasFilter)
        {
            // Top-K sequential read without orderBy or where condition does not perform an unindexed sort or filter scan.
            return true;
        }

        var candidates = GetCandidateIndexes(effectiveMeta);
        if (candidates.Count == 0)
        {
            return false;
        }

        bool orderMatched = false;
        if (hasExplicitOrder)
        {
            // When an explicit orderBy is specified, the database requires a matching index prefix
            // (matching column names and ASC/DESC directions) to avoid a full unindexed sort.
            foreach (var candidate in candidates)
            {
                if (CandidateMatchesOrder(candidate, orderBy!))
                {
                    orderMatched = true;
                    break;
                }
            }

            if (!orderMatched)
            {
                return false;
            }
        }

        bool filterMatched = false;
        if (hasFilter)
        {
            if (filterColumns!.Any(fc => string.Equals(fc, UnresolvedVariableMarker, StringComparison.Ordinal) ||
                                         string.Equals(fc, UnusableFilterMarker, StringComparison.Ordinal)))
            {
                return false;
            }

            var orBranches = new Dictionary<int, List<string>>();
            var normalColumns = new List<string>();

            foreach (var fc in filterColumns!)
            {
                if (fc.StartsWith(OrBranchPrefix, StringComparison.Ordinal))
                {
                    var rest = fc.AsSpan(OrBranchPrefix.Length);
                    var colonIdx = rest.IndexOf(':');
                    if (colonIdx > 0 && int.TryParse(rest.Slice(0, colonIdx), out int branchIdx))
                    {
                        var colName = rest.Slice(colonIdx + 1).ToString();
                        if (!orBranches.TryGetValue(branchIdx, out var branchList))
                        {
                            branchList = new List<string>();
                            orBranches[branchIdx] = branchList;
                        }
                        branchList.Add(colName);
                        continue;
                    }
                }

                normalColumns.Add(fc);
            }

            bool normalMatched = normalColumns.Count == 0 || candidates.Any(c => c.Columns.Count > 0 &&
                normalColumns.Any(nc => string.Equals(nc, c.Columns[0], StringComparison.OrdinalIgnoreCase)));

            bool orMatched = true;
            if (orBranches.Count > 0)
            {
                foreach (var (_, branchCols) in orBranches)
                {
                    bool branchMatchesAnyIndex = candidates.Any(c => c.Columns.Count > 0 &&
                        branchCols.Any(bc => string.Equals(bc, c.Columns[0], StringComparison.OrdinalIgnoreCase)));

                    if (!branchMatchesAnyIndex)
                    {
                        orMatched = false;
                        break;
                    }
                }
            }

            filterMatched = normalMatched && orMatched;

            if (!filterMatched)
            {
                return false;
            }
        }

        if (hasExplicitOrder && hasFilter)
        {
            // HIGH-03: When both orderBy and where are specified, both must be satisfied by indexes
            return orderMatched && filterMatched;
        }

        if (hasExplicitOrder) return orderMatched;
        if (hasFilter) return filterMatched;

        return true;
    }

    /// <summary>
    /// Checks if a candidate index prefix matches the requested orderBy columns and directions in order.
    /// </summary>
    private static bool CandidateMatchesOrder(
        CandidateIndex candidate,
        IReadOnlyList<(string FieldName, string Direction)> orderBy)
    {
        if (candidate.Columns.Count < orderBy.Count)
        {
            return false;
        }

        for (int i = 0; i < orderBy.Count; i++)
        {
            if (string.Equals(orderBy[i].FieldName, UnresolvedVariableMarker, StringComparison.Ordinal) ||
                string.Equals(orderBy[i].Direction, UnresolvedVariableMarker, StringComparison.Ordinal))
            {
                return false;
            }

            if (!string.Equals(candidate.Columns[i], orderBy[i].FieldName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            bool indexDesc = KeyDirections.IsDescending(candidate.Directions, i);
            bool requestedDesc = string.Equals(orderBy[i].Direction, "DESC", StringComparison.OrdinalIgnoreCase);

            if (indexDesc != requestedDesc)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Gathers all candidate indexes from table metadata: primary key, secondary/unique indexes, and physical indexes.
    /// </summary>
    internal static List<CandidateIndex> GetCandidateIndexes(TableMetadata metadata)
    {
        var candidates = new List<CandidateIndex>(2 + metadata.Indexes.Count + metadata.PhysicalIndexes.Count);

        if (metadata.PrimaryKeyColumns.Count > 0)
        {
            candidates.Add(new CandidateIndex(
                metadata.PrimaryKeyColumns,
                KeyDirections.Canonical(metadata.PrimaryKeyDirections) ?? Array.Empty<KeyColumnDirection>()));
        }

        foreach (var idx in metadata.Indexes)
        {
            if (idx.Columns.Count > 0)
            {
                candidates.Add(new CandidateIndex(
                    idx.Columns,
                    KeyDirections.Canonical(idx.Directions) ?? Array.Empty<KeyColumnDirection>()));
            }
        }

        foreach (var phys in metadata.PhysicalIndexes)
        {
            if (phys.Columns.Count > 0)
            {
                candidates.Add(new CandidateIndex(
                    phys.Columns,
                    KeyDirections.Canonical(phys.Directions) ?? Array.Empty<KeyColumnDirection>()));
            }
        }

        return candidates;
    }

    /// <summary>
    /// Extracts ordered (FieldName, Direction) tuples from an AST "orderBy" argument node.
    /// </summary>
    public static List<(string FieldName, string Direction)>? ExtractOrderBy(
        ArgumentNode? orderArg,
        IReadOnlyDictionary<string, object?>? variableValues = null)
    {
        if (orderArg is null || orderArg.Value is null)
        {
            return null;
        }

        var literal = orderArg.Value;
        if (literal is VariableNode varNode)
        {
            if (variableValues != null && variableValues.TryGetValue(varNode.Name.Value, out var varVal))
            {
                return ExtractOrderByFromRuntimeValue(varVal);
            }

            return new List<(string FieldName, string Direction)> { (UnresolvedVariableMarker, "ASC") };
        }

        var orders = new List<(string FieldName, string Direction)>();

        if (literal is ListValueNode listNode)
        {
            foreach (var item in listNode.Items)
            {
                if (item is VariableNode vn)
                {
                    if (variableValues != null && variableValues.TryGetValue(vn.Name.Value, out var iv))
                    {
                        var runtimeOrders = ExtractOrderByFromRuntimeValue(iv);
                        if (runtimeOrders is not null)
                        {
                            orders.AddRange(runtimeOrders);
                        }
                    }
                    else
                    {
                        orders.Add((UnresolvedVariableMarker, "ASC"));
                    }
                }
                else if (item is ObjectValueNode obj)
                {
                    AddOrderEntry(obj, orders, variableValues);
                }
                else if (item is not null)
                {
                    var runtimeOrders = ExtractOrderByFromRuntimeValue(item);
                    if (runtimeOrders is not null)
                    {
                        orders.AddRange(runtimeOrders);
                    }
                }
            }
        }
        else if (literal is ObjectValueNode singleObj)
        {
            AddOrderEntry(singleObj, orders, variableValues);
        }

        return orders;
    }

    private static void AddOrderEntry(
        ObjectValueNode obj,
        List<(string FieldName, string Direction)> orders,
        IReadOnlyDictionary<string, object?>? variableValues)
    {
        foreach (var f in obj.Fields)
        {
            string dir;
            if (f.Value is VariableNode fvn)
            {
                if (variableValues != null && variableValues.TryGetValue(fvn.Name.Value, out var varVal))
                {
                    dir = varVal switch
                    {
                        EnumValueNode ev => ev.Value,
                        StringValueNode sv => sv.Value,
                        _ => varVal?.ToString() ?? "ASC"
                    };
                }
                else
                {
                    orders.Add((f.Name.Value, UnresolvedVariableMarker));
                    continue;
                }
            }
            else
            {
                dir = f.Value switch
                {
                    EnumValueNode ev => ev.Value,
                    StringValueNode sv => sv.Value,
                    _ => "ASC"
                };
            }

            orders.Add((f.Name.Value, dir));
        }
    }

    private static List<(string FieldName, string Direction)>? ExtractOrderByFromRuntimeValue(object? val)
    {
        if (val is null) return null;
        var result = new List<(string FieldName, string Direction)>();

        if (val is ListValueNode listNode)
        {
            foreach (var item in listNode.Items)
            {
                if (item is ObjectValueNode objNode)
                {
                    AddOrderEntry(objNode, result, null);
                }
            }
        }
        else if (val is ObjectValueNode singleObj)
        {
            AddOrderEntry(singleObj, result, null);
        }
        else if (val is IEnumerable<object> list)
        {
            foreach (var item in list)
            {
                if (item is IDictionary<string, object?> dict)
                {
                    foreach (var kvp in dict)
                    {
                        result.Add((kvp.Key, kvp.Value?.ToString() ?? "ASC"));
                    }
                }
                else if (item is ObjectValueNode ovn)
                {
                    AddOrderEntry(ovn, result, null);
                }
            }
        }
        else if (val is IDictionary<string, object?> dict)
        {
            foreach (var kvp in dict)
            {
                result.Add((kvp.Key, kvp.Value?.ToString() ?? "ASC"));
            }
        }

        return result;
    }

    /// <summary>
    /// Extracts column names referenced in a "where" filter AST argument.
    /// </summary>
    public static List<string>? ExtractFilterColumns(
        ArgumentNode? whereArg,
        IReadOnlyDictionary<string, object?>? variableValues = null)
    {
        if (whereArg is null || whereArg.Value is null)
        {
            return null;
        }

        var literal = whereArg.Value;
        if (literal is VariableNode varNode)
        {
            if (variableValues != null && variableValues.TryGetValue(varNode.Name.Value, out var varVal))
            {
                return ExtractFilterColumnsFromRuntimeValue(varVal);
            }

            return new List<string> { UnresolvedVariableMarker };
        }

        var columns = new List<string>();
        bool hadConditions = false;
        CollectFilterColumnsFromNode(literal, columns, variableValues, ref hadConditions, orBranchIndex: null);
        if (hadConditions && columns.Count == 0)
        {
            columns.Add(UnusableFilterMarker);
        }

        return columns.Count > 0 ? columns : null;
    }

    private static void CollectFilterColumnsFromNode(
        IValueNode node,
        List<string> columns,
        IReadOnlyDictionary<string, object?>? variableValues,
        ref bool hadConditions,
        int? orBranchIndex)
    {
        if (node is VariableNode vn)
        {
            if (variableValues != null && variableValues.TryGetValue(vn.Name.Value, out var varVal))
            {
                if (varVal is IValueNode ivn)
                {
                    CollectFilterColumnsFromNode(ivn, columns, variableValues, ref hadConditions, orBranchIndex);
                }
                else if (varVal is not null)
                {
                    CollectFilterColumnsFromDictionary(varVal, columns, ref hadConditions, orBranchIndex);
                }
            }
            else
            {
                hadConditions = true;
                columns.Add(UnresolvedVariableMarker);
            }

            return;
        }

        if (node is ObjectValueNode obj)
        {
            foreach (var field in obj.Fields)
            {
                hadConditions = true;
                var name = field.Name.Value;
                if (string.Equals(name, "and", StringComparison.OrdinalIgnoreCase))
                {
                    if (field.Value is ListValueNode list)
                    {
                        foreach (var item in list.Items)
                        {
                            CollectFilterColumnsFromNode(item, columns, variableValues, ref hadConditions, orBranchIndex);
                        }
                    }
                    else
                    {
                        CollectFilterColumnsFromNode(field.Value, columns, variableValues, ref hadConditions, orBranchIndex);
                    }
                }
                else if (string.Equals(name, "or", StringComparison.OrdinalIgnoreCase))
                {
                    if (field.Value is ListValueNode list)
                    {
                        for (int b = 0; b < list.Items.Count; b++)
                        {
                            var branchCols = new List<string>();
                            bool branchHadCond = false;
                            CollectFilterColumnsFromNode(list.Items[b], branchCols, variableValues, ref branchHadCond, orBranchIndex: b);
                            if (branchCols.Count == 0 || branchCols.Contains(UnusableFilterMarker))
                            {
                                columns.Add(UnusableFilterMarker);
                                return;
                            }
                            columns.AddRange(branchCols);
                        }
                    }
                    else
                    {
                        var branchCols = new List<string>();
                        bool branchHadCond = false;
                        CollectFilterColumnsFromNode(field.Value, branchCols, variableValues, ref branchHadCond, orBranchIndex: 0);
                        if (branchCols.Count == 0 || branchCols.Contains(UnusableFilterMarker))
                        {
                            columns.Add(UnusableFilterMarker);
                            return;
                        }
                        columns.AddRange(branchCols);
                    }
                }
                else if (string.Equals(name, "not", StringComparison.OrdinalIgnoreCase))
                {
                    // Inside 'not', do not credit inner columns as SARGable index matches
                }
                else
                {
                    // Column comparison
                    if (IsSargableNode(field.Value, variableValues, out bool isUnresolved))
                    {
                        AddColumn(columns, name, orBranchIndex);
                    }
                    else if (isUnresolved)
                    {
                        columns.Add(UnresolvedVariableMarker);
                    }
                }
            }
        }
    }

    private static bool IsSargableNode(IValueNode valNode, IReadOnlyDictionary<string, object?>? variableValues, out bool isUnresolved)
    {
        isUnresolved = false;
        if (valNode is VariableNode vn)
        {
            if (variableValues != null && variableValues.TryGetValue(vn.Name.Value, out var resolved))
            {
                if (resolved is IValueNode ivn)
                {
                    return IsSargableNode(ivn, variableValues, out isUnresolved);
                }
                return IsSargableRuntimeValue(resolved);
            }
            isUnresolved = true;
            return false;
        }

        if (valNode is ObjectValueNode opObj)
        {
            bool hasSeekable = false;
            foreach (var f in opObj.Fields)
            {
                var op = f.Name.Value;
                if (ScanOperators.Contains(op))
                {
                    return false;
                }

                if (SeekableOperators.Contains(op))
                {
                    hasSeekable = true;
                }
            }

            return hasSeekable;
        }

        // Direct scalar comparison (e.g., id: 1) is equivalent to eq -> SARGable
        return true;
    }

    private static bool IsSargableRuntimeValue(object? val)
    {
        if (val is null) return false;
        if (val is IDictionary<string, object?> dict)
        {
            bool hasSeekable = false;
            foreach (var key in dict.Keys)
            {
                if (ScanOperators.Contains(key))
                {
                    return false;
                }

                if (SeekableOperators.Contains(key))
                {
                    hasSeekable = true;
                }
            }

            return hasSeekable;
        }

        // Direct scalar comparison
        return true;
    }

    private static void AddColumn(List<string> columns, string columnName, int? orBranchIndex)
    {
        var formatted = orBranchIndex.HasValue
            ? $"{OrBranchPrefix}{orBranchIndex.Value}:{columnName}"
            : columnName;

        if (!columns.Contains(formatted, StringComparer.OrdinalIgnoreCase))
        {
            columns.Add(formatted);
        }
    }

    private static List<string>? ExtractFilterColumnsFromRuntimeValue(object? val)
    {
        if (val is null) return null;
        var columns = new List<string>();
        bool hadConditions = false;

        if (val is IValueNode vn)
        {
            CollectFilterColumnsFromNode(vn, columns, null, ref hadConditions, orBranchIndex: null);
        }
        else
        {
            CollectFilterColumnsFromDictionary(val, columns, ref hadConditions, orBranchIndex: null);
        }

        if (hadConditions && columns.Count == 0)
        {
            columns.Add(UnusableFilterMarker);
        }

        return columns.Count > 0 ? columns : null;
    }

    private static void CollectFilterColumnsFromDictionary(
        object? val,
        List<string> columns,
        ref bool hadConditions,
        int? orBranchIndex)
    {
        if (val is IDictionary<string, object?> dict)
        {
            foreach (var kvp in dict)
            {
                hadConditions = true;
                var key = kvp.Key;
                if (string.Equals(key, "and", StringComparison.OrdinalIgnoreCase))
                {
                    if (kvp.Value is IEnumerable<object> list)
                    {
                        foreach (var item in list)
                        {
                            CollectFilterColumnsFromDictionary(item, columns, ref hadConditions, orBranchIndex);
                        }
                    }
                    else
                    {
                        CollectFilterColumnsFromDictionary(kvp.Value, columns, ref hadConditions, orBranchIndex);
                    }
                }
                else if (string.Equals(key, "or", StringComparison.OrdinalIgnoreCase))
                {
                    if (kvp.Value is IEnumerable<object> list)
                    {
                        int b = 0;
                        foreach (var item in list)
                        {
                            var branchCols = new List<string>();
                            bool branchHadCond = false;
                            CollectFilterColumnsFromDictionary(item, branchCols, ref branchHadCond, orBranchIndex: b);
                            if (branchCols.Count == 0 || branchCols.Contains(UnusableFilterMarker))
                            {
                                columns.Add(UnusableFilterMarker);
                                return;
                            }
                            columns.AddRange(branchCols);
                            b++;
                        }
                    }
                    else
                    {
                        var branchCols = new List<string>();
                        bool branchHadCond = false;
                        CollectFilterColumnsFromDictionary(kvp.Value, branchCols, ref branchHadCond, orBranchIndex: 0);
                        if (branchCols.Count == 0 || branchCols.Contains(UnusableFilterMarker))
                        {
                            columns.Add(UnusableFilterMarker);
                            return;
                        }
                        columns.AddRange(branchCols);
                    }
                }
                else if (string.Equals(key, "not", StringComparison.OrdinalIgnoreCase))
                {
                    // Inside 'not', do not credit inner columns as SARGable
                }
                else
                {
                    if (IsSargableRuntimeValue(kvp.Value))
                    {
                        AddColumn(columns, key, orBranchIndex);
                    }
                }
            }
        }
    }
}

