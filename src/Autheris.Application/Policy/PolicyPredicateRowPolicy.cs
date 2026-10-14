namespace Autheris.Application.Policy;

using System;
using Autheris.Application.Sql.Policy;
using Autheris.Domain.Interfaces;
using TrinoSqlEngine;
using TrinoSqlEngine.Governance;

/// <summary>
/// <see cref="IRowPolicy"/> over a typed <see cref="PolicyPredicate"/> (WP-X0e). The only way to obtain one is from a predicate, never
/// from text. The text projection is rendered once through <see cref="IPolicyPredicateRenderer"/> in the policy's target dialect, so
/// the legacy string consumers and the AST path read the same source.
/// </summary>
public sealed class PolicyPredicateRowPolicy : IRowPolicy
{
    private readonly IPolicyPredicateRenderer _renderer;
    private readonly Lazy<RowPolicyText> _text;

    public PolicyPredicateRowPolicy(PolicyPredicate predicate, TargetSqlDialect dialect, IPolicyPredicateRenderer renderer)
    {
        Predicate = predicate ?? throw new ArgumentNullException(nameof(predicate));
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        Dialect = dialect;
        _text = new Lazy<RowPolicyText>(() =>
        {
            var rendered = _renderer.Render(Predicate, Dialect, RowFilterAliases.Target);
            return new RowPolicyText(rendered.Sql, rendered.Parameters);
        });
    }

    public PolicyPredicate Predicate { get; }

    public TargetSqlDialect Dialect { get; }

    public string Fingerprint => Predicate.Fingerprint;

    public bool IsDenyAll => string.Equals(Predicate.Fingerprint, PolicyPredicate.DenyAll.Fingerprint, StringComparison.Ordinal);

    public IRowPolicy And(IRowPolicy other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (other is not PolicyPredicateRowPolicy typed || typed.Dialect != Dialect)
        {
            // Fail closed: policies of different target dialects (or foreign implementations) are never merged.
            throw new PolicyConflictException("Row policies of different target dialects cannot be merged.");
        }

        if (IsDenyAll)
        {
            return this;
        }

        return typed.IsDenyAll ? typed : new PolicyPredicateRowPolicy(Predicate.And(typed.Predicate), Dialect, _renderer);
    }

    public RowPolicyText Project() => _text.Value;
}
