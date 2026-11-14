namespace Autheris.Tests.Unit.DataApi;

using System;
using System.Collections.Generic;
using System.Text.Json;
using Autheris.Application.DataSources.Interfaces;
using Autheris.Application.DataSources.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class GovernedTableReadPlannerKeysetRequiredFilterTests
{
    private static readonly TableIdentifier Orders = new("sales", "dbo", "orders");

    private static TableMetadata Meta(string[]? required = null) => new()
    {
        Identifier = Orders,
        Table = new Table
        {
            Id = Guid.NewGuid(),
            SourceName = "sales",
            SchemaName = "dbo",
            TableName = "orders",
            DataSourceType = DataSourceType.Sql,
            IsActive = true,
            RequiredFilterColumns = [.. (required ?? ["region"])]
        },
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "int" },
            new TableColumn { ColumnName = "region", DataType = "varchar" },
            new TableColumn { ColumnName = "status", DataType = "varchar" }
        ],
        PrimaryKeyColumns = ["id"]
    };

    private static TableAccessDecision Decision() => TableAccessDecision.Allowed(
        Orders,
        new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = ColumnAccessLevel.Clear,
            ["region"] = ColumnAccessLevel.Clear,
            ["status"] = ColumnAccessLevel.Clear
        },
        hasUnconstrainedColumnAllow: true);

    private static JsonElement JsonVal(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement.Clone();
    }

    [Theory]
    [InlineData("neq", "\"EU\"")]
    [InlineData("ne", "\"EU\"")]
    [InlineData("<>", "\"EU\"")]
    [InlineData("like", "\"%\"")]
    [InlineData("isnull", "null")]
    [InlineData("isnotnull", "null")]
    [InlineData("eq", "null")]
    [InlineData("gt", "null")]
    [InlineData("in", "[]")]
    [InlineData("in", "[null]")]
    [InlineData("notin", "[]")]
    [InlineData("notin", "[\"EU\"]")]
    public void Keyset_WeakOrNullOperator_DoesNotSatisfy(string op, string rawJson)
    {
        var planner = new GovernedTableReadPlanner();
        var request = new BulkTableStreamRequest(
            Filter: new TypedFilterNode(Field: "region", Op: op, Value: JsonVal(rawJson)));

        Should.Throw<RequiredFilterColumnMissingException>(() =>
            planner.PlanKeysetStream(request, Meta(), Decision()));
    }

    [Fact]
    public void Keyset_NotFilter_DoesNotSatisfy()
    {
        var planner = new GovernedTableReadPlanner();
        var request = new BulkTableStreamRequest(
            Filter: new TypedFilterNode(Not: new TypedFilterNode(Field: "region", Op: "eq", Value: JsonVal("\"EU\""))));

        Should.Throw<RequiredFilterColumnMissingException>(() =>
            planner.PlanKeysetStream(request, Meta(), Decision()));
    }

    [Fact]
    public void Keyset_OrWithUnconstrainedBranch_DoesNotSatisfy()
    {
        var planner = new GovernedTableReadPlanner();
        var request = new BulkTableStreamRequest(
            Filter: new TypedFilterNode(Or:
            [
                new TypedFilterNode(Field: "region", Op: "eq", Value: JsonVal("\"EU\"")),
                new TypedFilterNode(Field: "status", Op: "eq", Value: JsonVal("\"open\""))
            ]));

        Should.Throw<RequiredFilterColumnMissingException>(() =>
            planner.PlanKeysetStream(request, Meta(), Decision()));
    }

    [Theory]
    [InlineData("eq", "\"EU\"")]
    [InlineData("gt", "\"A\"")]
    [InlineData("gte", "\"A\"")]
    [InlineData("lt", "\"Z\"")]
    [InlineData("lte", "\"Z\"")]
    [InlineData("in", "[\"EU\", \"US\"]")]
    public void Keyset_StrictOperator_Satisfies(string op, string rawJson)
    {
        var planner = new GovernedTableReadPlanner();
        var request = new BulkTableStreamRequest(
            Filter: new TypedFilterNode(Field: "region", Op: op, Value: JsonVal(rawJson)));

        Should.NotThrow(() => planner.PlanKeysetStream(request, Meta(), Decision()));
    }

    [Fact]
    public void Keyset_OrConstrainedOnAllBranches_And_AndBranch_Satisfy()
    {
        var planner = new GovernedTableReadPlanner();
        var orReq = new BulkTableStreamRequest(
            Filter: new TypedFilterNode(Or:
            [
                new TypedFilterNode(Field: "region", Op: "eq", Value: JsonVal("\"EU\"")),
                new TypedFilterNode(Field: "region", Op: "eq", Value: JsonVal("\"US\""))
            ]));
        Should.NotThrow(() => planner.PlanKeysetStream(orReq, Meta(), Decision()));

        var andReq = new BulkTableStreamRequest(
            Filter: new TypedFilterNode(And:
            [
                new TypedFilterNode(Field: "status", Op: "eq", Value: JsonVal("\"open\"")),
                new TypedFilterNode(Field: "region", Op: "eq", Value: JsonVal("\"EU\""))
            ]));
        Should.NotThrow(() => planner.PlanKeysetStream(andReq, Meta(), Decision()));
    }
}
