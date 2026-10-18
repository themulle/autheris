using System;
using System.Collections.Generic;
using Autheris.Application.Analytics;
using Autheris.Application.Data.Interfaces;
using Autheris.Application.Data.Services;
using Autheris.Application.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Sql.Policy;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;
using Xunit;

namespace Autheris.Tests.Unit.DataApi;

[Trait("Category", "Security")]
public sealed class GovernedTableReadPlannerRequiredFilterTests
{
    private static readonly TableIdentifier Customers = new("sales", "dbo", "customers");

    private readonly GovernedTableReadPlanner _planner = new();

    private static TableMetadata Metadata(string[]? requiredFilterColumns = null, bool includeTenant = false) => new()
    {
        Identifier = Customers,
        Table = new Table
        {
            Id = Guid.NewGuid(),
            SourceName = "sales",
            SchemaName = "dbo",
            TableName = "customers",
            DataSourceType = DataSourceType.Sql,
            IsActive = true,
            RequiredFilterColumns = [.. (requiredFilterColumns ?? ["region"])]
        },
        Columns = includeTenant
            ? [
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "region", DataType = "varchar" },
                new TableColumn { ColumnName = "tenant_id", DataType = "varchar" },
                new TableColumn { ColumnName = "name", DataType = "varchar" },
                new TableColumn { ColumnName = "status", DataType = "varchar" }
              ]
            : [
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "region", DataType = "varchar" },
                new TableColumn { ColumnName = "name", DataType = "varchar" },
                new TableColumn { ColumnName = "status", DataType = "varchar" }
              ],
        PrimaryKeyColumns = ["id"]
    };

    private static TableAccessDecision Decision(IRowPolicy? rowPolicy = null) => TableAccessDecision.Allowed(
        Customers,
        new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = ColumnAccessLevel.Clear,
            ["region"] = ColumnAccessLevel.Clear,
            ["tenant_id"] = ColumnAccessLevel.Clear,
            ["name"] = ColumnAccessLevel.Clear,
            ["status"] = ColumnAccessLevel.Clear
        },
        hasUnconstrainedColumnAllow: true) with { RowPolicy = rowPolicy };

    [Fact]
    [Trait("Category", "Security")]
    public void Plan_MissingFilter_ThrowsRequiredFilterException()
    {
        var meta = Metadata(["region"]);
        var decision = Decision();
        var intent = new TableReadIntent(Customers, ["id"], null, null, 10, 0, "Rest");

        var ex = Should.Throw<RequiredFilterColumnMissingException>(() =>
            _planner.Plan(intent, meta, decision));

        ex.Message.ShouldBe(GovernedSqlErrorCodes.FixedMessage(GovernedSqlErrorCodes.RequiredFilterColumnMissing));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void PlanCount_MissingFilter_ThrowsRequiredFilterException()
    {
        var meta = Metadata(["region"]);
        var decision = Decision();
        var intent = new TableReadIntent(Customers, ["id"], null, null, 10, 0, "Rest");

        var ex = Should.Throw<RequiredFilterColumnMissingException>(() =>
            _planner.PlanCount(intent, meta, decision));

        ex.Message.ShouldBe(GovernedSqlErrorCodes.FixedMessage(GovernedSqlErrorCodes.RequiredFilterColumnMissing));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void PlanAggregate_MissingFilter_ThrowsRequiredFilterException()
    {
        var meta = Metadata(["region"]);
        var decision = Decision();
        var intent = new TableAggregateIntent(Customers, ["status"], [new AggregateSelection(AggregateFunction.Count)], null, null, 10, "Rest");

        var ex = Should.Throw<RequiredFilterColumnMissingException>(() =>
            _planner.PlanAggregate(intent, meta, decision));

        ex.Message.ShouldBe(GovernedSqlErrorCodes.FixedMessage(GovernedSqlErrorCodes.RequiredFilterColumnMissing));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Plan_WithFilterClause_Passes()
    {
        var meta = Metadata(["region"]);
        var decision = Decision();
        var filter = TableFilterClause.FromTrinoText("\"region\" = 'EU'", ["region"]);
        var intent = new TableReadIntent(Customers, ["id"], filter, null, 10, 0, "Rest");

        var planned = _planner.Plan(intent, meta, decision);
        planned.ShouldNotBeNull();
        planned.Sql.ShouldContain("region");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Plan_WithActiveRlsDecision_Passes()
    {
        var meta = Metadata(["region"]);
        var pred = PolicyPredicate.Create(
            new BinaryExpression(
                new ColumnReference(new SqlQualifiedName("region")),
                BinaryOperator.Equal,
                new LiteralExpression("EU", LiteralType.String)),
            new Dictionary<string, PolicyValue>());
        var rls = new PolicyPredicateRowPolicy(pred, TargetSqlDialect.PostgreSql, new PolicyPredicateRenderer());
        var decision = Decision(rowPolicy: rls);
        var intent = new TableReadIntent(Customers, ["id"], null, null, 10, 0, "Rest");

        var planned = _planner.Plan(intent, meta, decision);
        planned.ShouldNotBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Plan_WithTenantColumn_Passes()
    {
        var meta = Metadata(["tenant_id"], includeTenant: true);
        var decision = Decision();
        var intent = new TableReadIntent(Customers, ["id"], null, null, 10, 0, "Rest");

        var planned = _planner.Plan(intent, meta, decision);
        planned.ShouldNotBeNull();
    }
}
