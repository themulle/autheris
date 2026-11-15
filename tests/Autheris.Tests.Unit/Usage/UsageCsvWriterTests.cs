namespace Autheris.Tests.Unit.Usage;

using System;
using System.Collections.Generic;
using Autheris.Application.Usage;
using Shouldly;
using Xunit;

public sealed class UsageCsvWriterTests
{
    [Fact]
    public void Writes_rfc4180_quoted_csv_and_flattens_multiple_cost_buckets()
    {
        var report = new UsageReportResponse(
            ReportType: "users",
            TenantId: "tenant-a",
            From: new DateOnly(2026, 11, 1),
            To: new DateOnly(2026, 11, 15),
            GroupBy: UsageGroupBy.User,
            MultiCounted: false,
            MultiCountedNote: null,
            TenantTotal: null,
            Rows:
            [
                new UsageReportRowDto(
                    Key: "u, \"1\"",
                    Day: null,
                    UserKey: "u, \"1\"",
                    GroupSid: null,
                    GroupName: null,
                    BillingGroup: null,
                    DataSource: null,
                    TableId: null,
                    Transport: null,
                    Application: null,
                    ClientId: null,
                    GrantId: null,
                    Requests: 10,
                    Queries: 8,
                    RowsReturned: 100,
                    RowsMeasured: 8,
                    Bytes: 2048,
                    DurationMs: 500,
                    AnalysisCost: 2,
                    SourceComputeMs: 300,
                    SourceComputeMeasured: 8,
                    SourceCost: 1.25m,
                    SourceCostUnit: "USD",
                    AllowCount: 8,
                    DenyAccess: 2,
                    DenyRateLimit: 0,
                    DenyCost: 0,
                    DenyQuota: 0,
                    DenyOther: 0,
                    Errors: 0,
                    Cost:
                    [
                        new UsageCostBucketDto(12.345m, "credits"),
                        new UsageCostBucketDto(0.5m, "EUR")
                    ],
                    TariffMissing: false)
            ],
            NextCursor: null,
            Truncated: false);

        string csv = UsageCsvWriter.Write(report);
        string[] lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        lines.Length.ShouldBe(3);
        lines[0].ShouldStartWith("key,day,user_key,");
        lines[1].ShouldContain("\"u, \"\"1\"\"\"");
        lines[1].ShouldEndWith(",12.345,credits,false");
        lines[2].ShouldEndWith(",0.5,EUR,false");
    }

    [Fact]
    public void Empty_cost_emits_single_row_with_zero_cost()
    {
        var report = new UsageReportResponse(
            ReportType: "sources",
            TenantId: "tenant-a",
            From: new DateOnly(2026, 11, 1),
            To: new DateOnly(2026, 11, 1),
            GroupBy: UsageGroupBy.DataSource,
            MultiCounted: false,
            MultiCountedNote: null,
            TenantTotal: null,
            Rows:
            [
                new UsageReportRowDto(
                    Key: "pg-main",
                    Day: null,
                    UserKey: null,
                    GroupSid: null,
                    GroupName: null,
                    BillingGroup: null,
                    DataSource: "pg-main",
                    TableId: null,
                    Transport: null,
                    Application: null,
                    ClientId: null,
                    GrantId: null,
                    Requests: 3,
                    Queries: 0,
                    RowsReturned: 0,
                    RowsMeasured: 0,
                    Bytes: 0,
                    DurationMs: 10,
                    AnalysisCost: 0,
                    SourceComputeMs: 0,
                    SourceComputeMeasured: 0,
                    SourceCost: null,
                    SourceCostUnit: null,
                    AllowCount: 0,
                    DenyAccess: 3,
                    DenyRateLimit: 0,
                    DenyCost: 0,
                    DenyQuota: 0,
                    DenyOther: 0,
                    Errors: 0,
                    Cost: [],
                    TariffMissing: false)
            ],
            NextCursor: null,
            Truncated: false);

        string csv = UsageCsvWriter.Write(report);
        string[] lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        lines.Length.ShouldBe(2);
        lines[1].ShouldEndWith(",0,,false");
    }

    [Fact]
    public void Group_report_emits_multi_counted_comment_header()
    {
        var report = new UsageReportResponse(
            ReportType: "groups",
            TenantId: "tenant-a",
            From: new DateOnly(2026, 11, 1),
            To: new DateOnly(2026, 11, 10),
            GroupBy: UsageGroupBy.Group,
            MultiCounted: true,
            MultiCountedNote: "multi-counted: a user in N groups is counted in each group",
            TenantTotal: null,
            Rows: [],
            NextCursor: null,
            Truncated: false);

        string csv = UsageCsvWriter.Write(report);
        csv.ShouldStartWith("# multi-counted: a user in N groups is counted in each group\r\nkey,");
    }

    [Fact]
    public void BuildContentDisposition_sanitizes_slug_and_formats_dates()
    {
        string header = UsageCsvWriter.BuildContentDisposition(
            "Group_Report/../evil",
            new DateOnly(2026, 11, 1),
            new DateOnly(2026, 11, 15));

        header.ShouldBe("attachment; filename=\"usage-group-report-evil-2026-11-01-2026-11-15.csv\"");
    }
}
