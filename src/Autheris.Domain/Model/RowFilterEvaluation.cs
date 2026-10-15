namespace Autheris.Domain.Model;

/// <summary>How a correlated row filter is evaluated (Issue #107): per statement as <c>EXISTS</c> (default) or from a cached key set.</summary>
public enum RowFilterEvaluationStrategy
{
    Exists = 0,
    KeySetCache = 1
}

/// <summary>Level that supplied the effective strategy.</summary>
public enum RowFilterEvaluationSource
{
    Global = 0,
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "Contract name of PLAN-GOV-107: the object (table) level.")]
    Object = 1,
    Rule = 2
}

/// <summary>Stored settings of one level (rule or object); null = inherit from the next level.</summary>
public sealed record RowFilterEvaluationSettings(
    RowFilterEvaluationStrategy? Strategy,
    int? DurationSeconds,
    int? MaxKeys)
{
    public static RowFilterEvaluationSettings Inherit { get; } = new(null, null, null);
}

/// <summary>Resolved (clamped) settings for one correlated filter.</summary>
public sealed record EffectiveRowFilterEvaluation(
    RowFilterEvaluationStrategy Strategy,
    RowFilterEvaluationSource StrategySource,
    TimeSpan Duration,
    int MaxKeys);

/// <summary>Result of a settings change; <paramref name="NewEpoch"/> is the bumped policy epoch of the target table.</summary>
public sealed record RowFilterEvaluationChange(
    TableIdentifier TargetTable,
    TenantId? Tenant,
    Guid? RowFilterId,
    RowFilterEvaluationSettings Before,
    RowFilterEvaluationSettings After,
    long NewEpoch);
