namespace Autheris.GraphQL.Interceptors;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Application.Data.Services;
using Autheris.Domain.Model;
using HotChocolate.Language;

/// <summary>
/// Issue #421 (WP-D of #417): Evaluates whether a GraphQL root-list plan can utilize a database index
/// based on catalog metadata (declared primary keys, secondary/unique indexes, and source physical indexes).
/// Zero live stats, zero-allocation friendly, fail-closed anti-enumeration.
/// </summary>
public static class IndexUsabilityEvaluator
{
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

        var candidates = GetCandidateIndexes(effectiveMeta);
        if (candidates.Count == 0)
        {
            return false;
        }

        bool hasExplicitOrder = orderBy is not null && orderBy.Count > 0;
        bool hasFilter = filterColumns is not null && filterColumns.Count > 0;

        if (hasExplicitOrder)
        {
            // When an explicit orderBy is specified, the database requires a matching index prefix
            // (matching column names and ASC/DESC directions) to avoid a full unindexed sort.
            bool orderMatched = false;
            foreach (var candidate in candidates)
            {
                if (CandidateMatchesOrder(candidate, orderBy!))
                {
                    orderMatched = true;
                    break;
                }
            }

            return orderMatched;
        }

        if (hasFilter)
        {
            // If no explicit orderBy is specified, check if any candidate index matches the filter's leading column
            // or if the table has a primary key for default ordering.
            bool filterMatched = false;
            foreach (var candidate in candidates)
            {
                if (candidate.Columns.Count > 0 &&
                    filterColumns!.Any(fc => string.Equals(fc, candidate.Columns[0], StringComparison.OrdinalIgnoreCase)))
                {
                    filterMatched = true;
                    break;
                }
            }

            if (filterMatched)
            {
                return true;
            }

            // Fallback: If filter is on non-indexed columns, but table has a primary key, default ordering uses PK
            // but the filter still requires a full scan. Unindexed filter -> unindexed plan.
            return false;
        }

        // Neither explicit orderBy nor where filter: uses default PK/unique index scan.
        return effectiveMeta.PrimaryKeyColumns.Count > 0 ||
               effectiveMeta.Indexes.Any(i => i.IsUnique);
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
        if (literal is VariableNode varNode && variableValues != null &&
            variableValues.TryGetValue(varNode.Name.Value, out var varVal))
        {
            return ExtractOrderByFromRuntimeValue(varVal);
        }

        var orders = new List<(string FieldName, string Direction)>();

        if (literal is ListValueNode listNode)
        {
            foreach (var item in listNode.Items)
            {
                var itemLit = item is VariableNode vn && variableValues != null &&
                              variableValues.TryGetValue(vn.Name.Value, out var iv)
                    ? iv
                    : item;

                if (itemLit is ObjectValueNode obj)
                {
                    AddOrderEntry(obj, orders);
                }
                else if (itemLit is not null)
                {
                    var runtimeOrders = ExtractOrderByFromRuntimeValue(itemLit);
                    if (runtimeOrders is not null)
                    {
                        orders.AddRange(runtimeOrders);
                    }
                }
            }
        }
        else if (literal is ObjectValueNode singleObj)
        {
            AddOrderEntry(singleObj, orders);
        }

        return orders;
    }

    private static void AddOrderEntry(ObjectValueNode obj, List<(string FieldName, string Direction)> orders)
    {
        foreach (var f in obj.Fields)
        {
            var dir = f.Value switch
            {
                EnumValueNode ev => ev.Value,
                StringValueNode sv => sv.Value,
                _ => "ASC"
            };
            orders.Add((f.Name.Value, dir));
        }
    }

    private static List<(string FieldName, string Direction)>? ExtractOrderByFromRuntimeValue(object? val)
    {
        if (val is null) return null;
        var result = new List<(string FieldName, string Direction)>();

        if (val is IEnumerable<object> list)
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
        if (literal is VariableNode varNode && variableValues != null &&
            variableValues.TryGetValue(varNode.Name.Value, out var varVal))
        {
            return ExtractFilterColumnsFromRuntimeValue(varVal);
        }

        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectFilterColumnsFromNode(literal, columns);
        return columns.Count > 0 ? columns.ToList() : null;
    }

    private static void CollectFilterColumnsFromNode(IValueNode node, HashSet<string> columns)
    {
        if (node is ObjectValueNode obj)
        {
            foreach (var field in obj.Fields)
            {
                var name = field.Name.Value;
                if (string.Equals(name, "and", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, "or", StringComparison.OrdinalIgnoreCase))
                {
                    if (field.Value is ListValueNode list)
                    {
                        foreach (var item in list.Items)
                        {
                            CollectFilterColumnsFromNode(item, columns);
                        }
                    }
                    else
                    {
                        CollectFilterColumnsFromNode(field.Value, columns);
                    }
                }
                else if (string.Equals(name, "not", StringComparison.OrdinalIgnoreCase))
                {
                    CollectFilterColumnsFromNode(field.Value, columns);
                }
                else
                {
                    columns.Add(name);
                }
            }
        }
    }

    private static List<string>? ExtractFilterColumnsFromRuntimeValue(object? val)
    {
        if (val is null) return null;
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectFilterColumnsFromDictionary(val, columns);
        return columns.Count > 0 ? columns.ToList() : null;
    }

    private static void CollectFilterColumnsFromDictionary(object? val, HashSet<string> columns)
    {
        if (val is IDictionary<string, object?> dict)
        {
            foreach (var kvp in dict)
            {
                var key = kvp.Key;
                if (string.Equals(key, "and", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(key, "or", StringComparison.OrdinalIgnoreCase))
                {
                    if (kvp.Value is IEnumerable<object> list)
                    {
                        foreach (var item in list)
                        {
                            CollectFilterColumnsFromDictionary(item, columns);
                        }
                    }
                    else
                    {
                        CollectFilterColumnsFromDictionary(kvp.Value, columns);
                    }
                }
                else if (string.Equals(key, "not", StringComparison.OrdinalIgnoreCase))
                {
                    CollectFilterColumnsFromDictionary(kvp.Value, columns);
                }
                else
                {
                    columns.Add(key);
                }
            }
        }
    }
}
