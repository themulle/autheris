namespace Autheris.Tests.Unit.Data;

using System;
using System.Collections.Generic;
using Autheris.Application.Analytics;
using Autheris.Application.Data.Interfaces;
using Autheris.Application.Data.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

public sealed class TimeBucketingQueryPlanningTests
{
    private static readonly TableIdentifier LogsTableId = new("ops", "dbo", "logs");

    private static TableMetadata CreateTableForDialect(string sourceType) => new()
    {
        Identifier = LogsTableId,
        PrimaryKeyColumns = ["id"],
        Table = new Table
        {
            SourceName = "LogsSource",
            SchemaName = "dbo",
            TableName = "logs",
            SourceType = sourceType,
            DataSourceType = DataSourceType.Sql,
            IsActive = true
        },
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "bigint" },
            new TableColumn { ColumnName = "created_at", DataType = sourceType == "SqlServer" ? "datetime2" : "timestamp" },
            new TableColumn { ColumnName = "level", DataType = "varchar" },
            new TableColumn { ColumnName = "latency_ms", DataType = "double" }
        ]
    };

    [Theory]
    [InlineData("PostgreSQL", TimeBucketInterval.Hour, "date_trunc('hour', \"created_at\")")]
    [InlineData("PostgreSQL", TimeBucketInterval.Day, "date_trunc('day', \"created_at\")")]
    [InlineData("PostgreSQL", TimeBucketInterval.Week, "date_trunc('week', \"created_at\")")]
    [InlineData("PostgreSQL", TimeBucketInterval.Month, "date_trunc('month', \"created_at\")")]
    [InlineData("PostgreSQL", TimeBucketInterval.Quarter, "date_trunc('quarter', \"created_at\")")]
    [InlineData("PostgreSQL", TimeBucketInterval.Year, "date_trunc('year', \"created_at\")")]
    public void PlanAggregate_PostgreSql_EmitsDateTrunc(string sourceType, TimeBucketInterval interval, string expectedExpr)
    {
        var metadata = CreateTableForDialect(sourceType);
        var decision = TableAccessDecision.Allowed(LogsTableId, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true);

        var planner = new GovernedTableReadPlanner();
        var intent = new TableAggregateIntent(
            LogsTableId,
            GroupBy: ["created_at"],
            Aggregates: [new AggregateSelection(AggregateFunction.Count, Alias: "cnt")],
            Filter: null,
            OrderBy: null,
            Limit: 100,
            TimeBuckets: [new TimeBucketSpecification("created_at", interval, "UTC")]);

        var planned = planner.PlanAggregate(intent, metadata, decision);

        planned.Sql.ShouldContain($"{expectedExpr} AS \"created_at\"");
        planned.Sql.ShouldContain($"GROUP BY {expectedExpr}");
    }

    [Fact]
    public void PlanAggregate_SqlServer_EmitsCanonicalConvertTimezone()
    {
        var metadata = CreateTableForDialect("SqlServer");
        var decision = TableAccessDecision.Allowed(LogsTableId, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true);

        var planner = new GovernedTableReadPlanner();
        var intent = new TableAggregateIntent(
            LogsTableId,
            GroupBy: ["created_at"],
            Aggregates: [new AggregateSelection(AggregateFunction.Sum, "latency_ms", "total_latency")],
            Filter: null,
            OrderBy: null,
            Limit: 100,
            TimeBuckets: [new TimeBucketSpecification("created_at", TimeBucketInterval.Day, "Europe/Berlin")]);

        var planned = planner.PlanAggregate(intent, metadata, decision);

        planned.Sql.ShouldContain("date_trunc('day', convert_timezone('UTC', 'Europe/Berlin', \"created_at\")) AS \"created_at\"");
        planned.Sql.ShouldContain("GROUP BY date_trunc('day', convert_timezone('UTC', 'Europe/Berlin', \"created_at\"))");
    }

    [Fact]
    public void PlanAggregate_Sqlite_WithNonUtcTimeZone_AppliesOffset()
    {
        var metadata = CreateTableForDialect("Sqlite");
        var decision = TableAccessDecision.Allowed(LogsTableId, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true);

        var planner = new GovernedTableReadPlanner();
        var intent = new TableAggregateIntent(
            LogsTableId,
            GroupBy: ["created_at"],
            Aggregates: [new AggregateSelection(AggregateFunction.Count, Alias: "cnt")],
            Filter: null,
            OrderBy: null,
            Limit: 100,
            TimeBuckets: [new TimeBucketSpecification("created_at", TimeBucketInterval.Hour, "Europe/Berlin")]);

        var planned = planner.PlanAggregate(intent, metadata, decision);

        planned.Sql.ShouldContain("strftime('%Y-%m-%d %H:00:00', datetime(\"created_at\", ");
    }

    [Fact]
    public void PlanAggregate_Sqlite_EmitsStrftime()
    {
        var metadata = CreateTableForDialect("Sqlite");
        var decision = TableAccessDecision.Allowed(LogsTableId, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true);

        var planner = new GovernedTableReadPlanner();
        var intent = new TableAggregateIntent(
            LogsTableId,
            GroupBy: ["created_at"],
            Aggregates: [new AggregateSelection(AggregateFunction.Count, Alias: "cnt")],
            Filter: null,
            OrderBy: null,
            Limit: 100,
            TimeBuckets: [new TimeBucketSpecification("created_at", TimeBucketInterval.Day, "UTC")]);

        var planned = planner.PlanAggregate(intent, metadata, decision);

        planned.Sql.ShouldContain("strftime('%Y-%m-%d', datetime(\"created_at\")) AS \"created_at\"");
        planned.Sql.ShouldContain("GROUP BY strftime('%Y-%m-%d', datetime(\"created_at\"))");
    }
}
