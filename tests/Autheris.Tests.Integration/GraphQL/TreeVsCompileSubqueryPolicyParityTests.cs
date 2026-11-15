namespace Autheris.Tests.Integration.GraphQL;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Application.Policy;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Tests.Common;
using Shouldly;
using Xunit;

/// <summary>
/// SQLA-06 follow-up: typed row policies with a subquery run in the tree through the secured source and expose
/// exactly the rows the governed WebSQL compile path exposes, on a real PostgreSQL database. Tenant t2 owns consent
/// rows that point at orders and items of tenant t1; neither path may let them admit a row.
/// </summary>
public sealed class TreeVsCompileSubqueryPolicyParityTests
{
    private static async Task<PostgresTreeHarness> CreateHarnessAsync(string connectionString, Func<TableIdentifier, TableMetadata?>? dependentMetadata = null)
    {
        await PostgresTreeHarness.SeedAsync(connectionString);
        return new PostgresTreeHarness(connectionString, dependentMetadata: dependentMetadata);
    }

    [RequiresContainerRuntimeTheory]
    [InlineData("t1", new long[] { 1 })]
    [InlineData("t2", new long[] { 4 })]
    public async Task SubqueryPolicy_Exists_TreeAndCompileExposeTheSameRows(string tenant, long[] expected)
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable) return;
        var h = await CreateHarnessAsync(db.ConnectionString);

        var tree = await h.TreeOrderIdsAsync(PostgresTreeHarness.ExistsPolicy(), tenant);
        var compiled = await h.CompileOrderIdsAsync(PostgresTreeHarness.ExistsPolicy(), tenant);

        tree.ShouldBe(expected);
        tree.ShouldBe(compiled);
    }

    [RequiresContainerRuntimeTheory]
    [InlineData("t1", new long[] { 1 })]
    [InlineData("t2", new long[] { 4 })]
    public async Task SubqueryPolicy_InSubquery_TreeAndCompileExposeTheSameRows(string tenant, long[] expected)
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable) return;
        var h = await CreateHarnessAsync(db.ConnectionString);

        var tree = await h.TreeOrderIdsAsync(PostgresTreeHarness.InPolicy(), tenant);
        var compiled = await h.CompileOrderIdsAsync(PostgresTreeHarness.InPolicy(), tenant);

        tree.ShouldBe(expected);
        tree.ShouldBe(compiled);
    }

    [Trait("Category", "Security")]
    [RequiresContainerRuntimeFact]
    public async Task SQLA06F_ForeignTenantConsentRow_NeverAdmitsRow()
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable) return;
        var h = await CreateHarnessAsync(db.ConnectionString);

        var t1 = await h.TreeOrderIdsAsync(PostgresTreeHarness.ExistsPolicy(), "t1");
        var t2 = await h.TreeOrderIdsAsync(PostgresTreeHarness.ExistsPolicy(), "t2");

        t1.ShouldNotContain(2L);
        t2.ShouldNotContain(2L);
        t1.ShouldBe([1L]);
        t2.ShouldBe([4L]);

        (await h.TreeOrderIdsAsync(PostgresTreeHarness.InPolicy(), "t1")).ShouldNotContain(2L);
    }

    [Trait("Category", "Security")]
    [RequiresContainerRuntimeTheory]
    [InlineData("t1", "1:[1];2:[3];3:[]")]
    [InlineData("t2", "4:[4];5:[]")]
    public async Task SQLA06F_ForeignTenantConsentRow_OnChildRelation_NeverAdmitsRow(string tenant, string expected)
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable) return;
        var h = await CreateHarnessAsync(db.ConnectionString);

        var json = await RunOrdersWithItemsAsync(h, tenant, rootPolicy: null);

        json.ShouldBe(expected);
    }

    [Trait("Category", "Security")]
    [RequiresContainerRuntimeFact]
    public async Task SubqueryPolicy_OnChildRelation_AndOnRoot_TwoSecuredSourcesDoNotCollide()
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable) return;
        var h = await CreateHarnessAsync(db.ConnectionString);

        var json = await RunOrdersWithItemsAsync(h, "t1", rootPolicy: PostgresTreeHarness.ExistsPolicy());

        json.ShouldBe("1:[1]");
    }

    private static async Task<string> RunOrdersWithItemsAsync(PostgresTreeHarness h, string tenant, IRowPolicy? rootPolicy)
    {
        var child = new TreeQueryNode(PostgresTreeHarness.ItemsId, ["id"]) { OrderBy = [new TreeOrder("id")], Limit = 10 };
        var root = new TreeQueryNode(PostgresTreeHarness.OrdersId, ["id"])
        {
            OrderBy = [new TreeOrder("id")],
            Limit = 100,
            Relations = [new TreeRelationNode("items", ["id"], ["order_id"], true, child)]
        };
        var access = new Dictionary<TableIdentifier, TreeTableAccess>
        {
            [PostgresTreeHarness.OrdersId] = await h.TreeAccessAsync(PostgresTreeHarness.OrdersId, rootPolicy, tenant),
            [PostgresTreeHarness.ItemsId] = await h.TreeAccessAsync(PostgresTreeHarness.ItemsId, PostgresTreeHarness.ItemPolicy(), tenant)
        };

        using var doc = JsonDocument.Parse(await h.RunTreeAsync(root, access));
        return string.Join(
            ';',
            doc.RootElement.EnumerateArray().Select(o =>
                $"{o.GetProperty("id").GetInt64()}:[{string.Join(',', o.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetInt64()))}]"));
    }

    [RequiresContainerRuntimeFact]
    public async Task SubqueryPolicy_OnChildRelation_TreeAndCompileExposeTheSameRows()
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable) return;
        var h = await CreateHarnessAsync(db.ConnectionString);

        var json = await RunOrdersWithItemsAsync(h, "t1", rootPolicy: null);
        json.ShouldBe("1:[1];2:[3];3:[]");

        var items = await h.CompileItemIdsAsync(PostgresTreeHarness.ItemPolicy(), "t1");
        items.ShouldBe([1L, 3L]);
    }

    [Trait("Category", "Security")]
    [RequiresContainerRuntimeFact]
    public async Task SubqueryPolicy_WithMask_TreeAndCompileExposeTheSameRows()
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable) return;
        var h = await CreateHarnessAsync(db.ConnectionString);

        var access = new Dictionary<TableIdentifier, TreeTableAccess>
        {
            [PostgresTreeHarness.OrdersId] = await h.TreeAccessAsync(PostgresTreeHarness.OrdersId, PostgresTreeHarness.ExistsPolicy(), "t1", maskEmail: true)
        };
        var root = new TreeQueryNode(PostgresTreeHarness.OrdersId, ["id", "email"]) { OrderBy = [new TreeOrder("id")], Limit = 100 };

        using var doc = JsonDocument.Parse(await h.RunTreeAsync(root, access));

        var rows = doc.RootElement.EnumerateArray().ToList();
        rows.Select(r => r.GetProperty("id").GetInt64()).ShouldBe(await h.CompileOrderIdsAsync(PostgresTreeHarness.ExistsPolicy(), "t1"));
        rows.ShouldAllBe(r => r.GetProperty("email").GetString() == "***");
        doc.RootElement.GetRawText().ShouldNotContain("x.example");
    }

    [Trait("Category", "Security")]
    [RequiresContainerRuntimeFact]
    public async Task SQLA06F_MaskedColumn_NotLeakedViaSubquerySource()
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable) return;
        var h = await CreateHarnessAsync(db.ConnectionString);

        var access = await h.TreeAccessAsync(PostgresTreeHarness.OrdersId, PostgresTreeHarness.ExistsPolicy(), "t1", maskEmail: true);
        var map = new Dictionary<TableIdentifier, TreeTableAccess> { [PostgresTreeHarness.OrdersId] = access };
        var root = new TreeQueryNode(PostgresTreeHarness.OrdersId, ["id", "email"]) { Limit = 100 };

        var compiled = TreeSqlCompiler.Compile(root, map, DatabaseDialect.PostgreSql);
        compiled.Sql.ShouldNotContain("autheris_target.*");
        compiled.Sql.ShouldNotContain("\"autheris_target\".*");

        using var doc = JsonDocument.Parse(await h.RunTreeAsync(root, map));
        doc.RootElement.GetRawText().ShouldNotContain("a@x.example");

        var filtered = root with { Where = new TreeComparison("email", TreeFilterOperator.Eq, "a@x.example") };
        await Should.ThrowAsync<Exception>(() => h.RunTreeAsync(filtered, map));
    }

    [Trait("Category", "Security")]
    [RequiresContainerRuntimeFact]
    public async Task Issue143RestrictedScope_WithSubqueryPolicy_MatchesWebSql()
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable) return;
        var h = await CreateHarnessAsync(db.ConnectionString);

        var access = await h.TreeAccessAsync(
            PostgresTreeHarness.OrdersId,
            PostgresTreeHarness.ExistsPolicy(),
            "t1",
            maskEmail: true,
            extraAccess: new Dictionary<string, ColumnAccessLevel> { ["amount"] = ColumnAccessLevel.Deny });
        var map = new Dictionary<TableIdentifier, TreeTableAccess> { [PostgresTreeHarness.OrdersId] = access };
        var root = new TreeQueryNode(PostgresTreeHarness.OrdersId, ["id", "email"]) { OrderBy = [new TreeOrder("id")], Limit = 100 };

        using var doc = JsonDocument.Parse(await h.RunTreeAsync(root, map));

        doc.RootElement.EnumerateArray().Select(r => r.GetProperty("id").GetInt64())
            .ShouldBe(await h.CompileOrderIdsAsync(PostgresTreeHarness.ExistsPolicy(), "t1"));
        doc.RootElement.GetRawText().ShouldNotContain("@x.example");
        Should.Throw<Exception>(() => TreeSqlCompiler.Compile(
            new TreeQueryNode(PostgresTreeHarness.OrdersId, ["id", "amount"]) { Limit = 10 }, map, DatabaseDialect.PostgreSql));
    }

    [Trait("Category", "Security")]
    [RequiresContainerRuntimeFact]
    public async Task SQLA06F_KeySetStageOff_TreeRowsEqualExistsPath()
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable) return;
        var h = await CreateHarnessAsync(db.ConnectionString);

        foreach (var tenant in new[] { "t1", "t2" })
        {
            (await h.TreeOrderIdsAsync(PostgresTreeHarness.ExistsPolicy(), tenant))
                .ShouldBe(await h.CompileOrderIdsAsync(PostgresTreeHarness.ExistsPolicy(), tenant));
        }
    }

    [Trait("Category", "Security")]
    [RequiresContainerRuntimeFact]
    public async Task SubqueryPolicy_InactiveDependent_YieldsNoRowsOnBothPaths()
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable) return;
        var h = await CreateHarnessAsync(db.ConnectionString, id => PostgresTreeHarness.DependentMetadata(id, active: false));

        await Should.ThrowAsync<GatewaySecurityException>(() => h.TreeOrderIdsAsync(PostgresTreeHarness.ExistsPolicy(), "t1"));
        await Should.ThrowAsync<Exception>(() => h.CompileOrderIdsAsync(PostgresTreeHarness.ExistsPolicy(), "t1"));
    }
}
