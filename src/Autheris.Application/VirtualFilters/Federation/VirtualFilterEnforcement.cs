namespace Autheris.Application.VirtualFilters.Federation;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Domain.Exceptions;

/// <summary>
/// F-GOV-14 (M3): the gateway semi-join for one protected table. For every applied (filter, binding) pair it holds the
/// target columns (after the binding's column map), their key types (from the catalog) and the authorized key set.
/// <see cref="Apply"/> keeps a row only if, for every pair, the row's key tuple is in that pair's set (AND, AC-3.3).
/// Only Autheris.Application can create it; it is passed to the governed read path explicitly, never through
/// <c>session.Items</c> (C-6, SEC-14).
/// </summary>
public sealed class VirtualFilterEnforcement
{
    private readonly PairEnforcement[] _pairs;

    private VirtualFilterEnforcement(TableIdentifier table, PairEnforcement[] pairs)
    {
        Table = table;
        _pairs = pairs;
        Bindings = pairs.Select(p => p.Pair).ToArray();
    }

    /// <summary>The protected table this enforcement was built for.</summary>
    public TableIdentifier Table { get; }

    /// <summary>The (filter, binding) pairs this enforcement covers.</summary>
    public IReadOnlyList<AppliedVirtualFilterBinding> Bindings { get; }

    /// <summary>
    /// Builds the enforcement for <paramref name="target"/>. Denies when a pair cannot be enforced exactly: a sql
    /// definition or validity window (<c>VF_KEY_SOURCE_UNSUPPORTED</c>), a target column that is invalid or missing
    /// (<c>VF_BINDING_MISCONFIGURED</c>), a catalog type without a safe comparison or a key set of other key types
    /// (<c>VF_KEY_TYPE_MISMATCH</c>), or no pair at all (<c>VF_ENFORCEMENT_INCOMPLETE</c>).
    /// </summary>
    internal static VirtualFilterEnforcement Create(
        TableMetadata target,
        IReadOnlyList<(AppliedVirtualFilterBinding Pair, VirtualFilterKeySet Keys)> pairs)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(pairs);
        if (pairs.Count == 0)
        {
            throw new GatewaySecurityException(
                $"Virtual filters on '{target.Identifier}' have no enforcement.", VirtualFilterErrorCodes.EnforcementIncomplete);
        }

        var built = new PairEnforcement[pairs.Count];
        for (int i = 0; i < built.Length; i++)
        {
            var (pair, keys) = pairs[i];
            ArgumentNullException.ThrowIfNull(pair);
            ArgumentNullException.ThrowIfNull(keys);
            var columns = TargetColumns(target, pair);
            var types = KeyTypes(target, columns);
            if (!types.SequenceEqual(keys.KeyTypes))
            {
                throw new GatewaySecurityException(
                    $"The key set of the Virtual Filter '{pair.Filter.Name}' does not have the key types of '{target.Identifier}'.",
                    VirtualFilterErrorCodes.KeyTypeMismatch);
            }

            built[i] = new PairEnforcement(pair, columns, types, keys);
        }

        return new VirtualFilterEnforcement(target.Identifier, built);
    }

    /// <summary>
    /// Target columns of a pair (catalog spelling): the filter's key columns renamed by the binding's column map.
    /// Throws when the pair cannot be enforced from authorized key tuples.
    /// </summary>
    internal static string[] TargetColumns(TableMetadata target, AppliedVirtualFilterBinding pair)
    {
        var filter = pair.Filter;
        if (filter.Sql != null || filter.Structured == null)
        {
            throw new GatewaySecurityException(
                $"The Virtual Filter '{filter.Name}' is not a structured filter and cannot be enforced on '{target.Identifier}'.",
                VirtualFilterErrorCodes.KeySourceUnsupported);
        }

        if (filter.ValidFromColumn != null || filter.ValidToColumn != null)
        {
            throw new GatewaySecurityException(
                $"The Virtual Filter '{filter.Name}' has a validity window, which cannot be enforced on '{target.Identifier}'.",
                VirtualFilterErrorCodes.KeySourceUnsupported);
        }

        var keyColumns = filter.TargetKeyColumns;
        if (keyColumns.Count == 0)
        {
            throw Misconfigured(filter.Name, target);
        }

        var columns = new string[keyColumns.Count];
        for (int i = 0; i < columns.Length; i++)
        {
            var key = keyColumns[i];
            var mapped = pair.Binding.ColumnMap != null && pair.Binding.ColumnMap.TryGetValue(key, out var m) ? m : key;
            try
            {
                VirtualFilterNames.ValidateIdentifier(mapped, "ColumnMap");
            }
            catch (ArgumentException)
            {
                throw Misconfigured(filter.Name, target);
            }

            columns[i] = target.GetColumn(mapped)?.ColumnName ?? throw Misconfigured(filter.Name, target);
        }

        if (columns.Distinct(StringComparer.OrdinalIgnoreCase).Count() != columns.Length)
        {
            throw Misconfigured(filter.Name, target);
        }

        return columns;
    }

    /// <summary>Key types of the target columns, from the catalog.</summary>
    internal static VirtualFilterKeyType[] KeyTypes(TableMetadata target, IReadOnlyList<string> columns) =>
        columns.Select(c => VirtualFilterKeyCanonicalizer.ForColumnType(target.GetColumn(c)?.DataType, $"{target.Identifier}.{c}")).ToArray();

    /// <summary>The rows whose key tuples are authorized by every pair; order is preserved, nothing is duplicated.</summary>
    public List<IReadOnlyDictionary<string, object?>> Apply(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var kept = new List<IReadOnlyDictionary<string, object?>>(rows.Count);
        foreach (var row in rows)
        {
            if (row != null && _pairs.All(p => p.Authorizes(row)))
            {
                kept.Add(row);
            }
        }

        return kept;
    }

    private static GatewaySecurityException Misconfigured(string filter, TableMetadata target) =>
        new($"The binding of the Virtual Filter '{filter}' does not map its keys to columns of '{target.Identifier}'.",
            VirtualFilterErrorCodes.BindingMisconfigured);

    private sealed class PairEnforcement(AppliedVirtualFilterBinding pair, string[] columns, VirtualFilterKeyType[] types, VirtualFilterKeySet keys)
    {
        public AppliedVirtualFilterBinding Pair { get; } = pair;

        public bool Authorizes(IReadOnlyDictionary<string, object?> row)
        {
            var values = new object?[columns.Length];
            for (int i = 0; i < columns.Length; i++)
            {
                if (!row.TryGetValue(columns[i], out values[i]))
                {
                    return false;
                }
            }

            return VirtualFilterKey.TryCreate(types, values, out var key) && keys.Contains(key);
        }
    }
}
