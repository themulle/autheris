namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Analytics;
using Autheris.Application.Data.Interfaces;
using Autheris.Application.Data.Services;
using Autheris.Application.Interfaces;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.GraphQL.Catalog;
using FluentAssertions;
using HotChocolate.Language;
using Shouldly;
using Xunit;

/// <summary>
/// Mandatory Phase 3 security tests for REQ-DATA-112B / Issue #112 Addendum:
/// SEC-DATA112B-01..05 time-bucketing aggregations and SQL pushdown security guardrails.
/// </summary>
public sealed class TimeBucketingAggregationSecurityTests
{
    private static readonly TableIdentifier TelemetryTableId = new("telemetry", "dbo", "metrics");

    private static TableMetadata CreateTelemetryTable(bool withRequiredFilter = false) => new()
    {
        Identifier = TelemetryTableId,
        PrimaryKeyColumns = ["id"],
        Table = new Table
        {
            SourceName = "TelemetrySource",
            SchemaName = "dbo",
            TableName = "metrics",
            SourceType = "PostgreSQL",
            DataSourceType = DataSourceType.Sql,
            IsActive = true,
            RequiredFilterColumns = withRequiredFilter ? ["tenant_id"] : []
        },
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "bigint" },
            new TableColumn { ColumnName = "tenant_id", DataType = "varchar" },
            new TableColumn { ColumnName = "device_id", DataType = "varchar" },
            new TableColumn { ColumnName = "ts", DataType = "timestamptz" },
            new TableColumn { ColumnName = "bytes_sent", DataType = "bigint" }
        ]
    };

    [Fact]
    [Trait("Category", "Security")]
    public void T_SEC_DATA112B_01_WhitelistIntervalsAndStrictIanaTimeZoneValidation()
    {
        // SEC-DATA112B-01: Time zones must strictly belong to the closed IANA registry set; injection attempts fail closed
        var tableType = new CatalogTableType(
            TelemetryTableId,
            "telemetry_metrics",
            "telemetry_metrics",
            "telemetry_metrics_filter",
            "telemetry_metrics_order_by",
            CreateTelemetryTable(),
            [
                new CatalogColumnField("ts", "ts", CatalogFieldType.DateTime, "timestamptz", false),
                new CatalogColumnField("bytes_sent", "bytes_sent", CatalogFieldType.Long, "bigint", false)
            ],
            []);

        var maliciousTimeZones = new[]
        {
            "Invalid/TimeZone",
            "UTC'; DROP TABLE metrics;--",
            "Europe/Berlin' OR '1'='1",
            "../../etc/passwd",
            ""
        };

        foreach (var badTz in maliciousTimeZones)
        {
            var groupByNode = new ListValueNode(
                new ObjectValueNode(
                    new ObjectFieldNode("column", new EnumValueNode("ts")),
                    new ObjectFieldNode("bucket", new EnumValueNode("DAY")),
                    new ObjectFieldNode("timeZone", new StringValueNode(badTz))));

            var act = () => AggregateArgumentParser.Parse(
                tableType,
                groupByNode,
                new ListValueNode(new ObjectValueNode(new ObjectFieldNode("fn", new EnumValueNode("COUNT")), new ObjectFieldNode("alias", new StringValueNode("cnt")))),
                null,
                null,
                new AggregationOptions(),
                _ => null);

            act.ShouldThrow<HotChocolate.GraphQLException>()
                .Errors[0].Code.ShouldBe("INVALID_QUERY");
        }

        // Valid IANA timezone must succeed
        var validGroupBy = new ListValueNode(
            new ObjectValueNode(
                new ObjectFieldNode("column", new EnumValueNode("ts")),
                new ObjectFieldNode("bucket", new EnumValueNode("DAY")),
                new ObjectFieldNode("timeZone", new StringValueNode("Europe/Berlin"))));

        var parsed = AggregateArgumentParser.Parse(
            tableType,
            validGroupBy,
            new ListValueNode(new ObjectValueNode(new ObjectFieldNode("fn", new EnumValueNode("COUNT")), new ObjectFieldNode("alias", new StringValueNode("cnt")))),
            null,
            null,
            new AggregationOptions(),
            _ => null);

        parsed.TimeBuckets.ShouldNotBeNull();
        parsed.TimeBuckets!.Count.ShouldBe(1);
        parsed.TimeBuckets[0].TimeZone.ShouldBe("Europe/Berlin");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void T_SEC_DATA112B_02_RequiredFilterColumnsEnforced()
    {
        // SEC-DATA112B-02: Missing required filter column on aggregation fails closed with REQUIRED_FILTER_MISSING
        var metadata = CreateTelemetryTable(withRequiredFilter: true);
        var decision = TableAccessDecision.Allowed(TelemetryTableId, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true);

        var planner = new GovernedTableReadPlanner();
        var intentWithoutRequiredFilter = new TableAggregateIntent(
            TelemetryTableId,
            GroupBy: ["ts"],
            Aggregates: [new AggregateSelection(AggregateFunction.Count, Alias: "cnt")],
            Filter: null, // Missing tenant_id filter!
            OrderBy: null,
            Limit: 100,
            TimeBuckets: [new TimeBucketSpecification("ts", TimeBucketInterval.Day, "UTC")]);

        var ex = Should.Throw<TrinoSqlEngine.Ast.Emit.RequiredFilterColumnMissingException>(
            () => planner.PlanAggregate(intentWithoutRequiredFilter, metadata, decision));

        // When mapped in AggregateFailureMapper, verify it maps to REQUIRED_FILTER_MISSING
        var mapped = AggregateFailureMapper.ForAggregate(ex);
        mapped.ShouldNotBeNull();
        mapped!.Errors[0].Code.ShouldBe("REQUIRED_FILTER_MISSING");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void T_SEC_DATA112B_03_GroupCardinalityCapBounded()
    {
        // SEC-DATA112B-03: Time-bucketed aggregation queries cannot exceed a limit of 1000 groups
        var tableType = new CatalogTableType(
            TelemetryTableId,
            "telemetry_metrics",
            "telemetry_metrics",
            "telemetry_metrics_filter",
            "telemetry_metrics_order_by",
            CreateTelemetryTable(),
            [
                new CatalogColumnField("ts", "ts", CatalogFieldType.DateTime, "timestamptz", false),
                new CatalogColumnField("bytes_sent", "bytes_sent", CatalogFieldType.Long, "bigint", false)
            ],
            []);

        var groupByNode = new ListValueNode(
            new ObjectValueNode(
                new ObjectFieldNode("column", new EnumValueNode("ts")),
                new ObjectFieldNode("bucket", new EnumValueNode("HOUR")),
                new ObjectFieldNode("timeZone", new StringValueNode("UTC"))));

        // Limit = 1001 must fail closed with QUERY_TOO_COMPLEX
        var act = () => AggregateArgumentParser.Parse(
            tableType,
            groupByNode,
            new ListValueNode(new ObjectValueNode(new ObjectFieldNode("fn", new EnumValueNode("COUNT")), new ObjectFieldNode("alias", new StringValueNode("cnt")))),
            null,
            new IntValueNode(1001),
            new AggregationOptions(),
            _ => null);

        var ex = act.ShouldThrow<HotChocolate.GraphQLException>();
        ex.Errors[0].Code.ShouldBe("QUERY_TOO_COMPLEX");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void T_SEC_DATA112B_04_SqlInjectionDefenseTimeZonesAndUnits()
    {
        // SEC-DATA112B-04: Dialect date truncation never interpolates raw input and only uses verified registry instances
        var metadata = CreateTelemetryTable();
        var decision = TableAccessDecision.Allowed(TelemetryTableId, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true);

        var planner = new GovernedTableReadPlanner();
        var intent = new TableAggregateIntent(
            TelemetryTableId,
            GroupBy: ["ts"],
            Aggregates: [new AggregateSelection(AggregateFunction.Count, Alias: "cnt")],
            Filter: null,
            OrderBy: null,
            Limit: 50,
            TimeBuckets: [new TimeBucketSpecification("ts", TimeBucketInterval.Day, "Europe/Berlin")]);

        var planned = planner.PlanAggregate(intent, metadata, decision);

        // SQL statement must contain exact PostgreSQL date_trunc and AT TIME ZONE 'Europe/Berlin'
        planned.Sql.ShouldContain("date_trunc('day', \"ts\" AT TIME ZONE 'Europe/Berlin') AS \"ts\"");
        planned.Sql.ShouldContain("GROUP BY date_trunc('day', \"ts\" AT TIME ZONE 'Europe/Berlin')");
        planned.Sql.ShouldNotContain("';");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void T_SEC_DATA112B_05_MaskedColumnInteroperability()
    {
        // SEC-DATA112B-05: Time-bucketing on a masked column fails closed with AGGREGATION_ON_MASKED_COLUMN
        var metadata = CreateTelemetryTable();
        var decision = TableAccessDecision.Allowed(
            TelemetryTableId,
            new Dictionary<string, ColumnAccessLevel> { ["ts"] = ColumnAccessLevel.Mask },
            hasUnconstrainedColumnAllow: false);

        var planner = new GovernedTableReadPlanner();
        var intent = new TableAggregateIntent(
            TelemetryTableId,
            GroupBy: ["ts"],
            Aggregates: [new AggregateSelection(AggregateFunction.Count, Alias: "cnt")],
            Filter: null,
            OrderBy: null,
            Limit: 50,
            TimeBuckets: [new TimeBucketSpecification("ts", TimeBucketInterval.Day, "UTC")]);

        var ex = Should.Throw<MaskedAggregationException>(
            () => planner.PlanAggregate(intent, metadata, decision));

        ex.ErrorCode.ShouldBe("AGGREGATION_ON_MASKED_COLUMN");
    }
}
