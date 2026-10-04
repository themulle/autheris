namespace Autheris.Domain.Model;

using System;

public sealed record PrivacyBudget(
    string ClientId,
    double TotalDailyEpsilonBudget,
    double ConsumedEpsilon,
    DateTimeOffset LastResetUtc
)
{
    public double RemainingEpsilon => Math.Max(0.0, TotalDailyEpsilonBudget - ConsumedEpsilon);
    public bool IsExhausted => ConsumedEpsilon >= TotalDailyEpsilonBudget;
}

public sealed record DifferentialPrivacyPerturbationRequest(
    string ClientId,
    double Value,
    double Epsilon = 0.5,
    double Sensitivity = 1.0,
    int? CohortCount = null,
    int MinimumCohortSize = 5
);

public sealed record DifferentialPrivacyPerturbationResult(
    string ClientId,
    double OriginalValue,
    double? PerturbedValue,
    double Noise,
    bool IsSuppressed,
    string? SuppressionReason,
    double ConsumedEpsilon,
    double RemainingEpsilon,
    DateTimeOffset Timestamp
);

public sealed class PrivacyBudgetExhaustedException : Exception
{
    public string ClientId { get; }
    public double ConsumedEpsilon { get; }
    public double TotalBudget { get; }

    public PrivacyBudgetExhaustedException(string clientId, double consumed, double total)
        : base($"Epsilon privacy budget exhausted for client '{clientId}'. Consumed {consumed:F2} of {total:F2} daily epsilon.")
    {
        ClientId = clientId;
        ConsumedEpsilon = consumed;
        TotalBudget = total;
    }
}
