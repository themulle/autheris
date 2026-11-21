namespace Autheris.Tests.Unit.Federation;

using System.Collections.Generic;
using System.Linq;
using Autheris.Application.Sql.Federation.Subtrees;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Unit.TestDoubles;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Builder;
using TrinoSqlEngine.Ast.Nodes;
using Xunit;

/// <summary>GAP-013 / SEC-PG-03: federated WebSQL subtrees translate the date part functions like single-source WebSQL.</summary>
[Trait("Category", "Security")]
public sealed class SubtreeDateTranslationTests
{
    private static readonly FastSqlEngine Engine = new();

    private static TableMetadata CreateTable(string table, IReadOnlyList<string> columns) => new()
    {
        Identifier = new TableIdentifier("tsql", "dbo", table),
        Table = new Table
        {
            TableName = table,
            SchemaName = "dbo",
            SourceType = DatabaseDialect.SqlServer.ToString(),
            DataSourceType = DataSourceType.Sql,
            SourceName = "tsql",
            IsActive = true
        },
        Columns = columns.Select(c => new TableColumn { ColumnName = c, DataType = c == "created" ? "timestamp" : "varchar" }).ToList()
    };

    private static SubtreeExecutionPlan Compile(string sql)
    {
        var metadata = Engine.Analyze(sql.AsMemory());
        var orders = CreateTable("orders", ["id", "tenant_id", "created"]);
        var items = CreateTable("order_items", ["id", "order_id", "tenant_id"]);
        var tables = new List<ResolvedSourceTable>
        {
            new(metadata.ReferencedTables[0], orders.Identifier, orders, "tsql"),
            new(metadata.ReferencedTables[1], items.Identifier, items, "tsql")
        };
        var (tree, _) = Engine.Parse(sql.AsMemory());
        var subtree = SubtreeGraphPartitioner.Partition(new SqlAstBuilder(new AstBuilderOptions
        {
            EnforceFunctionPolicy = true,
            AllowedFunctions = SqlFunctionAllowlists.WithDateTranslation(SqlFunctionAllowlists.DuckDb)
        }).BuildStatement(tree), tables, maxTablesPerSubtree: 12).Subtrees.Single();
        var decisions = tables.ToDictionary(t => t.Metadata.Identifier, t => TableAccessDecision.Allowed(
            t.Metadata.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true));
        return SubtreeCompiler.Compile(subtree, decisions, new CrossSourceOptions { Enabled = true }, Engine, PlannerTestIdentity.User, new TenantId("t-1"));
    }

    [Theory]
    [InlineData("year(o.created)", "DATEPART(year,")]
    [InlineData("date_part('month', o.created)", "DATEPART(month,")]
    [InlineData("EXTRACT(day FROM o.created)", "DATEPART(day,")]
    public void PartFunction_CompilesWithABoundFormat_AndNoStringLiteral(string expression, string expectedFragment)
    {
        var plan = Compile($"SELECT o.id FROM tsql.dbo.orders o JOIN tsql.dbo.order_items i ON o.id = i.order_id WHERE {expression} = 2024");

        plan.Compiled.Sql.ShouldNotContain("'");
        plan.Compiled.Sql.ShouldContain(expectedFragment);
    }

    [Fact]
    public void DateTrunc_CompilesWithABoundModifier()
    {
        var plan = Compile("SELECT o.id FROM tsql.dbo.orders o JOIN tsql.dbo.order_items i ON o.id = i.order_id WHERE date_trunc('year', o.created) = '2024-01-01'");

        plan.Compiled.Sql.ShouldNotContain("'");
        plan.Compiled.Sql.ShouldContain("DATEFROMPARTS(YEAR(");
    }
}
