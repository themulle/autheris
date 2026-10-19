namespace Autheris.Tests.Unit.GraphQL;

using System;
using System.Collections.Generic;
using Autheris.Application.Analytics;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.GraphQL.Catalog;
using HotChocolate.Language;
using Shouldly;
using Xunit;

public sealed class TimeBucketingAggregateParserTests
{
    private static readonly TableIdentifier EventsTableId = new("app", "public", "events");

    private static CatalogTableType CreateCatalogTable()
    {
        var meta = new TableMetadata
        {
            Identifier = EventsTableId,
            PrimaryKeyColumns = ["id"],
            Table = new Table
            {
                SourceName = "MainDb",
                SchemaName = "public",
                TableName = "events",
                SourceType = "PostgreSQL",
                DataSourceType = DataSourceType.Sql,
                IsActive = true
            },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "bigint" },
                new TableColumn { ColumnName = "created_at", DataType = "timestamptz" },
                new TableColumn { ColumnName = "category", DataType = "varchar" },
                new TableColumn { ColumnName = "duration_ms", DataType = "int" }
            ]
        };

        return new CatalogTableType(
            EventsTableId,
            "app_events",
            "app_events",
            "app_events_filter",
            "app_events_order_by",
            meta,
            [
                new CatalogColumnField("id", "id", CatalogFieldType.Long, "bigint", false),
                new CatalogColumnField("created_at", "created_at", CatalogFieldType.DateTime, "timestamptz", false),
                new CatalogColumnField("category", "category", CatalogFieldType.String, "varchar", false),
                new CatalogColumnField("duration_ms", "duration_ms", CatalogFieldType.Int, "int", false)
            ],
            []);
    }

    [Fact]
    public void Parse_StructuredGroupByTimeBucket_ParsesIntervalAndTimeZone()
    {
        var table = CreateCatalogTable();

        var groupByNode = new ListValueNode(
            new ObjectValueNode(
                new ObjectFieldNode("column", new EnumValueNode("created_at")),
                new ObjectFieldNode("bucket", new EnumValueNode("WEEK")),
                new ObjectFieldNode("timeZone", new StringValueNode("Europe/Berlin"))),
            new EnumValueNode("category"));

        var aggregatesNode = new ListValueNode(
            new ObjectValueNode(
                new ObjectFieldNode("fn", new EnumValueNode("COUNT")),
                new ObjectFieldNode("alias", new StringValueNode("event_count"))));

        var parsed = AggregateArgumentParser.Parse(
            table,
            groupByNode,
            aggregatesNode,
            null,
            null,
            new AggregationOptions(),
            _ => null);

        parsed.GroupBy.Count.ShouldBe(2);
        parsed.GroupBy[0].ColumnName.ShouldBe("created_at");
        parsed.GroupBy[1].ColumnName.ShouldBe("category");

        parsed.TimeBuckets.ShouldNotBeNull();
        parsed.TimeBuckets!.Count.ShouldBe(1);
        parsed.TimeBuckets[0].Column.ShouldBe("created_at");
        parsed.TimeBuckets[0].Interval.ShouldBe(TimeBucketInterval.Week);
        parsed.TimeBuckets[0].TimeZone.ShouldBe("Europe/Berlin");
    }

    [Fact]
    public void Parse_SeparateTimeBucketsArgument_ParsesSuccessfully()
    {
        var table = CreateCatalogTable();

        var groupByNode = new ListValueNode(new EnumValueNode("category"));
        var timeBucketsNode = new ListValueNode(
            new ObjectValueNode(
                new ObjectFieldNode("column", new EnumValueNode("created_at")),
                new ObjectFieldNode("bucket", new EnumValueNode("MONTH"))));

        var aggregatesNode = new ListValueNode(
            new ObjectValueNode(
                new ObjectFieldNode("fn", new EnumValueNode("SUM")),
                new ObjectFieldNode("column", new EnumValueNode("duration_ms")),
                new ObjectFieldNode("alias", new StringValueNode("total_duration"))));

        var parsed = AggregateArgumentParser.Parse(
            table,
            groupByNode,
            aggregatesNode,
            null,
            null,
            new AggregationOptions(),
            _ => null,
            timeBucketsNode);

        parsed.GroupBy.Count.ShouldBe(2);
        parsed.GroupBy.Select(g => g.ColumnName).ShouldContain("category");
        parsed.GroupBy.Select(g => g.ColumnName).ShouldContain("created_at");

        parsed.TimeBuckets.ShouldNotBeNull();
        parsed.TimeBuckets!.Count.ShouldBe(1);
        parsed.TimeBuckets[0].Column.ShouldBe("created_at");
        parsed.TimeBuckets[0].Interval.ShouldBe(TimeBucketInterval.Month);
        parsed.TimeBuckets[0].TimeZone.ShouldBe("UTC"); // Defaults to UTC!
    }
}
