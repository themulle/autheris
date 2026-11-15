namespace Autheris.Domain.Options;

using System;
using System.Collections.Generic;

public sealed class UsageAccountingOptions
{
    public const int DefaultFlushIntervalSeconds = 15;
    public const int DefaultMaxSeries = 100_000;
    public const int DefaultRetentionDays = 400;
    public const int DefaultMaxFlushRetries = 20;
    public const int DefaultMaxGroupsPerUser = 256;
    public const int DefaultMaxReportRangeDays = 366;
    public const int DefaultMaxReportRows = 50_000;

    /// <summary>Interval in seconds between background flushes of aggregated usage counters.</summary>
    public int FlushIntervalSeconds { get; set; } = DefaultFlushIntervalSeconds;

    /// <summary>Maximum number of distinct usage series buffered in memory before overflow folding.</summary>
    public int MaxSeries { get; set; } = DefaultMaxSeries;

    /// <summary>Retention horizon in days for aggregated usage data.</summary>
    public int RetentionDays { get; set; } = DefaultRetentionDays;

    /// <summary>Maximum consecutive flush failures before dropping a generation.</summary>
    public int MaxFlushRetries { get; set; } = DefaultMaxFlushRetries;

    /// <summary>Well-known or synthetic group SID prefixes excluded from usage group-set attribution.</summary>
    public List<string> ExcludedGroupSidPrefixes { get; set; } =
    [
        "S-1-1-",
        "S-1-2-",
        "S-1-3-",
        "S-1-5-32-",
        "S-1-5-11",
        "S-1-5-2",
        "S-1-5-4",
        "S-1-5-15",
        "S-1-5-64-",
        "S-1-16-",
        "S-1-18-"
    ];

    /// <summary>Optional allow-list of group SID prefixes retained even when exclusions apply.</summary>
    public List<string> IncludedGroupSidPrefixes { get; set; } = [];

    /// <summary>Maximum number of group SIDs tracked per user per usage event.</summary>
    public int MaxGroupsPerUser { get; set; } = DefaultMaxGroupsPerUser;

    /// <summary>Per-tenant mapping from group SIDs to billing groups.</summary>
    public Dictionary<string, List<string>> BillingGroups { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Static display names overriding directory lookups for known group SIDs.</summary>
    public Dictionary<string, string> GroupDisplayNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether user SIDs are pseudonymized before persistence.</summary>
    public bool PseudonymizeUsers { get; set; } = false;

    /// <summary>Key Vault or secret reference holding the HMAC key used for user pseudonymization.</summary>
    public string? PseudonymizationKeyVaultRef { get; set; }

    /// <summary>Maximum number of days permitted in a usage report query.</summary>
    public int MaxReportRangeDays { get; set; } = DefaultMaxReportRangeDays;

    /// <summary>Maximum number of rows returned across a usage report response.</summary>
    public int MaxReportRows { get; set; } = DefaultMaxReportRows;

    /// <summary>Whether data owners may see per-user breakdowns in usage reports for their tables.</summary>
    public bool DataOwnersSeeUsers { get; set; } = true;

    public bool EffectiveDataOwnersSeeUsers => DataOwnersSeeUsers;

    /// <summary>Optional initial tariff entries seeded at startup when USAGE_TARIFF has no row for the (tenant, data source) pair.</summary>
    public List<InitialTariffConfig> InitialTariffs { get; set; } = [];
}

/// <summary>Bootstrap tariff entry configured under <c>Gateway:UsageAccounting:InitialTariffs</c>.</summary>
public sealed class InitialTariffConfig
{
    /// <summary>Tenant identifier or <c>*</c> for cluster-wide default tariff.</summary>
    public string TenantId { get; set; } = "*";

    /// <summary>Configured data source name or <c>*</c> for wildcard source tariff.</summary>
    public string DataSource { get; set; } = "*";

    /// <summary>Effective start date (UTC) from which the initial tariff applies.</summary>
    public DateOnly ValidFrom { get; set; }

    /// <summary>Fixed cost units charged per query.</summary>
    public decimal BasePerQuery { get; set; }

    /// <summary>Cost units charged per returned row.</summary>
    public decimal PerRow { get; set; }

    /// <summary>Cost units charged per mebibyte (1,048,576 bytes) transferred.</summary>
    public decimal PerMib { get; set; }

    /// <summary>Cost units charged per compute second.</summary>
    public decimal PerComputeSecond { get; set; }

    /// <summary>Cost units charged per unit of static query analysis cost.</summary>
    public decimal PerAnalysisCost { get; set; }

    /// <summary>Pass-through multiplier applied to source-reported cost units.</summary>
    public decimal SourceCostFactor { get; set; }

    /// <summary>Unit of source-reported cost (required if and only if <see cref="SourceCostFactor"/> is positive).</summary>
    public string? SourceCostUnit { get; set; }

    /// <summary>Output cost unit label (1-32 characters matching <c>^[A-Za-z0-9 _.-]{1,32}$</c>).</summary>
    public string UnitLabel { get; set; } = "credits";

    /// <summary>Compute basis selector (0 = source compute when all queries measured, otherwise gateway duration; 1 = always gateway duration).</summary>
    public short ComputeBasis { get; set; }

    /// <summary>Audit reason recorded for the initial tariff seed.</summary>
    public string Reason { get; set; } = "Initial tariff seed";
}
