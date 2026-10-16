namespace Autheris.Application.Sql.Policy.RowFilterKeySets;

using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;

/// <summary>Issue #107 WP-B: site discovery and substitution (PLAN-GOV-107 A-10, SEC-RF107-06). Contract stub.</summary>
internal static class CorrelatedKeySetSubstitution
{
    /// <summary>Positive-polarity registered sites of the predicate, in a stable pre-order.</summary>
    public static IReadOnlyList<(ExistsExpression Node, CorrelatedFilterSite Site)> FindSites(PolicyPredicate predicate)
        => throw new NotImplementedException("WP-B: CorrelatedKeySetSubstitution.FindSites");

    /// <summary>Replaces each given node (reference identity) by its IN list or <c>1 = 0</c>; returns a new <see cref="PolicyPredicate"/>.</summary>
    public static PolicyPredicate Substitute(
        PolicyPredicate predicate,
        IReadOnlyDictionary<ExistsExpression, KeySetReplacement> replacements,
        TargetSqlDialect dialect)
        => throw new NotImplementedException("WP-B: CorrelatedKeySetSubstitution.Substitute");
}

/// <summary>Replacement of one site. SEC-RF107-09: <see cref="ToString"/> prints counts only, never key values.</summary>
public sealed record KeySetReplacement(string ForeignKeyColumn, IReadOnlyList<PolicyValue> Keys, int PaddedCount, int SiteOrdinal)
{
    public override string ToString() => $"KeySetReplacement {{ Keys = {Keys.Count}, PaddedCount = {PaddedCount}, SiteOrdinal = {SiteOrdinal} }}";
}

/// <summary>IN-list padding buckets (A-10): <c>min(nextPow2(count), maxKeys)</c>.</summary>
public static class KeySetPadding
{
    public static int Bucket(int count, int maxKeys)
        => throw new NotImplementedException("WP-B: KeySetPadding.Bucket");
}
