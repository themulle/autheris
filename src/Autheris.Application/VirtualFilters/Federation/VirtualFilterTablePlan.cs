namespace Autheris.Application.VirtualFilters.Federation;

using System;

/// <summary>
/// F-GOV-14 (M5): how the upstream rows of a protected HTTP table are obtained. The tier only decides what is fetched;
/// what is returned is always decided by the <see cref="VirtualFilterEnforcement"/>.
/// </summary>
public enum VirtualFilterTier
{
    /// <summary>Tier 0: a key set is empty, nothing can be authorized; the upstream call is skipped.</summary>
    EmptyKeySet = 0,

    /// <summary>Tier 1: the query is a point lookup (not planned yet).</summary>
    PointLookup = 1,

    /// <summary>Tier 2: the authorized keys are pushed to the upstream (not planned yet).</summary>
    Pushdown = 2,

    /// <summary>Tier 3: the upstream is read completely and filtered by the gateway.</summary>
    FullFetch = 3
}

/// <summary>
/// F-GOV-14 (WI-6): the plan of one protected HTTP table. <see cref="Enforcement"/> is always present, also when
/// <see cref="SkipUpstream"/> is set, so the governed read path runs the same guards and coverage check for a table
/// that is not read as for one that is (SEC-13). Only the planner creates plans.
/// </summary>
public sealed class VirtualFilterTablePlan
{
    internal VirtualFilterTablePlan(VirtualFilterEnforcement enforcement, VirtualFilterTier tier, bool skipUpstream, string reason)
    {
        ArgumentNullException.ThrowIfNull(enforcement);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        Enforcement = enforcement;
        Tier = tier;
        SkipUpstream = skipUpstream;
        Reason = reason;
    }

    /// <summary>The gateway semi-join of the table; never null.</summary>
    public VirtualFilterEnforcement Enforcement { get; }

    public VirtualFilterTier Tier { get; }

    /// <summary>True when the connector must not be called (the result is known to be empty).</summary>
    public bool SkipUpstream { get; }

    /// <summary>Short, key-free reason for the tier (audit).</summary>
    public string Reason { get; }

    public TableIdentifier Table => Enforcement.Table;
}
