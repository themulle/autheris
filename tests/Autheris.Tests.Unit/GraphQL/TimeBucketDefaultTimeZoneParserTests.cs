namespace Autheris.Tests.Unit.GraphQL;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Application.Analytics;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.GraphQL.Catalog;
using HotChocolate;
using HotChocolate.Language;
using Shouldly;
using Xunit;

public sealed class TimeBucketDefaultTimeZoneParserTests
{
    private static readonly TableIdentifier EventsTableId = new("app", "public", "events");

    internal static CatalogTableType CreateCatalogTable() => new(
        EventsTableId,
        "app_events",
        "app_events",
        "app_events_filter",
        "app_events_order_by",
        new TableMetadata
        {
            Identifier = EventsTableId,
            PrimaryKeyColumns = ["id"],
            Table = new Table { SourceName = "MainDb", SchemaName = "public", TableName = "events", SourceType = "PostgreSQL", DataSourceType = DataSourceType.Sql, IsActive = true },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "bigint" },
                new TableColumn { ColumnName = "created_at", DataType = "timestamptz" },
                new TableColumn { ColumnName = "category", DataType = "varchar" }
            ]
        },
        [
            new CatalogColumnField("id", "id", CatalogFieldType.Long, "bigint", false),
            new CatalogColumnField("created_at", "created_at", CatalogFieldType.DateTime, "timestamptz", false),
            new CatalogColumnField("category", "category", CatalogFieldType.String, "varchar", false)
        ],
        []);

    private static ObjectValueNode Bucket(string bucket = "DAY", IValueNode? timeZone = null, bool withZone = false)
    {
        var fields = new List<ObjectFieldNode>
        {
            new("column", new EnumValueNode("created_at")),
            new("bucket", new EnumValueNode(bucket))
        };
        if (withZone)
        {
            fields.Add(new ObjectFieldNode("timeZone", timeZone ?? NullValueNode.Default));
        }

        return new ObjectValueNode(fields);
    }

    private static readonly ListValueNode CountAggregate = new(
        new ObjectValueNode(
            new ObjectFieldNode("fn", new EnumValueNode("COUNT")),
            new ObjectFieldNode("alias", new StringValueNode("n"))));

    internal static AggregationOptions Berlin() => new() { DefaultTimeZone = "Europe/Berlin" };

    private static ParsedAggregateArguments ParseGroupByObject(ObjectValueNode bucket, AggregationOptions options, Func<string, IValueNode?>? variable = null) =>
        AggregateArgumentParser.Parse(CreateCatalogTable(), new ListValueNode(bucket), CountAggregate, null, null, options, variable ?? (_ => null));

    internal static ParsedAggregateArguments ParseTimeBuckets(ObjectValueNode bucket, AggregationOptions options, Func<string, IValueNode?>? variable = null, IValueNode? limit = null) =>
        AggregateArgumentParser.Parse(CreateCatalogTable(), null, CountAggregate, null, limit, options, variable ?? (_ => null), new ListValueNode(bucket));

    [Fact]
    public void Parse_DefaultOptions_OmittedTimeZone_IsUtc()
    {
        ParseGroupByObject(Bucket(), new AggregationOptions()).TimeBuckets!.Single().TimeZone.ShouldBe("UTC");
        ParseTimeBuckets(Bucket(), new AggregationOptions()).TimeBuckets!.Single().TimeZone.ShouldBe("UTC");
    }

    [Fact]
    public void Parse_OmittedTimeZone_BerlinDefault_GroupByObject_UsesBerlin() =>
        ParseGroupByObject(Bucket(), Berlin()).TimeBuckets!.Single().TimeZone.ShouldBe("Europe/Berlin");

    [Fact]
    public void Parse_OmittedTimeZone_BerlinDefault_TimeBucketsArgument_UsesBerlin() =>
        ParseTimeBuckets(Bucket("HOUR"), Berlin()).TimeBuckets!.Single().TimeZone.ShouldBe("Europe/Berlin");

    [Fact]
    public void Parse_NullLiteralTimeZone_UsesDefault() =>
        ParseTimeBuckets(Bucket(withZone: true, timeZone: NullValueNode.Default), Berlin()).TimeBuckets!.Single().TimeZone.ShouldBe("Europe/Berlin");

    [Fact]
    public void Parse_NullOrUndefinedVariableTimeZone_UsesDefault()
    {
        var bucket = Bucket(withZone: true, timeZone: new VariableNode("tz"));

        ParseTimeBuckets(bucket, Berlin(), _ => NullValueNode.Default).TimeBuckets!.Single().TimeZone.ShouldBe("Europe/Berlin");
        ParseTimeBuckets(bucket, Berlin(), _ => null).TimeBuckets!.Single().TimeZone.ShouldBe("Europe/Berlin");
    }

    [Fact]
    public void Parse_ExplicitUtcLiteral_WinsOverBerlinDefault() =>
        ParseTimeBuckets(Bucket(withZone: true, timeZone: new StringValueNode("UTC")), Berlin()).TimeBuckets!.Single().TimeZone.ShouldBe("UTC");

    [Fact]
    public void Parse_ExplicitUtcVariable_WinsOverBerlinDefault() =>
        ParseTimeBuckets(Bucket(withZone: true, timeZone: new VariableNode("tz")), Berlin(), _ => new StringValueNode("UTC"))
            .TimeBuckets!.Single().TimeZone.ShouldBe("UTC");

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Parse_BlankDefaultBypassingValidator_FailsClosedWithFixedMessage(string? zone)
    {
        var options = new AggregationOptions { DefaultTimeZone = zone! };

        var ex = Should.Throw<GraphQLException>(() => ParseTimeBuckets(Bucket(), options));

        ex.Errors.Single().Code.ShouldBe("INVALID_QUERY");
        ex.Errors.Single().Message.ShouldBe("The time zone is not a recognized IANA zone id.");
    }

    [Fact]
    public void Parse_PlainGroupByWithBerlinDefault_CreatesNoTimeBucket()
    {
        var parsed = AggregateArgumentParser.Parse(
            CreateCatalogTable(), new ListValueNode(new EnumValueNode("category")), CountAggregate, null, null, Berlin(), _ => null);

        parsed.TimeBuckets.ShouldBeNull();
    }
}
