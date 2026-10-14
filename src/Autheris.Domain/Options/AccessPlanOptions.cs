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

    /// <summary>
    /// Secret reference (resolved through <c>IKeyVaultSecretProvider</c>) from which the confirmation-token HMAC key is
    /// derived. Required when the cluster state is shared across replicas, so every node signs and verifies with the
    /// same key; without it a per-process random key is used (single node only).
    /// </summary>
    public string? SigningKeyRef { get; set; }
}
