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
    [InlineData("in_list_on_key_column_is_rejected")]
    [InlineData("like_on_unique_column_is_rejected")]
    [InlineData("composite_key_member_is_isolating")]
    [InlineData("set_filter_on_group_column_stays_allowed")]
    [InlineData("secure_non_zero_default_threshold")]
    [InlineData("threshold_below_floor_fails_validation")]
    [InlineData("suppressed_group_count_is_not_exposed_by_default")]
    public void Ghsa_rxr4_p2hv_regression(string bullet)
    {
        switch (bullet)
        {
            case "in_list_on_key_column_is_rejected":
                Should.Throw<AggregationFilterRejectedException>(() => EnforceAggregationFilter("employee_id IN (1, 2, 3)"));
                break;
            case "like_on_unique_column_is_rejected":
                Should.Throw<AggregationFilterRejectedException>(() => EnforceAggregationFilter("email LIKE 'a%'"));
                break;
            case "composite_key_member_is_isolating":
                Should.Throw<AggregationFilterRejectedException>(() => EnforceAggregationFilter("tenant_part <= 3"));
                break;
            case "set_filter_on_group_column_stays_allowed":
                Should.NotThrow(() => EnforceAggregationFilter("department IN ('a', 'b')"));
                break;
            case "secure_non_zero_default_threshold":
                new AnalyticsOptions().Privacy.DefaultMinGroupSize.ShouldBeGreaterThanOrEqualTo(2);
                break;
            case "threshold_below_floor_fails_validation":
                new AnalyticsOptionsValidator().Validate(null, new AnalyticsOptions { Privacy = new AnalyticsPrivacyOptions { DefaultMinGroupSize = 1 } }).Failed.ShouldBeTrue();
                new AnalyticsOptionsValidator().Validate(null, new AnalyticsOptions { Privacy = new AnalyticsPrivacyOptions { DefaultMinGroupSize = 0 } }).Failed.ShouldBeTrue();
                break;
            case "suppressed_group_count_is_not_exposed_by_default":
                new AnalyticsPrivacyOptions().ExposeSuppressedGroupsCount.ShouldBeFalse();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(bullet), bullet, null);
        }
    }
}
