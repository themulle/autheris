namespace TrinoSqlEngine.Governance;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Security;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Security;

/// <summary>Two policy predicates bind the same parameter name to different values or types, or a value is missing (fail closed).</summary>
public sealed class PolicyConflictException : SecurityException
{
    public PolicyConflictException(string message) : base(message)
    {
    }
}

/// <summary>The tenant value the compiler binds for every tenant-scoped table (never request text).</summary>
public sealed record TenantBinding(string ParameterName, object Value, SqlParameterType Type)
{
    // INV-16: the tenant value never appears in logs, spans or exception text.
    public override string ToString() => $"TenantBinding {{ ParameterName = {ParameterName}, Type = {Type} }}";
}

/// <summary>
/// A typed row-level-security predicate (plan 4.4): an AST that contains <see cref="PolicyParameterExpression"/> nodes and
/// never values, plus the values by parameter name.
/// </summary>
/// <param name="Expression">Predicate over the secured table. Contains parameter nodes, never raw values (INV-5).</param>
/// <param name="Parameters">Values by parameter name; they are bound, never inlined.</param>
/// <param name="Fingerprint">SHA-256 hex of the predicate shape (nodes, parameter names and types). Values are not part of it.</param>
/// <param name="ReferencedColumns">Lower-case names of target-table columns the predicate references (SEC-ADG-06).</param>
public sealed record PolicyPredicate(
    Expression Expression,
    FrozenDictionary<string, PolicyValue> Parameters,
    string Fingerprint,
    ImmutableHashSet<string> ReferencedColumns)
{
    /// <summary>Deny-all: the canonical <c>1 = 0</c>. A table without access gets this, never null (fail closed).</summary>
    public static PolicyPredicate DenyAll { get; } = Create(
        new BinaryExpression(new LiteralExpression(1L, LiteralType.Integer), BinaryOperator.Equal, new LiteralExpression(0L, LiteralType.Integer)),
        new Dictionary<string, PolicyValue>());

    /// <summary>Creates a predicate; every parameter node must have a value of its declared type.</summary>
    public static PolicyPredicate Create(Expression expression, IReadOnlyDictionary<string, PolicyValue> parameters)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(parameters);

        var nodes = AstReflection.Collect<PolicyParameterExpression>(expression);
        foreach (var node in nodes)
        {
            if (!parameters.TryGetValue(node.Name, out var value) || value.Type != node.Type)
            {
                throw new PolicyConflictException($"Policy parameter '{node.Name}' has no value of the declared type.");
            }
        }

        var used = nodes.Select(n => n.Name).ToHashSet(StringComparer.Ordinal);
        var kept = parameters.Where(p => used.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        return new PolicyPredicate(
            expression,
            kept.ToFrozenDictionary(StringComparer.Ordinal),
            AstFingerprint.Compute(expression),
            AstReflection.ReferencedTargetColumns(expression));
    }

    /// <summary>Typed AND-merge. A parameter name bound to different values or types throws <see cref="PolicyConflictException"/>.</summary>
    public PolicyPredicate And(PolicyPredicate other)
    {
        ArgumentNullException.ThrowIfNull(other);
        var merged = new Dictionary<string, PolicyValue>(Parameters, StringComparer.Ordinal);
        foreach (var (name, value) in other.Parameters)
        {
            if (merged.TryGetValue(name, out var existing))
            {
                if (existing.Type != value.Type || !ValueEquals(existing.Value, value.Value))
                {
                    throw new PolicyConflictException($"Policy parameter '{name}' is bound to conflicting values.");
                }
            }
            else
            {
                merged[name] = value;
            }
        }

        var expression = new BinaryExpression(
            new ParenthesizedExpression(Expression), BinaryOperator.And, new ParenthesizedExpression(other.Expression));
        return Create(expression, merged);
    }

    private static bool ValueEquals(object? a, object? b) =>
        a is byte[] x && b is byte[] y ? x.AsSpan().SequenceEqual(y) : Equals(a, b);
}
