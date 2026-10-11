namespace TrinoSqlEngine.Ast.Security;

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using TrinoSqlEngine.Ast.Nodes;

/// <summary>
/// Generic structural walk over AST records. It is used where the work is off the hot path and a new node type must be
/// covered automatically (policy IR collection, fingerprints), so a forgotten node can never hide a parameter or column.
/// </summary>
internal static class AstReflection
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> Properties = new();

    private static PropertyInfo[] PropertiesOf(Type type) => Properties.GetOrAdd(type, static t =>
        Array.FindAll(t.GetProperties(BindingFlags.Public | BindingFlags.Instance), static p =>
            p.GetIndexParameters().Length == 0 && p.GetMethod != null));

    /// <summary>Structural equality of two AST values (records, lists, primitives); list elements are compared in order.</summary>
    public static bool StructurallyEqual(object? a, object? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null || a.GetType() != b.GetType()) return false;
        RuntimeHelpers.EnsureSufficientExecutionStack();
        var type = a.GetType();
        if (type.IsPrimitive || type.IsEnum || a is string || a is decimal) return a.Equals(b);
        if (a is IEnumerable left)
        {
            var right = ((IEnumerable)b).GetEnumerator();
            foreach (var item in left)
            {
                if (!right.MoveNext() || !StructurallyEqual(item, right.Current)) return false;
            }

            return !right.MoveNext();
        }

        var properties = PropertiesOf(type);
        if (properties.Length == 0) return a.Equals(b);
        foreach (var property in properties)
        {
            if (!StructurallyEqual(property.GetValue(a), property.GetValue(b))) return false;
        }

        return true;
    }

    public static List<T> Collect<T>(object? root) where T : class
    {
        var result = new List<T>();
        Walk(root, node =>
        {
            if (node is T match) result.Add(match);
            return true;
        });
        return result;
    }

    /// <summary>Walks every <see cref="SqlNode"/> and list element below <paramref name="node"/>; <paramref name="visit"/> returns false to skip children.</summary>
    public static void Walk(object? node, Func<object, bool> visit)
    {
        if (node is null || node is string) return;
        RuntimeHelpers.EnsureSufficientExecutionStack();

        if (node is IEnumerable list)
        {
            foreach (var item in list) Walk(item, visit);
            return;
        }

        if (node is not SqlNode && node is not ITuple && !(node.GetType().IsValueType && node.GetType().Namespace?.StartsWith("TrinoSqlEngine", StringComparison.Ordinal) == true))
        {
            return;
        }

        if (!visit(node)) return;
        foreach (var property in PropertiesOf(node.GetType()))
        {
            var type = property.PropertyType;
            if (type.IsPrimitive || type.IsEnum || type == typeof(string)) continue;
            Walk(property.GetValue(node), visit);
        }
    }

    /// <summary>Lower-case names of columns of the secured (target) table that <paramref name="expression"/> references.</summary>
    public static ImmutableHashSet<string> ReferencedTargetColumns(Expression expression)
    {
        var columns = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        Referenced(expression, insideSubquery: false, columns);
        return columns.ToImmutable();
    }

    private static void Referenced(object? node, bool insideSubquery, ImmutableHashSet<string>.Builder columns)
    {
        if (node is null || node is string) return;
        RuntimeHelpers.EnsureSufficientExecutionStack();
        if (node is IEnumerable list)
        {
            foreach (var item in list) Referenced(item, insideSubquery, columns);
            return;
        }

        if (node is not SqlNode) return;
        if (node is ColumnReference column)
        {
            var parts = column.Name.Parts;
            if (!insideSubquery)
            {
                columns.Add(column.Name.SimpleName.ToLowerInvariant());
            }
            else if (parts.Count >= 2 && string.Equals(parts[^2].Value, RowFilterAliases.Target, StringComparison.OrdinalIgnoreCase))
            {
                columns.Add(column.Name.SimpleName.ToLowerInvariant());
            }

            return;
        }

        bool nowInside = insideSubquery || node is SelectStatement;
        foreach (var property in PropertiesOf(node.GetType()))
        {
            var type = property.PropertyType;
            if (type.IsPrimitive || type.IsEnum || type == typeof(string)) continue;
            Referenced(property.GetValue(node), nowInside, columns);
        }
    }

    private static readonly ConditionalWeakTable<object, string> FingerprintMemo = new();

    /// <summary>
    /// Fingerprint of an immutable record instance, memoized per instance (CR-ADG-12): a cache hit re-validates every table
    /// dependency, and re-serializing an unchanged catalog entry or mask specification by reflection on each hit is wasted work.
    /// Only instances that are never mutated may be passed (catalog entries, mask arguments).
    /// </summary>
    public static string FingerprintMemoized(object immutableRoot)
    {
        ArgumentNullException.ThrowIfNull(immutableRoot);
        return FingerprintMemo.GetValue(immutableRoot, static root => Fingerprint(root));
    }

    public static string Fingerprint(object root)
    {
        var sb = new StringBuilder(256);
        Serialize(root, sb);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private static void Serialize(object? value, StringBuilder sb)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        switch (value)
        {
            case null:
                sb.Append('~');
                return;
            case string s:
                sb.Append('"').Append(s.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(s).Append('"');
                return;
            case bool or char or Enum or IConvertible:
                sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture)).Append(';');
                return;
            case IEnumerable list:
                sb.Append('[');
                foreach (var item in list) Serialize(item, sb);
                sb.Append(']');
                return;
        }

        var type = value.GetType();
        sb.Append(type.Name).Append('{');
        foreach (var property in PropertiesOf(type))
        {
            sb.Append(property.Name).Append('=');
            Serialize(property.GetValue(value), sb);
        }

        sb.Append('}');
    }
}

/// <summary>Stable SHA-256 shape fingerprint of AST fragments (values that live in parameters are not part of it).</summary>
public static class AstFingerprint
{
    public static string Compute(SqlNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return AstReflection.Fingerprint(node);
    }
}
