namespace Autheris.Tests.Unit.Federation;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Application.Interfaces;
using Autheris.Application.Sql.Federation.Conjuncts;
using Autheris.Application.Sql.Federation.KeySet;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Analysis;
using TrinoSqlEngine.Ast.Builder;
using TrinoSqlEngine.Ast.Nodes;
using Xunit;

public sealed class WebSqlDynamicKeySetPlannerTests
{
    private static readonly FastSqlEngine Engine = new();

    private static TableMetadata CreateTable(
        string domain,
        string table,
        DataSourceType dsType,
        IReadOnlyList<string> columns,
        DatabaseDialect dialect = DatabaseDialect.PostgreSql,
        long? expectedRows = null)
    {
        return new TableMetadata
        {
            Identifier = new TableIdentifier(domain, "public", table),
            Table = new Table
            {
                TableName = table,
                SchemaName = "public",
                SourceType = dsType == DataSourceType.Sql ? dialect.ToString() : "Http",
                DataSourceType = dsType,
                SourceName = domain,
                IsActive = true,
                ExpectedRows = expectedRows
            },
            ExpectedRows = expectedRows,
            Columns = columns.Select(c => new TableColumn
            {
                ColumnName = c,
                DataType = "varchar"
            }).ToList()
        };
    }

    private static SqlStatement Parse(string sql)
    {
        var (tree, _) = Engine.Parse(sql.AsMemory());
        return new SqlAstBuilder().BuildStatement(tree);
    }

    private static TableAccessDecision AllowAll(TableMetadata meta) =>
        TableAccessDecision.Allowed(meta.Identifier, meta.Columns.ToDictionary(c => c.ColumnName, _ => ColumnAccessLevel.Clear), hasUnconstrainedColumnAllow: true);

    private static TableAccessTarget Target(string catalog, string schema, string table, string alias) =>
        new(catalog, schema, table, alias, $"{catalog}.{schema}.{table}");

    [Fact]
    public void Plan_SelectiveFilterOnOneSide_DesignatesFilteredSideAsDriving()
    {
        var customers = CreateTable("crm", "customers", DataSourceType.Sql, ["id", "status"]);
        var orders = CreateTable("erp", "orders", DataSourceType.Sql, ["id", "customer_id", "total"]);

        var sql = "SELECT * FROM crm.public.customers c JOIN erp.public.orders o ON c.id = o.customer_id WHERE c.status = 'ACTIVE'";
        var stmt = Parse(sql);

        var resolvedCustomers = new ResolvedSourceTable(Target("crm", "public", "customers", "c"), customers.Identifier, customers, "crm");
        var resolvedOrders = new ResolvedSourceTable(Target("erp", "public", "orders", "o"), orders.Identifier, orders, "erp");

        var tables = new[] { resolvedCustomers, resolvedOrders };
        var decisions = tables.ToDictionary(t => t.ResolvedIdentifier, t => AllowAll(t.Metadata));
        var options = new CrossSourceOptions { DynamicKeySet = new DynamicKeySetOptions() };

        var plans = DynamicKeySetPlanner.Plan(stmt, tables, decisions, options);

        plans.Count.ShouldBe(1);
        var plan = plans[0];
        plan.DrivingTable.ResolvedIdentifier.ShouldBe(customers.Identifier);
        plan.DrivenTable.ResolvedIdentifier.ShouldBe(orders.Identifier);
        plan.DrivingKeyColumn.ShouldBe("id");
        plan.DrivenKeyColumn.ShouldBe("customer_id");
    }

    [Fact]
    public void Plan_ExpectedRowsDifferential_DesignatesSmallerSideAsDriving()
    {
        // Neither side has pushed filters, but customers has 50 rows, orders has 1000 rows (>= 10x differential)
        var customers = CreateTable("crm", "customers", DataSourceType.Sql, ["id", "name"], expectedRows: 50);
        var orders = CreateTable("erp", "orders", DataSourceType.Sql, ["id", "customer_id", "amount"], expectedRows: 1000);

        var sql = "SELECT * FROM crm.public.customers c JOIN erp.public.orders o ON c.id = o.customer_id";
        var stmt = Parse(sql);

        var resolvedCustomers = new ResolvedSourceTable(Target("crm", "public", "customers", "c"), customers.Identifier, customers, "crm");
        var resolvedOrders = new ResolvedSourceTable(Target("erp", "public", "orders", "o"), orders.Identifier, orders, "erp");

        var tables = new[] { resolvedCustomers, resolvedOrders };
        var decisions = tables.ToDictionary(t => t.ResolvedIdentifier, t => AllowAll(t.Metadata));
        var options = new CrossSourceOptions { DynamicKeySet = new DynamicKeySetOptions() };

        var plans = DynamicKeySetPlanner.Plan(stmt, tables, decisions, options);

        plans.Count.ShouldBe(1);
        plans[0].DrivingTable.ResolvedIdentifier.ShouldBe(customers.Identifier);
        plans[0].DrivenTable.ResolvedIdentifier.ShouldBe(orders.Identifier);
        plans[0].DrivingKeyColumn.ShouldBe("id");
        plans[0].DrivenKeyColumn.ShouldBe("customer_id");
    }

