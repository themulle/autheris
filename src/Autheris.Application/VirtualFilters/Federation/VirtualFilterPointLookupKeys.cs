namespace Autheris.Application.VirtualFilters.Federation;

using System;
using System.Collections.Generic;
using TrinoSqlEngine.Ast.Nodes;

/// <summary>
/// F-GOV-14 (WI-7, M6): turns the literals of a point lookup into canonical key tuples of one (filter, binding) pair.
/// Anything that is not exact returns null (the pair is then not decided by the query, the table is read as a scan):
/// a key column without a predicate, a literal that does not convert, a text column compared with a number, or more
/// tuples than the cap.
/// </summary>
internal static class VirtualFilterPointLookupKeys
{
    public static VirtualFilterKey[]? TryBuild(
        IReadOnlyDictionary<string, IReadOnlyList<PointLiteral>> lookup,
        IReadOnlyList<string> columns,
        IReadOnlyList<VirtualFilterKeyType> types,
        int maxTuples)
    {
        var perColumn = new List<object>[columns.Count];
        long product = 1;
        for (int c = 0; c < columns.Count; c++)
        {
            if (!lookup.TryGetValue(columns[c], out var literals) || literals.Count == 0)
            {
                return null;
            }

            product *= literals.Count;
            if (product > maxTuples)
            {
                return null;
            }

            perColumn[c] = new List<object>(literals.Count);
            foreach (var literal in literals)
            {
                // A text column is only matched by text literals: DuckDB would cast a number differently than the gateway.
                if ((types[c] == VirtualFilterKeyType.Text && literal.Type != LiteralType.String) ||
                    !VirtualFilterKeyCanonicalizer.TryCanonicalize(types[c], literal.Value, out var canonical))
                {
                    return null;
                }

                perColumn[c].Add(canonical);
            }
        }

        var keys = new List<VirtualFilterKey>((int)product);
        var current = new object?[columns.Count];
        Expand(perColumn, 0, current, types, keys);
        return [.. keys];
    }

    private static void Expand(List<object>[] perColumn, int index, object?[] current, IReadOnlyList<VirtualFilterKeyType> types, List<VirtualFilterKey> keys)
    {
        if (index == perColumn.Length)
        {
            if (VirtualFilterKey.TryCreate(types, current, out var key))
            {
                keys.Add(key);
            }

            return;
        }

        foreach (var value in perColumn[index])
        {
            current[index] = value;
            Expand(perColumn, index + 1, current, types, keys);
        }
    }
}
