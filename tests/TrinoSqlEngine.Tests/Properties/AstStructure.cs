using System.Collections;
using System.Reflection;
using TrinoSqlEngine.Ast.Nodes;

namespace TrinoSqlEngine.Tests.Properties;

/// <summary>Structural equality of AST nodes, ignoring <see cref="ParenthesizedExpression"/> wrappers. Records compare lists by reference, so the property gate needs its own comparison.</summary>
internal static class AstStructure
{
    /// <summary>Returns null when the trees are equal, otherwise the path of the first difference.</summary>
    public static string? FirstDifference(object? a, object? b, string path = "$")
    {
        // Parentheses are syntax: the tree already encodes the grouping, and the builder drops redundant ones in some positions.
        while (a is ParenthesizedExpression pa)
        {
            a = pa.Expression;
        }

        while (b is ParenthesizedExpression pb)
        {
            b = pb.Expression;
        }

        if (a is null || b is null)
        {
            return a is null && b is null ? null : $"{path}: {(a is null ? "null" : a.GetType().Name)} vs {(b is null ? "null" : b.GetType().Name)}";
        }

        if (a.GetType() != b.GetType())
        {
            return $"{path}: {a.GetType().Name} vs {b.GetType().Name}";
        }

        if (a is string or ValueType && !(a.GetType().IsGenericType && a.GetType().Name.StartsWith("ValueTuple", StringComparison.Ordinal)))
        {
            return Equals(a, b) ? null : $"{path}: {a} vs {b}";
        }

        if (a is IEnumerable left && b is IEnumerable right)
        {
            var l = left.Cast<object?>().ToList();
            var r = right.Cast<object?>().ToList();
            if (l.Count != r.Count)
            {
                return $"{path}: {l.Count} vs {r.Count} elements";
            }

            for (int i = 0; i < l.Count; i++)
            {
                if (FirstDifference(l[i], r[i], $"{path}[{i}]") is { } d)
                {
                    return d;
                }
            }

            return null;
        }

        foreach (var property in a.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetIndexParameters().Length == 0 && p.Name != "EqualityContract"))
        {
            if (FirstDifference(property.GetValue(a), property.GetValue(b), $"{path}.{property.Name}") is { } d)
            {
                return d;
            }
        }

        return null;
    }
}
