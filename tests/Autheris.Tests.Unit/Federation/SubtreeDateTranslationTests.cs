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
        Identifier = new TableIdentifier("lite", "main", table),
        Table = new Table
        {
            TableName = table,
            SchemaName = "main",
            SourceType = DatabaseDialect.Sqlite.ToString(),
            DataSourceType = DataSourceType.Sql,
            SourceName = "lite",
            IsActive = true
        },
        Columns = columns.Select(c => new TableColumn { ColumnName = c, DataType = "varchar" }).ToList()
    };

    private static SubtreeExecutionPlan Compile(string sql)
    {
        var metadata = Engine.Analyze(sql.AsMemory());
        var orders = CreateTable("orders", ["id", "tenant_id", "created"]);
        var items = CreateTable("order_items", ["id", "order_id", "tenant_id"]);
        var tables = new List<ResolvedSourceTable>
        {
            new(metadata.ReferencedTables[0], orders.Identifier, orders, "lite"),
            new(metadata.ReferencedTables[1], items.Identifier, items, "lite")
        };
        var (tree, _) = Engine.Parse(sql.AsMemory());
        var subtree = SubtreeGraphPartitioner.Partition(new SqlAstBuilder().BuildStatement(tree), tables, maxTablesPerSubtree: 12).Subtrees.Single();
        var decisions = tables.ToDictionary(t => t.Metadata.Identifier, t => TableAccessDecision.Allowed(
            t.Metadata.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true));
        return SubtreeCompiler.Compile(subtree, decisions, new CrossSourceOptions { Enabled = true }, Engine, PlannerTestIdentity.User, new TenantId("t-1"));
    }

    [Theory]
    [InlineData("year(o.created)", "%Y")]
    [InlineData("date_part('month', o.created)", "%m")]
    [InlineData("EXTRACT(day FROM o.created)", "%d")]
    public void PartFunction_CompilesWithABoundFormat_AndNoStringLiteral(string expression, string format)
    {
        var plan = Compile($"SELECT o.id FROM lite.main.orders o JOIN lite.main.order_items i ON o.id = i.order_id WHERE {expression} = 2024");

        plan.Compiled.Sql.ShouldNotContain("'");
        plan.Compiled.Parameters.Select(p => p.Value as string).ShouldContain(format, plan.Compiled.Sql + " | " + string.Join(",", plan.Compiled.Parameters.Select(p => p.Value)));
    }

    [Fact]
    public void DateTrunc_CompilesWithABoundModifier()
    {
        var plan = Compile("SELECT o.id FROM lite.main.orders o JOIN lite.main.order_items i ON o.id = i.order_id WHERE date_trunc('year', o.created) = '2024-01-01'");

        plan.Compiled.Sql.ShouldNotContain("'");
        plan.Compiled.Parameters.Select(p => p.Value as string).ShouldContain("start of year");
    }
}
