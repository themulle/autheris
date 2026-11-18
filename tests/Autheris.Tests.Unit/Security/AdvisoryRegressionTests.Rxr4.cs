namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using Autheris.Application.Analytics;
using Autheris.Application.DataSources.Services;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed partial class AdvisoryRegressionTests
{
    private static readonly TableMetadata AggregationMeta = new()
    {
        Identifier = new TableIdentifier("hr", "public", "employees"),
        Columns =
        [
            new TableColumn { ColumnName = "employee_id", DataType = "int" },
            new TableColumn { ColumnName = "tenant_part", DataType = "int" },
            new TableColumn { ColumnName = "email", DataType = "varchar" },
            new TableColumn { ColumnName = "department", DataType = "varchar" },
            new TableColumn { ColumnName = "salary", DataType = "decimal" }
        ],
        PrimaryKeyColumns = ["employee_id", "tenant_part"],
        Indexes = [new TableIndexDefinition("ux_email", ["email"], true)]
    };

    private static void EnforceAggregationFilter(string sql, int minGroupSize = 5) =>
        AggregationNegationGuard.Enforce(new TableFilterClause(sql, new Dictionary<string, object?>(), []), ["department"], AggregationMeta, minGroupSize, true);

    // GHSA-rxr4-mf28-p2hv (details: private advisory).
    [Theory]
    [InlineData("case_01")]
    [InlineData("case_02")]
    [InlineData("case_03")]
    [InlineData("case_04")]
    [InlineData("case_05")]
    [InlineData("case_06")]
    [InlineData("case_07")]
    public void Ghsa_rxr4_p2hv_regression(string bullet)
    {
        switch (bullet)
        {
            case "case_01":
                Should.Throw<AggregationFilterRejectedException>(() => EnforceAggregationFilter("employee_id IN (1, 2, 3)"));
                break;
            case "case_02":
                Should.Throw<AggregationFilterRejectedException>(() => EnforceAggregationFilter("email LIKE 'a%'"));
                break;
            case "case_03":
                Should.Throw<AggregationFilterRejectedException>(() => EnforceAggregationFilter("tenant_part <= 3"));
                break;
            case "case_04":
                Should.NotThrow(() => EnforceAggregationFilter("department IN ('a', 'b')"));
                break;
            case "case_05":
                new AnalyticsOptions().Privacy.DefaultMinGroupSize.ShouldBeGreaterThanOrEqualTo(2);
                break;
            case "case_06":
                new AnalyticsOptionsValidator().Validate(null, new AnalyticsOptions { Privacy = new AnalyticsPrivacyOptions { DefaultMinGroupSize = 1 } }).Failed.ShouldBeTrue();
                new AnalyticsOptionsValidator().Validate(null, new AnalyticsOptions { Privacy = new AnalyticsPrivacyOptions { DefaultMinGroupSize = 0 } }).Failed.ShouldBeTrue();
                break;
            case "case_07":
                new AnalyticsPrivacyOptions().ExposeSuppressedGroupsCount.ShouldBeFalse();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(bullet), bullet, null);
        }
    }
}
