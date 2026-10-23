using System.Text.Json;
using Autheris.Application.Policy;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Shouldly;
using TrinoSqlEngine.Ast.Nodes;
using Xunit;

namespace Autheris.Tests.Unit.GraphQL;

/// <summary>
/// SQLA-06 follow-up: typed row policies with a subquery (correlated consent filters) run in the tree through the secured source and expose
/// exactly the rows the governed WebSQL compile path exposes, on a real SQLite database. Tenant t2 owns consent rows that point at orders and
/// items of tenant t1; neither path may let them admit a row.
/// </summary>
public sealed class TreeVsCompileSubqueryPolicyParityTests : IDisposable
{
    private readonly SecuredTreeHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Theory]
    [InlineData("t1", new long[] { 1 })]
    [InlineData("t2", new long[] { 4 })]
    public async Task SubqueryPolicy_Exists_TreeAndCompileExposeTheSameRows(string tenant, long[] expected)
    {
        var tree = await _h.TreeOrderIdsAsync(SecuredTreeHarness.ExistsPolicy(), tenant);
        var compiled = await _h.CompileOrderIdsAsync(SecuredTreeHarness.ExistsPolicy(), tenant);

        tree.ShouldBe(expected);
        tree.ShouldBe(compiled);
    }

    [Theory]
    [InlineData("t1", new long[] { 1 })]
    [InlineData("t2", new long[] { 4 })]
    public async Task SubqueryPolicy_InSubquery_TreeAndCompileExposeTheSameRows(string tenant, long[] expected)
    {
        var tree = await _h.TreeOrderIdsAsync(SecuredTreeHarness.InPolicy(), tenant);
        var compiled = await _h.CompileOrderIdsAsync(SecuredTreeHarness.InPolicy(), tenant);

        tree.ShouldBe(expected);
        tree.ShouldBe(compiled);
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task SQLA06F_ForeignTenantConsentRow_NeverAdmitsRow()
    {
        // SQLA06F-C1/C3: consent 4 (tenant t2) points at order 2 (tenant t1). The tenant wrapper inside the subquery drops it for t1,
        // and the tenant conjunct of the order table drops order 2 for t2.
        var t1 = await _h.TreeOrderIdsAsync(SecuredTreeHarness.ExistsPolicy(), "t1");
        var t2 = await _h.TreeOrderIdsAsync(SecuredTreeHarness.ExistsPolicy(), "t2");

        t1.ShouldNotContain(2L);
        t2.ShouldNotContain(2L);
        t1.ShouldBe(new long[] { 1 });
        t2.ShouldBe(new long[] { 4 });

        (await _h.TreeOrderIdsAsync(SecuredTreeHarness.InPolicy(), "t1")).ShouldNotContain(2L);
    }

    [Trait("Category", "Security")]
    [Theory]
    [InlineData("t1", "1:[1];2:[3];3:[]")]
    [InlineData("t2", "4:[4];5:[]")]
    public async Task SQLA06F_ForeignTenantConsentRow_OnChildRelation_NeverAdmitsRow(string tenant, string expected)
    {
        // SQLA06F-C9: the child's secured source is built from the child's own decision; item_consents row 4 (t2) points at item 2 (t1).
        var json = await RunOrdersWithItemsAsync(tenant, rootPolicy: null);

        json.ShouldBe(expected);
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task SubqueryPolicy_OnChildRelation_AndOnRoot_TwoSecuredSourcesDoNotCollide()
    {
        var json = await RunOrdersWithItemsAsync("t1", rootPolicy: SecuredTreeHarness.ExistsPolicy());

        json.ShouldBe("1:[1]");
    }

    private async Task<string> RunOrdersWithItemsAsync(string tenant, IRowPolicy? rootPolicy)
    {
        var child = new TreeQueryNode(SecuredTreeHarness.ItemsId, ["id"]) { OrderBy = [new TreeOrder("id")], Limit = 10 };
        var root = new TreeQueryNode(SecuredTreeHarness.OrdersId, ["id"])
        {
            OrderBy = [new TreeOrder("id")],
            Limit = 100,
            Relations = [new TreeRelationNode("items", ["id"], ["order_id"], true, child)]
        };
        var access = new Dictionary<TableIdentifier, TreeTableAccess>
        {
            [SecuredTreeHarness.OrdersId] = await _h.TreeAccessAsync(SecuredTreeHarness.OrdersId, rootPolicy, tenant),
            [SecuredTreeHarness.ItemsId] = await _h.TreeAccessAsync(SecuredTreeHarness.ItemsId, SecuredTreeHarness.ItemPolicy(), tenant)
        };

        using var doc = JsonDocument.Parse(_h.RunTree(root, access));
        return string.Join(
            ';',
            doc.RootElement.EnumerateArray().Select(o =>
                $"{o.GetProperty("id").GetInt64()}:[{string.Join(',', o.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetInt64()))}]"));
    }

    [Fact]
    public async Task SubqueryPolicy_OnChildRelation_TreeAndCompileExposeTheSameRows()
    {
        // The child table is read through the tree and through the WebSQL path with the same policy.
        var json = await RunOrdersWithItemsAsync("t1", rootPolicy: null);
        json.ShouldBe("1:[1];2:[3];3:[]");

        var items = await _h.CompileItemIdsAsync(SecuredTreeHarness.ItemPolicy(), "t1");
        items.ShouldBe(new long[] { 1, 3 });
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task SubqueryPolicy_WithMask_TreeAndCompileExposeTheSameRows()
    {
        var access = new Dictionary<TableIdentifier, TreeTableAccess>
        {
            [SecuredTreeHarness.OrdersId] = await _h.TreeAccessAsync(SecuredTreeHarness.OrdersId, SecuredTreeHarness.ExistsPolicy(), "t1", maskEmail: true)
        };
        var root = new TreeQueryNode(SecuredTreeHarness.OrdersId, ["id", "email"]) { OrderBy = [new TreeOrder("id")], Limit = 100 };

        using var doc = JsonDocument.Parse(_h.RunTree(root, access));

        var rows = doc.RootElement.EnumerateArray().ToList();
        rows.Select(r => r.GetProperty("id").GetInt64()).ShouldBe(await _h.CompileOrderIdsAsync(SecuredTreeHarness.ExistsPolicy(), "t1"));
        rows.ShouldAllBe(r => r.GetProperty("email").GetString() == "***");
        doc.RootElement.GetRawText().ShouldNotContain("x.example");
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task SQLA06F_MaskedColumn_NotLeakedViaSubquerySource()
    {
        // SQLA06F-C5: the derived table carries the raw email, so the outer projection must mask it; masks-off never leaves the source.
        var access = await _h.TreeAccessAsync(SecuredTreeHarness.OrdersId, SecuredTreeHarness.ExistsPolicy(), "t1", maskEmail: true);
        var map = new Dictionary<TableIdentifier, TreeTableAccess> { [SecuredTreeHarness.OrdersId] = access };
        var root = new TreeQueryNode(SecuredTreeHarness.OrdersId, ["id", "email"]) { Limit = 100 };

        var compiled = TreeSqlCompiler.Compile(root, map, Autheris.Domain.Common.DatabaseDialect.Sqlite);
        compiled.Sql.ShouldNotContain("autheris_target.*");
        compiled.Sql.ShouldNotContain("\"autheris_target\".*");

        using var doc = JsonDocument.Parse(_h.RunTree(root, map));
        doc.RootElement.GetRawText().ShouldNotContain("a@x.example");

        // a masked column is not a filter or order column in clear
        var filtered = root with { Where = new TreeComparison("email", TreeFilterOperator.Eq, "a@x.example") };
        Should.Throw<Exception>(() => _h.RunTree(filtered, map));
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task Issue143RestrictedScope_WithSubqueryPolicy_MatchesWebSql()
    {
        var access = await _h.TreeAccessAsync(
            SecuredTreeHarness.OrdersId,
            SecuredTreeHarness.ExistsPolicy(),
            "t1",
            maskEmail: true,
            extraAccess: new Dictionary<string, ColumnAccessLevel> { ["amount"] = ColumnAccessLevel.Deny });
        var map = new Dictionary<TableIdentifier, TreeTableAccess> { [SecuredTreeHarness.OrdersId] = access };
        var root = new TreeQueryNode(SecuredTreeHarness.OrdersId, ["id", "email"]) { OrderBy = [new TreeOrder("id")], Limit = 100 };

        using var doc = JsonDocument.Parse(_h.RunTree(root, map));

        doc.RootElement.EnumerateArray().Select(r => r.GetProperty("id").GetInt64())
            .ShouldBe(await _h.CompileOrderIdsAsync(SecuredTreeHarness.ExistsPolicy(), "t1"));
        doc.RootElement.GetRawText().ShouldNotContain("@x.example");
        Should.Throw<Exception>(() => TreeSqlCompiler.Compile(
            new TreeQueryNode(SecuredTreeHarness.OrdersId, ["id", "amount"]) { Limit = 10 }, map, Autheris.Domain.Common.DatabaseDialect.Sqlite));
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task SQLA06F_KeySetStageOff_TreeRowsEqualExistsPath()
    {
        // SQLA06F-C6: no key-set stage on the tree; its rows equal the EXISTS rows of the WebSQL path.
        foreach (var tenant in new[] { "t1", "t2" })
        {
            (await _h.TreeOrderIdsAsync(SecuredTreeHarness.ExistsPolicy(), tenant))
                .ShouldBe(await _h.CompileOrderIdsAsync(SecuredTreeHarness.ExistsPolicy(), tenant));
        }
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task SubqueryPolicy_InactiveDependent_YieldsNoRowsOnBothPaths()
    {
        // SQLA06F-C1 / F5: an inactive dependent makes the policy unenforceable, so the table is unknown to the compiler. Both paths deny
        // (zero rows, never more than WebSQL).
        using var h = new SecuredTreeHarness(dependentMetadata: id => SecuredTreeHarness.DependentMetadata(id, active: false));

        await Should.ThrowAsync<GatewaySecurityException>(() => h.TreeOrderIdsAsync(SecuredTreeHarness.ExistsPolicy(), "t1"));
        await Should.ThrowAsync<Exception>(() => h.CompileOrderIdsAsync(SecuredTreeHarness.ExistsPolicy(), "t1"));
    }
}
