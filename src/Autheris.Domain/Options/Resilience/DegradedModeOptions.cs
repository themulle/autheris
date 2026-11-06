using System.ComponentModel.DataAnnotations;

namespace Autheris.Domain.Options;

public sealed class HighAvailabilityOptions
{
    public bool MultiNodeClusterMode { get; init; } = false;
    public int Replicas { get; init; } = 1;
    [Range(1, 30)] public int DrainDelaySeconds { get; init; } = 5;
    [Range(5, 120)] public int QueryTimeoutSeconds { get; init; } = 30;
    [Range(10, 180)] public int ShutdownTimeoutSeconds { get; init; } = 40;
    [Range(20, 300)] public int TerminationGracePeriodSeconds { get; init; } = 60;
}

public sealed class SystemMetricsOptions
{
    public bool Enabled { get; init; } = true;
    public bool ExposeRestEndpoints { get; init; } = true;

    /// <summary>
    /// Roles that may read the system metrics endpoints. When not configured: GovernanceAdmin, ClusterAdmin and
    /// SecurityAdmin. A configured list replaces that default entirely. Nullable on purpose: the configuration binder
    /// appends to pre-filled lists, so a non-empty default could not be narrowed. A configured empty JSON array
    /// aborts the start (it would silently apply the default); to switch the endpoints off set <see cref="Enabled"/>
    /// (or <see cref="ExposeRestEndpoints"/>) to false. A list that is empty only when built in code (not through
    /// configuration) makes the endpoints require GovernanceAdmin.
    /// </summary>
    public List<string>? AllowedRoles { get; init; }

    private static readonly string[] DefaultAllowedRoles = ["GovernanceAdmin", "ClusterAdmin", "SecurityAdmin"];

    /// <summary>Configured <see cref="AllowedRoles"/> or the default list.</summary>
    public IReadOnlyList<string> GetEffectiveAllowedRoles() => AllowedRoles ?? (IReadOnlyList<string>)DefaultAllowedRoles;
}

