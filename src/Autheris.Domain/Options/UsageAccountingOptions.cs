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

    public int FlushIntervalSeconds { get; set; } = DefaultFlushIntervalSeconds;

    public int MaxSeries { get; set; } = DefaultMaxSeries;

    public int RetentionDays { get; set; } = DefaultRetentionDays;

    public int MaxFlushRetries { get; set; } = DefaultMaxFlushRetries;

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

    public List<string> IncludedGroupSidPrefixes { get; set; } = [];

    public int MaxGroupsPerUser { get; set; } = DefaultMaxGroupsPerUser;

    public Dictionary<string, List<string>> BillingGroups { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> GroupDisplayNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool PseudonymizeUsers { get; set; } = false;

    public string? PseudonymizationKeyVaultRef { get; set; }

    public int MaxReportRangeDays { get; set; } = DefaultMaxReportRangeDays;

    public int MaxReportRows { get; set; } = DefaultMaxReportRows;

    public bool DataOwnersSeeUsers { get; set; } = true;

    public bool EffectiveDataOwnersSeeUsers => DataOwnersSeeUsers;
}
