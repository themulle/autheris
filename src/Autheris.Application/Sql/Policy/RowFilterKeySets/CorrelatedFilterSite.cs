namespace Autheris.Application.Sql.Policy.RowFilterKeySets;

/// <summary>Origin of a correlated filter site: a consent row filter (rule-level settings) or a Casbin rule (object and global settings only).</summary>
public enum CorrelatedFilterSource
{
    Consent,
    Casbin
}

/// <summary>
/// Issue #107 (PLAN-GOV-107 A-1): metadata registered for one correlated <c>EXISTS</c> node created by
/// <c>AdvancedRlsPredicateBuilder.CorrelatedExpression</c>. <see cref="StructurallyEligible"/> is false when temporal columns are set.
/// </summary>
public sealed record CorrelatedFilterSite(
    Guid? RuleId,
    CorrelatedFilterSource Source,
    RowFilterEvaluationSettings? RuleSettings,
    bool StructurallyEligible);
