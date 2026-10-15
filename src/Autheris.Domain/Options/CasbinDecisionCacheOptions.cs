namespace Autheris.Domain.Options;

/// <summary>
/// Options of the Casbin decision cache and its cluster policy epoch. Bound to <see cref="SectionName"/>.
/// </summary>
/// <remarks>
/// Staleness bound of a cached allow after a policy change on another node: <c>min(TtlSeconds, EpochCheckSeconds + reload time)</c>.
/// If the publishing node could not increment the cluster epoch, at most <c>TtlSeconds</c>. If the cluster state is unavailable on
/// the reading node, the cache is bypassed once a check is due, so no cached allow outlives <c>EpochCheckSeconds</c>.
/// Policy files are per node: every node must load the same (or a shared) policy file.
/// </remarks>
public sealed class CasbinDecisionCacheOptions
{
    public const string SectionName = "Gateway:Casbin:DecisionCache";

    /// <summary>Maximum number of cached decisions; the least recently used entry is evicted when full.</summary>
    public int Capacity { get; init; } = 10_000;

    /// <summary>Maximum age of a cached decision in seconds.</summary>
    public int TtlSeconds { get; init; } = 60;

    /// <summary>How often a node reads the cluster policy epoch per tenant, in seconds (cluster-wide state only).</summary>
    public int EpochCheckSeconds { get; init; } = 5;

    /// <summary>After a failed epoch read the cache stays bypassed for this long before the next attempt.</summary>
    public int UnavailableBackoffMilliseconds { get; init; } = 1000;
}
