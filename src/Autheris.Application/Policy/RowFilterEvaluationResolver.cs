namespace Autheris.Application.Policy;

/// <summary>
/// Issue #107 (PLAN-GOV-107 A-7, C-3): pure resolution of the effective key-set settings. Strategy, duration and maxKeys are resolved
/// independently (rule ?? object ?? global), then duration is clamped to <c>MaxDuration</c> and maxKeys to <c>MaxKeys</c>.
/// WP-A owns the implementation; contract stub only.
/// </summary>
public static class RowFilterEvaluationResolver
{
    public static EffectiveRowFilterEvaluation Resolve(
        RowFilterEvaluationSettings? rule, RowFilterEvaluationSettings? obj, RowFilterKeySetCacheOptions global)
        => throw new NotImplementedException("WP-A: RowFilterEvaluationResolver.Resolve");

    /// <summary>
    /// 400 reason, or null when valid. Checks caps, positivity, enum range and (rule level) correlated filter type, ALLOW effect and no temporal column.
    /// </summary>
    public static string? Validate(
        RowFilterEvaluationSettings settings, RowFilterKeySetCacheOptions global, ConsentRowFilter? rule, ConsentEffect? effect)
        => throw new NotImplementedException("WP-A: RowFilterEvaluationResolver.Validate");
}
