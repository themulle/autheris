namespace Autheris.Domain.Options;

using System.ComponentModel.DataAnnotations;

/// <summary>
/// Limits for the two-phase access planning state (SR-L-06). Bound to <c>Gateway:Governance:AccessPlanning</c>.
/// </summary>
public sealed class AccessPlanOptions
{
    public const string SectionName = "Gateway:Governance:AccessPlanning";

    /// <summary>Maximum number of unexpired plans a single administrator may hold.</summary>
    [Range(1, 1000)]
    public int MaxPendingPlansPerAdmin { get; set; } = 20;

    /// <summary>Lifetime of a plan between planning and apply, in minutes.</summary>
    [Range(1, 1440)]
    public int PlanTtlMinutes { get; set; } = 10;

    /// <summary>Lifetime of a confirmation token issued after TOTP step-up, in minutes.</summary>
    [Range(1, 1440)]
    public int ConfirmationTokenTtlMinutes { get; set; } = 10;

    /// <summary>Minimum interval between opportunistic sweeps of expired plan state, in seconds.</summary>
    [Range(1, 86400)]
    public int SweepIntervalSeconds { get; set; } = 60;
}
