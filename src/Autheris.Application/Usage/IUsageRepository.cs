namespace Autheris.Application.Usage;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options.DataSources;
using Autheris.Domain.Security;

public enum UsageGroupBy
{
    Day,
    User,
    Group,
    BillingGroup,
    DataSource,
    Table,
    Transport,
    Application,
    ClientId,
    Grant
}

public sealed record UsageReportQuery(
    TenantId Tenant,
    DateOnly FromUtc,
    DateOnly ToUtc,
    UsageGroupBy GroupBy,
    string? UserKey = null,
    string? GroupSid = null,
    string? BillingGroup = null,
    string? DataSource = null,
    string? TableId = null,
    DataSourceTransportFlags? Transport = null,
    string? Application = null,
    string? ClientId = null,
    string? GrantId = null,
    IReadOnlyList<string>? RestrictedTableIds = null,
    bool IncludeTariffBreakdown = false,
    int Limit = 100,
    string? Cursor = null);

public sealed record UsageReportRow(
    DateOnly? DayUtc,
    string? UserKey,
    string? GroupSid,
    string? BillingGroup,
    string? DataSource,
    string? TableId,
    DataSourceTransportFlags? Transport,
    string? Application,
    string? ClientId,
    string? GrantId,
    long Requests,
    long Queries,
    long RowsReturned,
    long RowsMeasured,
    long Bytes,
    long DurationMs,
    long AnalysisCost,
    long SourceComputeMs,
    long SourceComputeMeasured,
    long SourceCostMicros,
    long SourceCostMeasured,
    long AllowCount,
    long DenyAccess,
    long DenyRateLimit,
    long DenyCost,
    long DenyQuota,
    long DenyOther,
    long Errors);

public sealed record UsageReportPage(IReadOnlyList<UsageReportRow> Rows, string? NextCursor);

public sealed record UsageTariffVersion(
    string TenantId,
    string DataSource,
    int Version,
    DateOnly ValidFrom,
    DateOnly? ValidUntil,
    decimal BasePerQuery,
    decimal PerRow,
    decimal PerMib,
    decimal PerComputeSecond,
    decimal PerAnalysisCost,
    decimal SourceCostFactor,
    string? SourceCostUnit,
    string UnitLabel,
    short ComputeBasis,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    string Reason);

public sealed record UsageGrantRefRecord(
    TenantId Tenant,
    string GrantId,
    DateTimeOffset? ValidFrom,
    DateTimeOffset? ValidUntil,
    DateOnly FirstSeen,
    DateOnly LastSeen);

public interface IUsageRepository
{
    Task AccumulateAsync(UsageGenerationSnapshot snapshot, CancellationToken ct = default);

    Task<UsageReportPage> QueryAsync(UsageReportQuery query, CancellationToken ct = default);

    Task<int> PurgeAsync(DateOnly cutoffExclusive, int batchSize = 10_000, CancellationToken ct = default);

    Task<int> EraseUserAsync(TenantId tenant, string userKey, CancellationToken ct = default);

    Task<IReadOnlyList<UsageTariffVersion>> GetTariffsAsync(TenantId tenant, DateOnly fromUtc, DateOnly toUtc, CancellationToken ct = default);

    Task<UsageTariffVersion> AddTariffVersionAsync(UsageTariffVersion tariff, CancellationToken ct = default);

    Task<UsageGrantRefRecord?> GetGrantRefAsync(TenantId tenant, string grantId, CancellationToken ct = default);

    Task<IReadOnlyList<ConsumerAppDailySummary>> QueryConsumerAppDailyAsync(
        TableIdentifier tableId,
        TenantId tenantId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken ct = default);

    Task<IReadOnlyDictionary<string, int>> QueryDistinctActorCountsAsync(
        TableIdentifier tableId,
        TenantId tenantId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken ct = default);

    Task<UsageQuotaTenantSnapshot> GetQuotaSnapshotAsync(
        TenantId tenant,
        DateOnly fromUtc,
        DateOnly toUtc,
        CancellationToken ct = default);

    Task<IReadOnlyList<TenantId>> GetActiveUsageTenantsAsync(
        DateOnly fromUtc,
        DateOnly toUtc,
        CancellationToken ct = default);
}

/// <summary>Aggregated usage series row projected for quota evaluation across a fixed UTC window.</summary>
public sealed record UsageQuotaSeriesEntry(
    DateOnly DayUtc,
    string UserKey,
    string GroupSetId,
    string BillingGroup,
    string DataSource,
    long Requests,
    long Queries,
    long RowsReturned,
    long Bytes,
    long DurationMs,
    long AnalysisCost,
    long SourceComputeMs,
    long SourceComputeMeasured,
    long SourceCostMicros,
    long AllowCount);

/// <summary>Cached persisted usage state for one tenant over the active quota window.</summary>
public sealed record UsageQuotaTenantSnapshot(
    IReadOnlyList<UsageQuotaSeriesEntry> Series,
    IReadOnlyDictionary<string, IReadOnlyList<Sid>> GroupSets,
    IReadOnlyList<UsageTariffVersion> Tariffs)
{
    public static readonly UsageQuotaTenantSnapshot Empty = new(
        [],
        new Dictionary<string, IReadOnlyList<Sid>>(StringComparer.Ordinal),
        []);
}