    [Fact]
    public void Plan_ComparableExpectedRows_SkipsKeySetPushdown()
    {
        // 500 rows vs 600 rows (< 10x differential)
        var customers = CreateTable("crm", "customers", DataSourceType.Sql, ["id", "name"], expectedRows: 500);
        var orders = CreateTable("erp", "orders", DataSourceType.Sql, ["id", "customer_id", "amount"], expectedRows: 600);

        var sql = "SELECT * FROM crm.public.customers c JOIN erp.public.orders o ON c.id = o.customer_id";
        var stmt = Parse(sql);

        var resolvedCustomers = new ResolvedSourceTable(Target("crm", "public", "customers", "c"), customers.Identifier, customers, "crm");
        var resolvedOrders = new ResolvedSourceTable(Target("erp", "public", "orders", "o"), orders.Identifier, orders, "erp");

        var tables = new[] { resolvedCustomers, resolvedOrders };
        var decisions = tables.ToDictionary(t => t.ResolvedIdentifier, t => AllowAll(t.Metadata));
        var options = new CrossSourceOptions { DynamicKeySet = new DynamicKeySetOptions() };

        var plans = DynamicKeySetPlanner.Plan(stmt, tables, decisions, options);

        plans.ShouldBeEmpty();
    }

    [Fact]
    public void Plan_NonEquiJoin_SkipsKeySetPushdown()
    {
        var t1 = CreateTable("crm", "ranges_a", DataSourceType.Sql, ["id", "val"], expectedRows: 10);
        var t2 = CreateTable("erp", "ranges_b", DataSourceType.Sql, ["id", "val"], expectedRows: 1000);

        var sql = "SELECT * FROM crm.public.ranges_a a JOIN erp.public.ranges_b b ON a.val < b.val";
        var stmt = Parse(sql);

        var resolvedA = new ResolvedSourceTable(Target("crm", "public", "ranges_a", "a"), t1.Identifier, t1, "crm");
        var resolvedB = new ResolvedSourceTable(Target("erp", "public", "ranges_b", "b"), t2.Identifier, t2, "erp");

        var tables = new[] { resolvedA, resolvedB };
        var decisions = tables.ToDictionary(t => t.ResolvedIdentifier, t => AllowAll(t.Metadata));
        var options = new CrossSourceOptions { DynamicKeySet = new DynamicKeySetOptions() };

        var plans = DynamicKeySetPlanner.Plan(stmt, tables, decisions, options);

        plans.ShouldBeEmpty();
    }

    [Fact]
    public void Plan_SameDataSource_SkipsKeySetPushdown()
    {
        // Both tables belong to crm
        var customers = CreateTable("crm", "customers", DataSourceType.Sql, ["id", "status"], expectedRows: 10);
        var orders = CreateTable("crm", "orders", DataSourceType.Sql, ["id", "customer_id"], expectedRows: 1000);

        var sql = "SELECT * FROM crm.public.customers c JOIN crm.public.orders o ON c.id = o.customer_id WHERE c.status = 'A'";
        var stmt = Parse(sql);

        var resolvedCustomers = new ResolvedSourceTable(Target("crm", "public", "customers", "c"), customers.Identifier, customers, "crm");
        var resolvedOrders = new ResolvedSourceTable(Target("crm", "public", "orders", "o"), orders.Identifier, orders, "crm");

        var tables = new[] { resolvedCustomers, resolvedOrders };
        var decisions = tables.ToDictionary(t => t.ResolvedIdentifier, t => AllowAll(t.Metadata));
        var options = new CrossSourceOptions { DynamicKeySet = new DynamicKeySetOptions() };

        var plans = DynamicKeySetPlanner.Plan(stmt, tables, decisions, options);

        plans.ShouldBeEmpty();
    }

    [Fact]
    public void Plan_DisabledDynamicKeySet_ReturnsEmpty()
    {
        var customers = CreateTable("crm", "customers", DataSourceType.Sql, ["id", "status"], expectedRows: 10);
        var orders = CreateTable("erp", "orders", DataSourceType.Sql, ["id", "customer_id"], expectedRows: 1000);

        var sql = "SELECT * FROM crm.public.customers c JOIN erp.public.orders o ON c.id = o.customer_id WHERE c.status = 'A'";
        var stmt = Parse(sql);

        var resolvedCustomers = new ResolvedSourceTable(Target("crm", "public", "customers", "c"), customers.Identifier, customers, "crm");
        var resolvedOrders = new ResolvedSourceTable(Target("erp", "public", "orders", "o"), orders.Identifier, orders, "erp");

        var tables = new[] { resolvedCustomers, resolvedOrders };
        var decisions = tables.ToDictionary(t => t.ResolvedIdentifier, t => AllowAll(t.Metadata));
        var options = new CrossSourceOptions { DynamicKeySet = new DynamicKeySetOptions { Enabled = false } };

        var plans = DynamicKeySetPlanner.Plan(stmt, tables, decisions, options);

        plans.ShouldBeEmpty();
    }

