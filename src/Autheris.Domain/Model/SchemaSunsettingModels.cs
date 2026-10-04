namespace Autheris.Domain.Model;

using System;
using System.Collections.Generic;

public enum SunsettingPhase
{
    Active = 0,
    Warning = 1,
    Brownout = 2,
    HardSunset = 3
}

public sealed record FieldSunsettingRule(
    Guid Id,
    string TargetTable,
    string FieldName,
    DateTimeOffset DeprecatedAt,
    DateTimeOffset SunsetAt,
    string? ReplacementField = null,
    string? DeprecationReason = null,
    TimeSpan? BrownoutWindow = null,
    double BrownoutPercentage = 0.25,
    int BrownoutLatencyMs = 200,
    int BrownoutErrorCode = 426,
    bool IsActive = true
)
{
    public TimeSpan EffectiveBrownoutWindow => BrownoutWindow ?? TimeSpan.FromDays(14);

    public SunsettingPhase DeterminePhase(DateTimeOffset now)
    {
        if (!IsActive || now < DeprecatedAt)
        {
            return SunsettingPhase.Active;
        }

        if (now >= SunsetAt)
        {
            return SunsettingPhase.HardSunset;
        }

        if (now >= SunsetAt - EffectiveBrownoutWindow)
        {
            return SunsettingPhase.Brownout;
        }

        return SunsettingPhase.Warning;
    }
}

public sealed record SunsettingEvaluationResult(
    FieldSunsettingRule Rule,
    SunsettingPhase Phase,
    bool ShouldInjectSyntheticLatency,
    int SyntheticLatencyMs,
    bool ShouldRejectWith426,
    bool IsHardSunsetBlocked,
    string DeprecationNotice,
    DateTimeOffset SunsetDate,
    string HttpSunsetHeader,
    IReadOnlyDictionary<string, object?> ExtensionsData
);

public sealed record EvaluateFieldSunsettingRequest(
    string TargetTable,
    string FieldName,
    DateTimeOffset? EvaluationDate = null
);

