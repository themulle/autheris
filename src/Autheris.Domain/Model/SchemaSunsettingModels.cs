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

/// <summary>Schema field deprecation and sunsetting rules (<c>Gateway:FieldSunsetting</c>).</summary>
public sealed class FieldSunsettingOptions
{
    /// <summary>Configured field sunsetting rules evaluated during query execution and schema inspection.</summary>
    public List<FieldSunsettingRule> Rules { get; init; } = [];
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
    public string TableName => TargetTable;
    public DateTimeOffset DeprecatedSince => DeprecatedAt;
    public DateTimeOffset SunsetDate => SunsetAt;
    public string? Reason => DeprecationReason;
    public int WarningWindowDays => (int)Math.Max(1, (SunsetAt - DeprecatedAt).TotalDays);
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