    [Fact]
    public void KeyBatcher_ExtractDistinctKeys_FiltersNullsAndPreservesOrdinalUniqueness()
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["key"] = "ABC" },
            new Dictionary<string, object?> { ["key"] = null },
            new Dictionary<string, object?> { ["key"] = DBNull.Value },
            new Dictionary<string, object?> { ["key"] = "abc" }, // distinct under ordinal comparison
            new Dictionary<string, object?> { ["key"] = "ABC" }, // duplicate
            new Dictionary<string, object?> { ["other"] = "XYZ" }, // key missing
            new Dictionary<string, object?> { ["key"] = "DEF" }
        };

        var distinct = KeyBatcher.ExtractDistinctKeys(rows, "key");

        distinct.Count.ShouldBe(3);
        distinct.ShouldBe(["ABC", "abc", "DEF"]);
    }

    [Fact]
    public void KeyBatcher_ResolveBatchSize_ClampsToDialectLimits()
    {
        // SQL Server clamped to 2090
        KeyBatcher.ResolveBatchSize(DatabaseDialect.SqlServer, 3000).ShouldBe(2090);
        KeyBatcher.ResolveBatchSize(DatabaseDialect.SqlServer, 500).ShouldBe(500);

        // SQLite clamped to 999
        KeyBatcher.ResolveBatchSize(DatabaseDialect.Sqlite, 1500).ShouldBe(999);

        // Oracle clamped to 1000
        KeyBatcher.ResolveBatchSize(DatabaseDialect.Oracle, 1500).ShouldBe(1000);
    }

    [Fact]
    public void KeyBatcher_CreateBatches_PartitionsCorrectly()
    {
        var keys = Enumerable.Range(1, 10).Cast<object>().ToList();
        var batches = KeyBatcher.CreateBatches(keys, 3);

        batches.Count.ShouldBe(4);
        batches[0].Count.ShouldBe(3);
        batches[1].Count.ShouldBe(3);
        batches[2].Count.ShouldBe(3);
        batches[3].Count.ShouldBe(1);
    }

    [Fact]
    public void KeyBatcher_IsWithinBounds_EnforcesLimits()
    {
        KeyBatcher.IsWithinBounds(2000, 10, 2000, 10).ShouldBeTrue();
        KeyBatcher.IsWithinBounds(2001, 10, 2000, 10).ShouldBeFalse();
        KeyBatcher.IsWithinBounds(2000, 11, 2000, 10).ShouldBeFalse();
    }

    [Fact]
    public void KeySetClauseBuilder_BuildsParameterizedInClause()
    {
        var keys = new object[] { "K1", "K2", "K3" };
        var clause = KeySetClauseBuilder.Build("user_id", keys);

        clause.ReferencedColumns.ShouldBe(["user_id"]);
        clause.Parameters.Count.ShouldBe(3);
        clause.Parameters["@k_0"].ShouldBe("K1");
        clause.Parameters["@k_1"].ShouldBe("K2");
        clause.Parameters["@k_2"].ShouldBe("K3");

        clause.GetTrinoPredicate().ShouldBe("\"user_id\" IN (@k_0, @k_1, @k_2)");
        clause.PushdownPredicates.ShouldNotBeNull();
        clause.PushdownPredicates.Count.ShouldBe(1);
        clause.PushdownPredicates[0].Column.ShouldBe("user_id");
        clause.PushdownPredicates[0].Operator.ShouldBe("In");
    }

    [Fact]
    public void KeySetClauseBuilder_CombinesWithExistingPushdownFilter()
    {
        var existing = new TableFilterClause(
            "\"status\" = @p_pd_0",
            new Dictionary<string, object?> { ["@p_pd_0"] = "ACTIVE" },
            ["status"])
        {
            TrinoSqlFactory = res => $"\"{res("status")}\" = @p_pd_0",
            PushdownPredicates = [new PushdownPredicateInfo("status", "Equal")]
        };

        var keys = new object[] { "101", "102" };
        var combined = KeySetClauseBuilder.Build("customer_id", keys, existing);

        combined.ReferencedColumns.OrderBy(c => c).ShouldBe(["customer_id", "status"]);
        combined.Parameters.Count.ShouldBe(3);
        combined.Parameters["@p_pd_0"].ShouldBe("ACTIVE");
        combined.Parameters["@k_0"].ShouldBe("101");
        combined.Parameters["@k_1"].ShouldBe("102");

        var trinoSql = combined.GetTrinoPredicate();
        trinoSql.ShouldContain("\"status\" = @p_pd_0");
        trinoSql.ShouldContain("\"customer_id\" IN (@k_0, @k_1)");
        combined.PushdownPredicates!.Count.ShouldBe(2);
    }
}
