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

    /// <summary>One readable property of an AST type: a typed getter delegate (no <c>PropertyInfo.GetValue</c> reflection call per read).</summary>
    private sealed record Accessor(string Name, Func<object, object?> Get, bool Descend);

    private static readonly ConcurrentDictionary<Type, Accessor[]> Accessors = new();

    // Property order, filter and values are exactly those of PropertiesOf/GetValue; only the way a value is read changes.
    private static Accessor[] AccessorsOf(Type type) => Accessors.GetOrAdd(type, static t =>
        Array.ConvertAll(PropertiesOf(t), static p => new Accessor(
            p.Name,
            MakeGetter(p),
            Descend: !(p.PropertyType.IsPrimitive || p.PropertyType.IsEnum || p.PropertyType == typeof(string)))));

    private static Func<object, object?> MakeGetter(PropertyInfo property) => PropertyGetters.Create(property);

    /// <summary>Structural equality of two AST values (records, lists, primitives); list elements are compared in order.</summary>
    public static bool StructurallyEqual(object? a, object? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null || a.GetType() != b.GetType()) return false;
        ExecutionStackGuard.Ensure();
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

        var properties = AccessorsOf(type);
        if (properties.Length == 0) return a.Equals(b);
        foreach (var property in properties)
        {
            if (!StructurallyEqual(property.Get(a), property.Get(b))) return false;
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
        ExecutionStackGuard.Ensure();

        if (node is IList indexed)
        {
            for (int i = 0; i < indexed.Count; i++) Walk(indexed[i], visit);
            return;
        }

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
        foreach (var property in AccessorsOf(node.GetType()))
        {
            if (!property.Descend) continue;
            Walk(property.Get(node), visit);
        }
    }

    /// <summary>
    /// <see cref="Children(object, Action{object})"/> with a caller state, so a <c>static</c> lambda can be used and no closure is
    /// allocated per visited node.
    /// </summary>
    public static void Children<TState>(object node, TState state, Action<object, TState> visit)
    {
        if (node is IList indexed && node is not string)
        {
            for (int i = 0; i < indexed.Count; i++) if (indexed[i] is { } item) visit(item, state);
            return;
        }

        if (node is IEnumerable list && node is not string)
        {
            foreach (var item in list) if (item is not null) visit(item, state);
            return;
        }

        if (node is not SqlNode) return;
        foreach (var property in AccessorsOf(node.GetType()))
        {
            if (!property.Descend) continue;
            if (property.Get(node) is { } value) visit(value, state);
        }
    }

    /// <summary>The direct AST children of <paramref name="node"/> (list elements are flattened; non-node values are skipped).</summary>
    public static void Children(object node, Action<object> visit)
    {
        if (node is IEnumerable list && node is not string)
        {
            foreach (var item in list) if (item is not null) visit(item);
            return;
        }

        if (node is not SqlNode) return;
        foreach (var property in AccessorsOf(node.GetType()))
        {
            if (!property.Descend) continue;
            if (property.Get(node) is { } value) visit(value);
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
        ExecutionStackGuard.Ensure();
        if (node is IList indexed)
        {
            for (int i = 0; i < indexed.Count; i++) Referenced(indexed[i], insideSubquery, columns);
            return;
        }

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
        foreach (var property in AccessorsOf(node.GetType()))
        {
            if (!property.Descend) continue;
            Referenced(property.Get(node), nowInside, columns);
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
        ExecutionStackGuard.Ensure();
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
        foreach (var property in AccessorsOf(type))
        {
            sb.Append(property.Name).Append('=');
            Serialize(property.Get(value), sb);
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


/// <summary>
/// Typed getter delegates for public properties (instead of <c>PropertyInfo.GetValue</c> on every read). A <c>bool</c> result reuses two
/// shared boxes; other value types are boxed per read, exactly like <c>GetValue</c> does.
/// </summary>
internal static class PropertyGetters
{
    private static readonly object True = true;
    private static readonly object False = false;

    public static Func<object, object?> Create(PropertyInfo property)
    {
        if (property.DeclaringType!.IsValueType)
        {
            return property.GetValue;   // an open instance delegate over a struct getter needs a by-ref receiver; structs stay on reflection
        }

        var adapt = typeof(PropertyGetters).GetMethod(nameof(Adapt), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(property.DeclaringType!, property.PropertyType);
        return (Func<object, object?>)adapt.Invoke(null, [property.GetMethod!])!;
    }

    private static Func<object, object?> Adapt<TTarget, TValue>(MethodInfo getter)
        where TTarget : class
    {
        var typed = (Func<TTarget, TValue>)Delegate.CreateDelegate(typeof(Func<TTarget, TValue>), getter);
        if (typeof(TValue) == typeof(bool))
        {
            var flag = (Func<TTarget, bool>)(object)typed;
            return target => flag((TTarget)target) ? True : False;
        }

        return target => typed((TTarget)target);
    }
}
