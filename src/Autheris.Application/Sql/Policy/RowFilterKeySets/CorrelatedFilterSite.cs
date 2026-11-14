namespace Autheris.Application.Sql.Policy.RowFilterKeySets;

/// <summary>Origin of a correlated filter site: a consent row filter (rule-level settings).</summary>
public enum CorrelatedFilterSource
{
    Consent
}

/// <summary>
/// Issue #107 (107 A-1): metadata registered for one correlated<c>EXISTS</c> node created by
/// <c>AdvancedRlsPredicateBuilder.CorrelatedExpression</c>. <see cref="StructurallyEligible"/> is false when temporal columns are set.
/// </summary>
public sealed record CorrelatedFilterSite(
    Guid? RuleId,
    CorrelatedFilterSource Source,
    RowFilterEvaluationSettings? RuleSettings,
    bool StructurallyEligible);
