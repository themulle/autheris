namespace Autheris.Application.Policy;

/// <summary>
/// Issue #107 (PLAN-GOV-107 A-7, C-3): pure resolution of the effective key-set settings. Strategy, duration and maxKeys are resolved
/// independently (rule ?? object ?? global), then duration is clamped to <c>MaxDuration</c> and maxKeys to <c>MaxKeys</c>.
/// A level that carries an invalid stored value (direct load, D-11) is read as "not set" for that setting and its strategy as
/// <see cref="RowFilterEvaluationStrategy.Exists"/> (safe and exact); callers log one Warning per table/rule id (no values) using
/// <see cref="HasInvalidStoredValue"/>.
/// </summary>
public static class RowFilterEvaluationResolver
{
    public static EffectiveRowFilterEvaluation Resolve(
        RowFilterEvaluationSettings? rule, RowFilterEvaluationSettings? obj, RowFilterKeySetCacheOptions global)
    {
        ArgumentNullException.ThrowIfNull(global);

        var (strategy, source) = ResolveStrategy(rule, obj, global);

        var seconds = ValidPositive(rule?.DurationSeconds) ?? ValidPositive(obj?.DurationSeconds);
        var duration = seconds is { } sec ? TimeSpan.FromSeconds(sec) : global.DefaultDuration;
        if (duration > global.MaxDuration)
        {
            duration = global.MaxDuration;
        }

        var maxKeys = ValidPositive(rule?.MaxKeys) ?? ValidPositive(obj?.MaxKeys) ?? global.MaxKeys;
        if (maxKeys > global.MaxKeys)
        {
            maxKeys = global.MaxKeys;
        }

        return new EffectiveRowFilterEvaluation(strategy, source, duration, maxKeys);
    }

    /// <summary>True when the stored settings of a level hold a value that cannot be honoured (undefined enum, non-positive number).</summary>
    public static bool HasInvalidStoredValue(RowFilterEvaluationSettings? settings)
        => settings is not null
           && ((settings.Strategy is { } st && !Enum.IsDefined(st))
               || settings.DurationSeconds is <= 0
               || settings.MaxKeys is <= 0);

    /// <summary>
    /// 400 reason, or null when valid. Checks caps, positivity, enum range and (rule level) correlated filter type, ALLOW effect and no temporal column.
    /// An all-null (inherit) value is always valid so that an administrator can clear a setting on any rule.
    /// </summary>
    public static string? Validate(
        RowFilterEvaluationSettings settings, RowFilterKeySetCacheOptions global, ConsentRowFilter? rule, ConsentEffect? effect)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(global);

        if (settings.Strategy is { } st && !Enum.IsDefined(st))
        {
            return "strategy must be 'Exists' or 'KeySetCache'.";
        }

        if (settings.DurationSeconds is { } d)
        {
            if (d <= 0)
            {
                return "durationSeconds must be a positive integer.";
            }

            if (d > global.MaxDuration.TotalSeconds)
            {
                return $"durationSeconds exceeds the configured maximum of {(long)global.MaxDuration.TotalSeconds} seconds.";
            }
        }

        if (settings.MaxKeys is { } k)
        {
            if (k <= 0)
            {
                return "maxKeys must be a positive integer.";
            }

            if (k > global.MaxKeys)
            {
                return $"maxKeys exceeds the configured maximum of {global.MaxKeys}.";
            }
        }

        if (rule is not null && IsActiveSetting(settings))
        {
            if (effect != ConsentEffect.Allow)
            {
                return "Key-set cache settings are only supported on ALLOW rules.";
            }

            if (rule.FilterType != RowFilterType.SubqueryCorrelated)
            {
                return "Key-set cache settings are only supported on correlated subquery row filters.";
            }

            if (!string.IsNullOrWhiteSpace(rule.TargetTemporalColumn)
                || !string.IsNullOrWhiteSpace(rule.DependentValidFromColumn)
                || !string.IsNullOrWhiteSpace(rule.DependentValidToColumn))
            {
                return "Key-set cache settings are not supported on temporal row filters.";
            }
        }

        return null;
    }

    // Anything but "inherit" or an explicit Exists with no tuning values needs an eligible rule.
    private static bool IsActiveSetting(RowFilterEvaluationSettings s)
        => s.Strategy == RowFilterEvaluationStrategy.KeySetCache || s.DurationSeconds.HasValue || s.MaxKeys.HasValue;

    private static (RowFilterEvaluationStrategy Strategy, RowFilterEvaluationSource Source) ResolveStrategy(
        RowFilterEvaluationSettings? rule, RowFilterEvaluationSettings? obj, RowFilterKeySetCacheOptions global)
    {
        if (HasInvalidStoredValue(rule))
        {
            return (RowFilterEvaluationStrategy.Exists, RowFilterEvaluationSource.Rule);
        }

        if (rule?.Strategy is { } ruleStrategy)
        {
            return (ruleStrategy, RowFilterEvaluationSource.Rule);
        }

        if (HasInvalidStoredValue(obj))
        {
            return (RowFilterEvaluationStrategy.Exists, RowFilterEvaluationSource.Object);
        }

        if (obj?.Strategy is { } objStrategy)
        {
            return (objStrategy, RowFilterEvaluationSource.Object);
        }

        return (global.DefaultStrategy, RowFilterEvaluationSource.Global);
    }

    private static int? ValidPositive(int? value) => value is > 0 ? value : null;
}
