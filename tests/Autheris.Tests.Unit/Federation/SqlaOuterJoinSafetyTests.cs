namespace Autheris.Tests.Unit.Federation;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Application.Sql.Federation.Conjuncts;
using Autheris.Application.Sql.Federation.KeySet;
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

/// <summary>SQLA-11 (key-set), SQLA-16 (subtree WHERE) and SQLA-14 (subtree tenant): outer-join sides and the caller's tenant.</summary>
[Trait("Category", "Security")]
public sealed class SqlaOuterJoinSafetyTests
{
    private static readonly FastSqlEngine Engine = new();

    private static TableMetadata CreateTable(string domain, string table, IReadOnlyList<string> columns) => new()
    {
        Identifier = new TableIdentifier(domain, "public", table),
        Table = new Table
        {
            TableName = table,
            SchemaName = "public",
            SourceType = DatabaseDialect.PostgreSql.ToString(),
            DataSourceType = DataSourceType.Sql,
            SourceName = domain,
            IsActive = true
        },
        Columns = columns.Select(c => new TableColumn { ColumnName = c, DataType = "varchar" }).ToList()
    };

    private static SqlStatement Parse(string sql)
    {
        var (tree, _) = Engine.Parse(sql.AsMemory());
        return new SqlAstBuilder().BuildStatement(tree);
    }

    private static IReadOnlyList<KeySetJoinPlan> KeySet(string sql)
    {
        var customers = CreateTable("crm", "customers", ["id", "status"]);
        var orders = CreateTable("erp", "orders", ["id", "customer_id", "total"]);
        var stmt = Parse(sql);
        var resolvedCustomers = new ResolvedSourceTable(new TableAccessTarget("crm", "public", "customers", "c", "crm.public.customers"), customers.Identifier, customers, "crm");
        var resolvedOrders = new ResolvedSourceTable(new TableAccessTarget("erp", "public", "orders", "o", "erp.public.orders"), orders.Identifier, orders, "erp");
        var tables = new[] { resolvedCustomers, resolvedOrders };
        var decisions = tables.ToDictionary(t => t.ResolvedIdentifier, t => TableAccessDecision.Allowed(
            t.Metadata.Identifier, t.Metadata.Columns.ToDictionary(c => c.ColumnName, _ => ColumnAccessLevel.Clear), hasUnconstrainedColumnAllow: true));
        return DynamicKeySetPlanner.Plan(stmt, tables, decisions, new CrossSourceOptions { DynamicKeySet = new DynamicKeySetOptions() });
    }

    // ---- SQLA-11 ----

    [Fact]
    public void KeySet_InnerJoin_Unchanged() =>
        KeySet("SELECT * FROM crm.public.customers c JOIN erp.public.orders o ON c.id = o.customer_id WHERE c.status = 'ACTIVE'").Count.ShouldBe(1);

    [Fact]
    public void KeySet_OuterJoinOnConjunct_NotCandidate() =>
        KeySet("SELECT * FROM crm.public.customers c LEFT JOIN erp.public.orders o ON c.id = o.customer_id WHERE c.status = 'ACTIVE'").ShouldBeEmpty();

    [Fact]
    public void KeySet_LeftJoinPreservedSide_NeverDriven() =>
        KeySet("SELECT * FROM crm.public.customers c LEFT JOIN erp.public.orders o ON c.id = o.customer_id WHERE o.customer_id = c.id AND o.total > 5").ShouldBeEmpty();

    [Fact]
    public void KeySet_FullOuterJoin_NoPlan() =>
        KeySet("SELECT * FROM crm.public.customers c FULL JOIN erp.public.orders o ON c.id = o.customer_id WHERE c.status = 'ACTIVE'").ShouldBeEmpty();

    // ---- SQLA-16 ----

    private static JoinedTableSource Cluster() =>
        new(new NamedTableSource(new SqlQualifiedName("o"), null), JoinType.Inner, new NamedTableSource(new SqlQualifiedName("i"), null), null);

