using System.Collections.Concurrent;
using System.Reflection;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Nodes;

namespace TrinoSqlEngine.Tests.Conformance;

/// <summary>The pure checks of the G2 gate. They are separate from the xUnit classes so that negative controls can feed them broken input.</summary>
internal static class ConformanceVerifier
{
    /// <summary>Null when the observation matches the golden entry; otherwise a short description of the first difference.</summary>
    public static string? Mismatch(ConformanceFixture fixture, ConformanceObservation observed)
    {
        if (fixture.Outcome != observed.Outcome) return $"outcome: expected {fixture.Outcome}, got {observed.Outcome}";
        if (fixture.ExceptionType != observed.ExceptionType) return $"exception: expected {fixture.ExceptionType}, got {observed.ExceptionType}";
        if (fixture.Expected != observed.Sql) return $"sql: expected [{fixture.Expected}], got [{observed.Sql}]";
        if (!fixture.Parameters.SequenceEqual(observed.Parameters)) return "parameter list differs";
        return null;
    }

    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> Properties = new();

    /// <summary>Every concrete node type reachable from <paramref name="root"/> (reflection walk over record properties).</summary>
    public static void CollectNodeTypes(object? root, ISet<Type> into)
    {
        switch (root)
        {
            case null or string:
                return;
            case SqlNode node:
                if (!into.Add(node.GetType())) { /* still walk: children can differ */ }
                foreach (var p in Properties.GetOrAdd(node.GetType(), t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                             .Where(x => x.GetIndexParameters().Length == 0 && x.Name != "EqualityContract").ToArray()))
                {
                    CollectNodeTypes(p.GetValue(node), into);
                }
                return;
            case System.Collections.IEnumerable items:
                foreach (var item in items) CollectNodeTypes(item, into);
                return;
        }
    }

    /// <summary>The concrete node families that need a conformance fixture or an explicit rejection: expressions, statements and their structural parts.</summary>
    public static IReadOnlyList<Type> RequiredNodeTypes() => typeof(SqlNode).Assembly.GetTypes()
        .Where(t => t is { IsAbstract: false, IsClass: true } && typeof(SqlNode).IsAssignableFrom(t))
        .Where(t => typeof(Expression).IsAssignableFrom(t) || typeof(SqlStatement).IsAssignableFrom(t) || typeof(QueryBody).IsAssignableFrom(t)
                    || typeof(TableSource).IsAssignableFrom(t) || typeof(SelectItem).IsAssignableFrom(t) || typeof(MergeClause).IsAssignableFrom(t)
                    || typeof(JoinCondition).IsAssignableFrom(t))
        .OrderBy(t => t.Name, StringComparer.Ordinal)
        .ToArray();

    /// <summary>Node types that have neither an accepted fixture nor an explicit rejection entry.</summary>
    public static IReadOnlyList<string> Uncovered(IEnumerable<Type> required, ISet<string> covered, ISet<string> rejected) =>
        required.Select(t => t.Name).Where(n => !covered.Contains(n) && !rejected.Contains(n)).ToArray();

    /// <summary>The first dialect-specific exemption: a dialect without a capability entry cannot generate yet (SQLite until WP-X0h merges).</summary>
    public static bool IsPending(TargetSqlDialect dialect)
    {
        try { DialectCapabilityTable.Default.Get(dialect); return false; }
        catch (ArgumentOutOfRangeException) { return true; }
    }
}
