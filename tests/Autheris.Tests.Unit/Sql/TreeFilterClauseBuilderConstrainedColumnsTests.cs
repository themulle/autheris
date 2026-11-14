namespace Autheris.Tests.Unit.Sql;

using System.Collections.Generic;
using Autheris.Application.Analytics;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;
using RequiredFilterColumnMissingException = TrinoSqlEngine.Ast.Emit.RequiredFilterColumnMissingException;

[Trait("Category", "Security")]
public sealed class TreeFilterClauseBuilderConstrainedColumnsTests
{
    private static readonly TableIdentifier Orders = new("sales", "dbo", "orders");

    private static TableMetadata Meta(params string[] required) => new()
    {
        Identifier = Orders,
        Table = new Table
        {
            SourceName = "sales",
            SchemaName = "dbo",
            TableName = "orders",
            RequiredFilterColumns = [.. required]
        },
        PrimaryKeyColumns = ["id"],
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "int" },
            new TableColumn { ColumnName = "region", DataType = "varchar" },
            new TableColumn { ColumnName = "amount", DataType = "int" }
        ]
    };

    private static ColumnRef? Resolve(string name) => name.ToLowerInvariant() switch
    {
        "id" => new ColumnRef("id", ColumnValueKind.Int),
        "region" => new ColumnRef("region", ColumnValueKind.String),
        "amount" => new ColumnRef("amount", ColumnValueKind.Int),
        _ => null
    };

    private static readonly TableAccessDecision ClearDecision = TableAccessDecision.Allowed(
        Orders, new Dictionary<string, ColumnAccessLevel>(), null, hasUnconstrainedColumnAllow: true);

    [Theory]
    [InlineData(TreeFilterOperator.Neq, "EU")]
    [InlineData(TreeFilterOperator.IsNull, true)]
    [InlineData(TreeFilterOperator.IsNull, false)]
    [InlineData(TreeFilterOperator.Contains, "%")]
    [InlineData(TreeFilterOperator.StartsWith, "E")]
    [InlineData(TreeFilterOperator.EndsWith, "U")]
    public void WeakOperator_ProducesEmptyConstrainedColumns_AndFailsTreeSqlCompiler(TreeFilterOperator op, object value)
    {
        var filter = new TreeComparison("region", op, value);
        var clause = TreeFilterClauseBuilder.Build(filter, Resolve);

        clause.ConstrainedColumns.ShouldNotBeNull();
        clause.ConstrainedColumns.ShouldBeEmpty();

        var access = new Dictionary<TableIdentifier, TreeTableAccess> { [Orders] = new(Meta("region"), ClearDecision) };
        Should.Throw<RequiredFilterColumnMissingException>(() =>
            TreeSqlCompiler.Compile(new TreeQueryNode(Orders, ["id"]) { Where = filter, Limit = 10 }, access, DatabaseDialect.PostgreSql));
    }

    [Fact]
    public void Not_NotIn_BindNullOperand_AndEmptyOrAllNullIn_DoNotConstrain()
    {
        TreeFilterClauseBuilder.Build(new TreeNotFilter(new TreeComparison("region", TreeFilterOperator.Eq, "EU")), Resolve)
            .ConstrainedColumns.ShouldBeEmpty();

        TreeFilterClauseBuilder.Build(new TreeComparison("region", TreeFilterOperator.NotIn, new List<object?> { "EU" }), Resolve)
            .ConstrainedColumns.ShouldBeEmpty();

        TreeFilterClauseBuilder.Build(new TreeComparison("region", TreeFilterOperator.Eq, null) { BindNullOperand = true }, Resolve)
            .ConstrainedColumns.ShouldBeEmpty();

        TreeFilterClauseBuilder.Build(new TreeComparison("region", TreeFilterOperator.In, new List<object?> { null }), Resolve)
            .ConstrainedColumns.ShouldBeEmpty();
    }

    [Fact]
    public void Eq_Range_NonEmptyIn_And_OrIntersection_ConstrainExpectedColumns()
    {
        TreeFilterClauseBuilder.Build(new TreeComparison("region", TreeFilterOperator.Eq, "EU"), Resolve)
            .ConstrainedColumns.ShouldBe(["region"]);

        TreeFilterClauseBuilder.Build(new TreeComparison("amount", TreeFilterOperator.Gte, 10), Resolve)
            .ConstrainedColumns.ShouldBe(["amount"]);

        TreeFilterClauseBuilder.Build(new TreeComparison("region", TreeFilterOperator.In, new List<object?> { "EU", "US" }), Resolve)
            .ConstrainedColumns.ShouldBe(["region"]);

        var andClause = TreeFilterClauseBuilder.Build(new TreeAndFilter([
            new TreeComparison("region", TreeFilterOperator.Eq, "EU"),
            new TreeComparison("amount", TreeFilterOperator.Neq, 0)
        ]), Resolve);
        andClause.ConstrainedColumns.ShouldBe(["region"]);

        var partialOrClause = TreeFilterClauseBuilder.Build(new TreeOrFilter([
            new TreeComparison("region", TreeFilterOperator.Eq, "EU"),
            new TreeComparison("amount", TreeFilterOperator.Gt, 5)
        ]), Resolve);
        partialOrClause.ConstrainedColumns.ShouldBeEmpty();

        var fullOrClause = TreeFilterClauseBuilder.Build(new TreeOrFilter([
            new TreeComparison("region", TreeFilterOperator.Eq, "EU"),
            new TreeComparison("region", TreeFilterOperator.Eq, "US")
        ]), Resolve);
        fullOrClause.ConstrainedColumns.ShouldBe(["region"]);
    }
}
