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

        var stack = new Stack<(object? Left, object? Right)>();
        stack.Push((a, b));
        while (stack.Count > 0)
        {
            var (curA, curB) = stack.Pop();
            if (ReferenceEquals(curA, curB)) continue;
            if (curA is null || curB is null || curA.GetType() != curB.GetType()) return false;

            ExecutionStackGuard.Ensure();
            var type = curA.GetType();
            if (type.IsPrimitive || type.IsEnum || curA is string || curA is decimal)
            {
                if (!curA.Equals(curB)) return false;
                continue;
            }

            if (curA is IEnumerable left)
            {
                var leftItems = new List<object?>();
                foreach (var item in left) leftItems.Add(item);
                var rightEnum = ((IEnumerable)curB).GetEnumerator();
                int count = 0;
                var rightItems = new List<object?>(leftItems.Count);
                while (rightEnum.MoveNext())
                {
                    if (count >= leftItems.Count) return false;
                    rightItems.Add(rightEnum.Current);
                    count++;
                }

                if (count != leftItems.Count) return false;
                for (int i = leftItems.Count - 1; i >= 0; i--)
                {
                    stack.Push((leftItems[i], rightItems[i]));
                }

                continue;
            }

            var properties = AccessorsOf(type);
            if (properties.Length == 0)
            {
                if (!curA.Equals(curB)) return false;
                continue;
            }

            for (int i = properties.Length - 1; i >= 0; i--)
            {
                var prop = properties[i];
                stack.Push((prop.Get(curA), prop.Get(curB)));
            }
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

        var stack = new Stack<object?>();
        stack.Push(node);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current is null || current is string) continue;
            ExecutionStackGuard.Ensure();

            if (current is IList indexed)
            {
                for (int i = indexed.Count - 1; i >= 0; i--) stack.Push(indexed[i]);
                continue;
            }

            if (current is IEnumerable list)
            {
                var items = new List<object?>();
                foreach (var item in list) items.Add(item);
                for (int i = items.Count - 1; i >= 0; i--) stack.Push(items[i]);
                continue;
            }

            if (current is not SqlNode && current is not ITuple && !(current.GetType().IsValueType && current.GetType().Namespace?.StartsWith("TrinoSqlEngine", StringComparison.Ordinal) == true))
            {
                continue;
            }

            if (!visit(current)) continue;
            var accessors = AccessorsOf(current.GetType());
            for (int i = accessors.Length - 1; i >= 0; i--)
            {
                if (!accessors[i].Descend) continue;
                stack.Push(accessors[i].Get(current));
            }
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

    private static void Referenced(object? root, bool insideSubquery, ImmutableHashSet<string>.Builder columns)
    {
        if (root is null || root is string) return;

        var stack = new Stack<(object? Node, bool InsideSubquery)>();
        stack.Push((root, insideSubquery));
        while (stack.Count > 0)
        {
            var (node, currentInsideSubquery) = stack.Pop();
            if (node is null || node is string) continue;
            ExecutionStackGuard.Ensure();
            if (node is IList indexed)
            {
                for (int i = indexed.Count - 1; i >= 0; i--) stack.Push((indexed[i], currentInsideSubquery));
                continue;
            }

            if (node is IEnumerable list)
            {
                var items = new List<object?>();
                foreach (var item in list) items.Add(item);
                for (int i = items.Count - 1; i >= 0; i--) stack.Push((items[i], currentInsideSubquery));
                continue;
            }

            if (node is not SqlNode) continue;
            if (node is ColumnReference column)
            {
                var parts = column.Name.Parts;
                if (!currentInsideSubquery)
                {
                    columns.Add(column.Name.SimpleName.ToLowerInvariant());
                }
                else if (parts.Count >= 2 && string.Equals(parts[^2].Value, RowFilterAliases.Target, StringComparison.OrdinalIgnoreCase))
                {
                    columns.Add(column.Name.SimpleName.ToLowerInvariant());
                }

                continue;
            }

            bool nowInside = currentInsideSubquery || node is SelectStatement;
            var accessors = AccessorsOf(node.GetType());
            for (int i = accessors.Length - 1; i >= 0; i--)
            {
                if (!accessors[i].Descend) continue;
                stack.Push((accessors[i].Get(node), nowInside));
            }
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

    private enum SerializeFrameKind
    {
        Value,
        AppendChar,
        AppendPropPrefix
    }

    private static void Serialize(object? rootValue, StringBuilder sb)
    {
        var stack = new Stack<(SerializeFrameKind Kind, object? Data, char Ch)>();
        stack.Push((SerializeFrameKind.Value, rootValue, '\0'));
        while (stack.Count > 0)
        {
            var (kind, data, ch) = stack.Pop();
            if (kind == SerializeFrameKind.AppendChar)
            {
                sb.Append(ch);
                continue;
            }

            if (kind == SerializeFrameKind.AppendPropPrefix)
            {
                sb.Append((string)data!).Append('=');
                continue;
            }

            ExecutionStackGuard.Ensure();
            switch (data)
            {
                case null:
                    sb.Append('~');
                    break;
                case string s:
                    sb.Append('"').Append(s.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(s).Append('"');
                    break;
                case bool or char or Enum or IConvertible:
                    sb.Append(Convert.ToString(data, CultureInfo.InvariantCulture)).Append(';');
                    break;
                case IEnumerable list:
                {
                    sb.Append('[');
                    stack.Push((SerializeFrameKind.AppendChar, null, ']'));
                    var items = new List<object?>();
                    foreach (var item in list) items.Add(item);
                    for (int i = items.Count - 1; i >= 0; i--)
                    {
                        stack.Push((SerializeFrameKind.Value, items[i], '\0'));
                    }

                    break;
                }
                default:
                {
                    var type = data.GetType();
                    sb.Append(type.Name).Append('{');
                    stack.Push((SerializeFrameKind.AppendChar, null, '}'));
                    var accessors = AccessorsOf(type);
                    for (int i = accessors.Length - 1; i >= 0; i--)
                    {
                        stack.Push((SerializeFrameKind.Value, accessors[i].Get(data), '\0'));
                        stack.Push((SerializeFrameKind.AppendPropPrefix, accessors[i].Name, '\0'));
                    }

                    break;
                }
            }
        }
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
