namespace Autheris.Tests.Unit.Usage;

using System;
using Autheris.Application.Usage;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class UsageCsvSecurityTests
{
    [Theory]
    [InlineData("=CMD('calc')", "'=CMD('calc')")]
    [InlineData("+SUM(1,2)", "\"'+SUM(1,2)\"")]
    [InlineData("-1+1", "'-1+1")]
    [InlineData("@SUM(A1:A2)", "'@SUM(A1:A2)")]
    [InlineData("\t=cmd", "'\t=cmd")]
    [InlineData("\r=cmd", "\"'\r=cmd\"")]
    public void Formula_triggers_at_cell_start_are_prefixed_with_single_quote(string input, string expected)
    {
        UsageCsvWriter.FormatCell(input).ShouldBe(expected);
    }

    [Fact]
    public void Csv_export_neutralizes_injected_application_and_group_names()
    {
        var report = new UsageReportResponse(
            ReportType: "applications",
            TenantId: "tenant-a",
            From: new DateOnly(2026, 11, 1),
            To: new DateOnly(2026, 11, 2),
            GroupBy: UsageGroupBy.Application,
            MultiCounted: false,
            MultiCountedNote: null,
            TenantTotal: null,
            Rows:
            [
                new UsageReportRowDto(
                    Key: "=HYPERLINK(\"http://evil\",\"click\")",
                    Day: null,
                    UserKey: null,
                    GroupSid: null,
                    GroupName: "+cmd|' /C calc'!A0",
                    BillingGroup: null,
                    DataSource: null,
                    TableId: null,
                    Transport: null,
                    Application: "=HYPERLINK(\"http://evil\",\"click\")",
                    ClientId: null,
                    GrantId: null,
                    Requests: 1,
                    Queries: 1,
                    RowsReturned: 1,
                    RowsMeasured: 1,
                    Bytes: 10,
                    DurationMs: 5,
                    AnalysisCost: 0,
                    SourceComputeMs: 0,
                    SourceComputeMeasured: 0,
                    SourceCost: null,
                    SourceCostUnit: null,
                    AllowCount: 1,
                    DenyAccess: 0,
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

        lines[1].ShouldStartWith("\"'=HYPERLINK");
        lines[1].ShouldContain("'+cmd");
    }
}
