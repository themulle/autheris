namespace Autheris.Application.Usage;

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Autheris.Domain.Common;
using Autheris.Domain.Options.DataSources;

/// <summary>Query specification for rolled-up usage reports.</summary>
public sealed record UsageReportFilter(
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
    int Limit = 100,
    string? Cursor = null,
    bool IncludeTenantTotal = false);

/// <summary>Currency/unit cost bucket on a rolled-up report row.</summary>
public sealed record UsageCostBucketDto(
    decimal CostUnits,
    string UnitLabel);

/// <summary>Single rolled-up row in a usage report with deterministic tariff pricing.</summary>
public sealed record UsageReportRowDto(
    string Key,
    DateOnly? Day,
    string? UserKey,
    string? GroupSid,
    string? GroupName,
    string? BillingGroup,
    string? DataSource,
    string? TableId,
    string? Transport,
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
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? SourceCost,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SourceCostUnit,
    long AllowCount,
    long DenyAccess,
    long DenyRateLimit,
    long DenyCost,
    long DenyQuota,
    long DenyOther,
    long Errors,
    IReadOnlyList<UsageCostBucketDto> Cost,
    bool TariffMissing);

/// <summary>Paged usage report response shared across JSON and CSV serializers.</summary>
public sealed record UsageReportResponse(
    string ReportType,
    string TenantId,
    DateOnly From,
    DateOnly To,
    UsageGroupBy GroupBy,
    bool MultiCounted,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? MultiCountedNote,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] UsageReportRowDto? TenantTotal,
    IReadOnlyList<UsageReportRowDto> Rows,
    string? NextCursor,
    bool Truncated);

/// <summary>Audit context attached to audited usage reports and user erasure requests.</summary>
public sealed record UsageReportAuditContext(
    string ActorIdentity,
    string ReportType,
    string? TargetResource = null,
    string? TraceId = null);

/// <summary>Validation failure raised by reporting range, filter, or parameter checks.</summary>
public sealed class UsageReportValidationException : ArgumentException
{
    public string ErrorCode { get; }

    public UsageReportValidationException(string errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    public UsageReportValidationException(string errorCode, string message, Exception innerException)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }
}