    private static NamedTableSource Outside() => new(new SqlQualifiedName("s"), null);

    [Fact]
    public void Subtree_RightJoinNullSupplyingCluster_WhereNotPushed()
    {
        var cluster = Cluster();
        var from = new JoinedTableSource(cluster, JoinType.RightOuter, Outside(), null);   // the cluster is the null-supplying (left) side

        OuterJoinSides.IsNullSuppliedFromOutside(from, cluster).ShouldBeTrue();
    }

    [Fact]
    public void Subtree_LeftJoinPreservedCluster_WherePushed()
    {
        var cluster = Cluster();
        var from = new JoinedTableSource(cluster, JoinType.LeftOuter, Outside(), null);

        OuterJoinSides.IsNullSuppliedFromOutside(from, cluster).ShouldBeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Subtree_FullJoin_WhereNotPushed(bool clusterOnLeft)
    {
        var cluster = Cluster();
        var from = clusterOnLeft
            ? new JoinedTableSource(cluster, JoinType.FullOuter, Outside(), null)
            : new JoinedTableSource(Outside(), JoinType.FullOuter, cluster, null);

        OuterJoinSides.IsNullSuppliedFromOutside(from, cluster).ShouldBeTrue();
    }

    [Fact]
    public void Subtree_ClusterNotInFromClause_FailsClosed() =>
        OuterJoinSides.IsNullSuppliedFromOutside(Outside(), Cluster()).ShouldBeTrue();

    [Fact]
    public void Subtree_InnerOnly_Unchanged()
    {
        var sql = "SELECT o.id FROM pg.public.orders o JOIN pg.public.order_items oi ON o.id = oi.order_id WHERE o.status = 'x'";
        var metadata = Engine.Analyze(sql.AsMemory());
        var orderMeta = CreateTable("pg", "orders", ["id", "status"]);
        var itemMeta = CreateTable("pg", "order_items", ["id", "order_id"]);
        var tables = new List<ResolvedSourceTable>
        {
            new(metadata.ReferencedTables[0], orderMeta.Identifier, orderMeta, "pg"),
            new(metadata.ReferencedTables[1], itemMeta.Identifier, itemMeta, "pg")
        };

        var result = SubtreeGraphPartitioner.Partition(Parse(sql), tables, maxTablesPerSubtree: 12);

        result.Subtrees.Single().PushablePredicates.Count.ShouldBe(1);
    }

    // ---- SQLA-14 ----

    [Fact]
    public void Subtree_EmptyTenant_Throws()
    {
        var sql = "SELECT o.id FROM pg.public.orders o JOIN pg.public.order_items oi ON o.id = oi.order_id";
        var metadata = Engine.Analyze(sql.AsMemory());
        var orderMeta = CreateTable("pg", "orders", ["id"]);
        var itemMeta = CreateTable("pg", "order_items", ["id", "order_id"]);
        var tables = new List<ResolvedSourceTable>
        {
            new(metadata.ReferencedTables[0], orderMeta.Identifier, orderMeta, "pg"),
            new(metadata.ReferencedTables[1], itemMeta.Identifier, itemMeta, "pg")
        };
        var subtree = SubtreeGraphPartitioner.Partition(Parse(sql), tables, maxTablesPerSubtree: 12).Subtrees.Single();
        var decisions = tables.ToDictionary(t => t.Metadata.Identifier, t => TableAccessDecision.Allowed(
            t.Metadata.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true));

        Should.Throw<ArgumentException>(() => SubtreeCompiler.Compile(
            subtree, decisions, new CrossSourceOptions { Enabled = true }, Engine, PlannerTestIdentity.User, default));
    }

    [Fact]
    public void CoLocatedCompileOverload_DoesNotExist()
    {
        var overloads = typeof(SubtreeCompiler).GetMethods().Where(m => m.Name == "Compile" && m.GetParameters()[0].ParameterType == typeof(string));

        overloads.ShouldBeEmpty();
    }
}
