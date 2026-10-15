namespace Autheris.Domain.Options;

/// <summary>
/// F-AI-08: FOCUS-compliant FinOps Accounting for Token &amp; Compute.
/// Implements standard cost attribution, usage metering, and budget governance caps.
/// </summary>
public sealed class FinOpsOptions
{
    public bool Enabled { get; init; } = false;
    public decimal DefaultMonthlyBudget { get; init; } = 1000m;
    public double SoftCapRatio { get; init; } = 0.8; // 80% warning
    public decimal PricePerThousandPromptTokens { get; init; } = 0.003m;
    public decimal PricePerThousandCompletionTokens { get; init; } = 0.015m;
    public decimal PricePerComputeSecond { get; init; } = 0.0001m;
    public Dictionary<string, decimal> TenantMonthlyBudgets { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, decimal> ScopeMonthlyBudgets { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}
