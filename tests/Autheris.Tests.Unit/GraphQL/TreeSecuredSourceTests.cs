using System.Collections.Immutable;
using System.Text.Json;
using Autheris.Application.Policy;
using Autheris.Application.Sql.Masking;
using Autheris.Application.Sql.Policy;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using NSubstitute;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;
using Xunit;

namespace Autheris.Tests.Unit.GraphQL;

/// <summary>SQLA-06 follow-up (SQLA06F-C1..C9): the secured source of a tree table, its marker remap and its fail-closed rules.</summary>
public sealed class TreeSecuredSourceTests : IDisposable
{
    private static readonly TableIdentity Orders = new("main", "orders");
    private readonly SecuredTreeHarness _h = new();

    public void Dispose() => _h.Dispose();

    private static BoundParameter Param(string marker, object? value, ParameterOrigin origin = ParameterOrigin.Policy) =>
        new(marker, marker.TrimStart('@', '$'), 0, value, SqlParameterType.String, origin);

    private static CompiledSql Compiled(string sql, ImmutableArray<BoundParameter> parameters, bool tenantPredicate = true, bool policyPredicate = true)
    {
        var predicates = ImmutableArray.CreateBuilder<SecurityPredicateId>();
        if (tenantPredicate)
        {
            predicates.Add(new SecurityPredicateId(Orders.ToString(), 0));
        }

        if (policyPredicate)
        {
            predicates.Add(new SecurityPredicateId(Orders.ToString(), 1));
        }

        return new CompiledSql(sql, parameters, TargetSqlDialect.Sqlite, SqlStatementClass.Select, predicates.ToImmutable(), "test");
    }

    private TreeSecuredSourceBuilder BuilderReturning(CompiledSql compiled)
    {
        var engine = Substitute.For<ISqlEngine>();
        engine.Compile(Arg.Any<ReadOnlyMemory<char>>(), Arg.Any<CompileRequest>(), Arg.Any<CancellationToken>()).Returns(compiled);
        return new TreeSecuredSourceBuilder(_h.Factory, engine);
    }

    private static Task<TreeSecuredSource> Build(TreeSecuredSourceBuilder builder, string tenant = "t1", DatabaseDialect dialect = DatabaseDialect.Sqlite, CancellationToken ct = default)
    {
        var meta = SecuredTreeHarness.Meta(SecuredTreeHarness.OrdersId);
        var access = SecuredTreeHarness.Resolved(meta, SecuredTreeHarness.Decision(SecuredTreeHarness.OrdersId, SecuredTreeHarness.ExistsPolicy()), tenant);
        return builder.BuildAsync(access, SecuredTreeHarness.DataSource, dialect, ct);
    }

    private static Dictionary<TableIdentifier, TreeTableAccess> Access(TreeSecuredSource source) => new()
    {
        [SecuredTreeHarness.OrdersId] = new TreeTableAccess(
            SecuredTreeHarness.Meta(SecuredTreeHarness.OrdersId),
            SecuredTreeHarness.Decision(SecuredTreeHarness.OrdersId, SecuredTreeHarness.ExistsPolicy()),
            "tenant_id",
            "t1",
            source)
    };

    private static CompiledTreeQuery CompileTree(TreeSecuredSource source, TreeQueryNode? root = null) =>
        TreeSqlCompiler.Compile(root ?? new TreeQueryNode(SecuredTreeHarness.OrdersId, ["id"]) { Limit = 10 }, Access(source), DatabaseDialect.Sqlite);

    private static TreeSecuredSource Source(string sql, params BoundParameter[] parameters) =>
        new(sql, [.. parameters], TargetSqlDialect.Sqlite);

    // ---- splice and remap ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task SecuredSource_ExistsPolicy_SplicesDerivedTableAndNoRowFilterText()
    {
        var access = await _h.TreeAccessAsync(SecuredTreeHarness.OrdersId, SecuredTreeHarness.ExistsPolicy(), "t1");
        var map = new Dictionary<TableIdentifier, TreeTableAccess> { [SecuredTreeHarness.OrdersId] = access };

        var compiled = TreeSqlCompiler.Compile(new TreeQueryNode(SecuredTreeHarness.OrdersId, ["id"]) { Limit = 10 }, map, DatabaseDialect.Sqlite);

        access.SecuredSource.ShouldNotBeNull();
        compiled.Sql.ShouldContain("FROM (SELECT");
        compiled.Sql.ShouldContain(") AS \"autheris_target\"");
        compiled.Sql.ShouldNotContain("__rf_");
        compiled.Sql.ShouldNotContain("@p0");
        compiled.Parameters.Keys.ShouldAllBe(k => k.StartsWith("@gq", StringComparison.Ordinal));
    }

    [Fact]
    public void SecuredSource_RemapsEngineMarkersToGqNames()
    {
        var source = Source("SELECT \"id\" FROM \"orders\" WHERE \"tenant_id\" = @p0 AND \"amount\" > @p1", Param("@p0", "t1", ParameterOrigin.Tenant), Param("@p1", 7L));

        var compiled = CompileTree(source);

        compiled.Sql.ShouldNotContain("@p0");
        compiled.Sql.ShouldNotContain("@p1");
        compiled.Parameters.Values.ShouldContain("t1");
        compiled.Parameters.Values.ShouldContain(7L);
    }

    [Fact]
    public void SecuredSource_TwoTablesSamePolicyParameter_NoCollision()
    {
        var a = Source("SELECT \"id\" FROM \"orders\" WHERE \"tenant_id\" = @p0", Param("@p0", "t1", ParameterOrigin.Tenant));
        var b = Source("SELECT \"id\", \"order_id\" FROM \"items\" WHERE \"label\" = @p0", Param("@p0", "other"));
        var child = new TreeQueryNode(SecuredTreeHarness.ItemsId, ["id"]) { Limit = 5 };
        var root = new TreeQueryNode(SecuredTreeHarness.OrdersId, ["id"])
        {
            Limit = 10,
            Relations = [new TreeRelationNode("items", ["id"], ["order_id"], true, child)]
        };
        var access = new Dictionary<TableIdentifier, TreeTableAccess>
        {
            [SecuredTreeHarness.OrdersId] = Access(a)[SecuredTreeHarness.OrdersId],
            [SecuredTreeHarness.ItemsId] = new TreeTableAccess(
                SecuredTreeHarness.Meta(SecuredTreeHarness.ItemsId),
                SecuredTreeHarness.Decision(SecuredTreeHarness.ItemsId, SecuredTreeHarness.ItemPolicy()),
                "tenant_id",
                "t1",
                b)
        };

        var compiled = TreeSqlCompiler.Compile(root, access, DatabaseDialect.Sqlite);

        compiled.Sql.ShouldNotContain("@p0");
        compiled.Parameters.Values.Count(v => Equals(v, "other")).ShouldBe(1);
        compiled.Parameters.Values.Count(v => Equals(v, "t1")).ShouldBeGreaterThanOrEqualTo(2);
        compiled.Parameters.Keys.Distinct().Count().ShouldBe(compiled.Parameters.Count);
    }

    [Fact]
    public void SecuredSource_MarkerP1NotPrefixOfP10()
    {
        var parameters = Enumerable.Range(0, 11).Select(i => Param($"@p{i}", (long)i)).ToArray();
        var source = Source("SELECT \"id\" FROM \"orders\" WHERE \"amount\" = @p1 AND \"id\" = @p10 AND \"amount\" > @p0 AND \"id\" IN (@p2, @p3, @p4, @p5, @p6, @p7, @p8, @p9)", parameters);

        var compiled = CompileTree(source);

        var sql = compiled.Sql;
        var amountName = sql.Split("\"amount\" = ")[1].Split(' ')[0];
        var idName = sql.Split("\"id\" = ")[1].Split(' ')[0].TrimEnd(')');
        compiled.Parameters[amountName].ShouldBe(1L);
        compiled.Parameters[idName].ShouldBe(10L);
    }

    [Theory]
    [InlineData("SELECT 'a @p0 b', \"@p0\", [@p0] FROM t WHERE x = @p0", "SELECT 'a @p0 b', \"@p0\", [@p0] FROM t WHERE x = @gq9")]
    [InlineData("SELECT 1 -- @p0 here\nFROM t WHERE x = @p0", "SELECT 1 -- @p0 here\nFROM t WHERE x = @gq9")]
    public void SQLA06F_MarkerRemap_DoesNotTouchLiteralsOrQuotedIdentifiers(string sql, string expected)
    {
        TreeSecuredSourceMarkers.Remap(sql, TargetSqlDialect.Sqlite, new Dictionary<string, string> { ["@p0"] = "@gq9" }).ShouldBe(expected);
    }

    [Trait("Category", "Security")]
    [Fact]
    public void SQLA06F_MarkerRemap_SqliteNumberedQuestionMarkers_WholeTokenAndLiteralsUntouched()
    {
        var map = new Dictionary<string, string> { ["?1"] = "@gq1", ["?10"] = "@gq10" };

        TreeSecuredSourceMarkers.Remap("SELECT '?1' WHERE a = ?1 AND b = ?10 AND c = ?1", TargetSqlDialect.Sqlite, map)
            .ShouldBe("SELECT '?1' WHERE a = @gq1 AND b = @gq10 AND c = @gq1");
        Should.Throw<GatewaySecurityException>(() => TreeSecuredSourceMarkers.Remap("SELECT 1 WHERE a = ?1 AND b = ?10 AND c = ?", TargetSqlDialect.Sqlite, map));
        Should.Throw<GatewaySecurityException>(() => TreeSecuredSourceMarkers.Remap("SELECT 1 WHERE a = ?1 AND b = ?10 AND c = ?2", TargetSqlDialect.Sqlite, map));
    }

    [Trait("Category", "Security")]
    [Fact]
    public void SQLA06F_MarkerRemap_PostgreSql_DollarMarkersWholeTokenAndDollarQuotesDenied()
    {
        var map = new Dictionary<string, string> { ["$1"] = "@gq1", ["$10"] = "@gq10" };

        TreeSecuredSourceMarkers.Remap("SELECT 1 WHERE a = $1 AND b = $10", TargetSqlDialect.PostgreSql, map).ShouldBe("SELECT 1 WHERE a = @gq1 AND b = @gq10");
        Should.Throw<GatewaySecurityException>(() => TreeSecuredSourceMarkers.Remap("SELECT $$x$$ WHERE a = $1 AND b = $10", TargetSqlDialect.PostgreSql, map));
        Should.Throw<GatewaySecurityException>(() => TreeSecuredSourceMarkers.Remap("SELECT $t$x$t$ WHERE a = $1 AND b = $10", TargetSqlDialect.PostgreSql, map));
    }

    [Trait("Category", "Security")]
    [Theory]
    [InlineData("SELECT 1 WHERE a = @p0 AND b = @p5")]            // an engine-style marker the source never declared
    [InlineData("SELECT 1 WHERE a = 1")]                          // a declared marker that never occurs
    [InlineData("SELECT 'unterminated WHERE a = @p0")]
    public void SQLA06F_MarkerRemap_AnomalyDeniesTheRequest(string sql)
    {
        Should.Throw<GatewaySecurityException>(() =>
            TreeSecuredSourceMarkers.Remap(sql, TargetSqlDialect.Sqlite, new Dictionary<string, string> { ["@p0"] = "@gq0" }))
            .ErrorCode.ShouldBe("TREE_ROW_POLICY_UNSUPPORTED");
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task SQLA06F_MarkerLookalikeInClientValue_StaysBoundAndInert()
    {
        var access = await _h.TreeAccessAsync(SecuredTreeHarness.OrdersId, SecuredTreeHarness.ExistsPolicy(), "t1");
        var map = new Dictionary<TableIdentifier, TreeTableAccess> { [SecuredTreeHarness.OrdersId] = access };

        foreach (var value in new[] { "@p0", "$1", "@gq1", "@p10", "'; DROP TABLE orders; --" })
        {
            var root = new TreeQueryNode(SecuredTreeHarness.OrdersId, ["id"])
            {
                Limit = 10,
                Where = new TreeComparison("email", TreeFilterOperator.Eq, value)
            };

            var compiled = TreeSqlCompiler.Compile(root, map, DatabaseDialect.Sqlite);

            if (value != "@gq1")
            {
                compiled.Sql.ShouldNotContain(value);   // "@gq1" is a legitimate name; its value must still travel as a bound value only
            }

            compiled.Parameters.Values.ShouldContain(value);
            using var doc = JsonDocument.Parse(_h.RunTree(root, map));
            doc.RootElement.GetArrayLength().ShouldBe(0);
        }
    }

    // ---- builder: fail closed --------------------------------------------------------------------------------------------

    [Trait("Category", "Security")]
    [Fact]
    public async Task SecuredSource_ClientOriginParameter_Denied()
    {
        var builder = BuilderReturning(Compiled("SELECT \"id\" FROM \"orders\" WHERE \"a\" = @p0", [Param("@p0", "x", ParameterOrigin.ClientNamed)]));

        (await Should.ThrowAsync<GatewaySecurityException>(() => Build(builder))).ErrorCode.ShouldBe("TREE_ROW_POLICY_UNSUPPORTED");
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task SecuredSource_MissingPolicyPredicateId_Denied()
    {
        var builder = BuilderReturning(Compiled("SELECT \"id\" FROM \"orders\"", [], policyPredicate: false));

        await Should.ThrowAsync<GatewaySecurityException>(() => Build(builder));
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task SecuredSource_MissingTenantPredicateId_Denied()
    {
        var builder = BuilderReturning(Compiled("SELECT \"id\" FROM \"orders\"", [], tenantPredicate: false));

        await Should.ThrowAsync<GatewaySecurityException>(() => Build(builder));
    }

    [Trait("Category", "Security")]
    [Theory]
    [InlineData("SELECT \"id\" FROM \"orders\" LIMIT 5")]
    [InlineData("SELECT TOP (5) \"id\" FROM \"orders\"")]
    [InlineData("SELECT \"id\" FROM \"orders\" ORDER BY \"id\" OFFSET 0 ROWS FETCH NEXT 5 ROWS ONLY")]
    public async Task SQLA06F_RootLimitInSource_Denied(string sql)
    {
        var builder = BuilderReturning(Compiled(sql, []));

        await Should.ThrowAsync<GatewaySecurityException>(() => Build(builder));
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task SQLA06F_TenantParameterOfAnotherTenant_Denied()
    {
        var builder = BuilderReturning(Compiled("SELECT \"id\" FROM \"orders\" WHERE \"tenant_id\" = @p0", [Param("@p0", "other", ParameterOrigin.Tenant)]));

        await Should.ThrowAsync<GatewaySecurityException>(() => Build(builder, tenant: "t1"));
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task SQLA06F_UnusedMarkerAndLeftoverEngineMarker_Denied()
    {
        await Should.ThrowAsync<GatewaySecurityException>(() =>
            Build(BuilderReturning(Compiled("SELECT \"id\" FROM \"orders\"", [Param("@p0", "x")]))));
        await Should.ThrowAsync<GatewaySecurityException>(() =>
            Build(BuilderReturning(Compiled("SELECT \"id\" FROM \"orders\" WHERE a = @p0 AND b = @p3", [Param("@p0", "x")]))));
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task SQLA06F_NestedSubquery_DeniesRequest()
    {
        // the dependent table's own subquery is a nested policy subquery: never accepted (PolicySubqueryTenantRewriter)
        var inner = new ExistsExpression(new SelectStatement(null,
            new QuerySpecification(false, [new ColumnSelectItem(new ColumnReference(new SqlQualifiedName([new SqlIdentifier("ic", true), new SqlIdentifier("item_id", true)])), null)],
                new NamedTableSource(new SqlQualifiedName([new SqlIdentifier("main", true), new SqlIdentifier("item_consents", true)]), new SqlIdentifier("ic", true)),
                null, null, null),
            null, null));
        var outer = new ExistsExpression(new SelectStatement(null,
            new QuerySpecification(false, [new ColumnSelectItem(new ColumnReference(new SqlQualifiedName([new SqlIdentifier("c", true), new SqlIdentifier("order_id", true)])), null)],
                new NamedTableSource(new SqlQualifiedName([new SqlIdentifier("main", true), new SqlIdentifier("consents", true)]), new SqlIdentifier("c", true)),
                inner, null, null),
            null, null));
        var policy = new PolicyPredicateRowPolicy(PolicyPredicate.Create(outer, new Dictionary<string, PolicyValue>()), TargetSqlDialect.Sqlite, new PolicyPredicateRenderer());
        var access = SecuredTreeHarness.Resolved(SecuredTreeHarness.Meta(SecuredTreeHarness.OrdersId), SecuredTreeHarness.Decision(SecuredTreeHarness.OrdersId, policy), "t1");

        await Should.ThrowAsync<GatewaySecurityException>(() => _h.Builder.BuildAsync(access, SecuredTreeHarness.DataSource, DatabaseDialect.Sqlite, CancellationToken.None));
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task SQLA06F_UncatalogedSubqueryTable_DeniesRequest()
    {
        using var h = new SecuredTreeHarness(catalogTables: ["orders"]);

        await Should.ThrowAsync<GatewaySecurityException>(() => h.TreeOrderIdsAsync(SecuredTreeHarness.ExistsPolicy(), "t1"));
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task SQLA06F_DependentWithoutTenantColumn_DeniesRequest()
    {
        using var h = new SecuredTreeHarness(
            new GatewayOptions { DataSources = new SqlDataSourceOptions { RequireTenantColumn = true } },
            id => SecuredTreeHarness.DependentMetadata(id, withTenant: false));

        await Should.ThrowAsync<GatewaySecurityException>(() => h.TreeOrderIdsAsync(SecuredTreeHarness.ExistsPolicy(), "t1"));
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task SQLA06F_FactoryOrEngineMissing_Denied()
    {
        var access = SecuredTreeHarness.Resolved(SecuredTreeHarness.Meta(SecuredTreeHarness.OrdersId), SecuredTreeHarness.Decision(SecuredTreeHarness.OrdersId, SecuredTreeHarness.ExistsPolicy()), "t1");

        await Should.ThrowAsync<GatewaySecurityException>(() =>
            new TreeSecuredSourceBuilder(null, _h.Engine).BuildAsync(access, SecuredTreeHarness.DataSource, DatabaseDialect.Sqlite, CancellationToken.None));
        await Should.ThrowAsync<GatewaySecurityException>(() =>
            new TreeSecuredSourceBuilder(_h.Factory, null).BuildAsync(access, SecuredTreeHarness.DataSource, DatabaseDialect.Sqlite, CancellationToken.None));
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task SQLA06F_UnsupportedDialect_Denied()
    {
        await Should.ThrowAsync<GatewaySecurityException>(() => Build(_h.Builder, dialect: DatabaseDialect.Oracle));
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task SQLA06F_UnsupportedPolicyType_Denied()
    {
        var text = TableAccessDecision.Allowed(SecuredTreeHarness.OrdersId, new Dictionary<string, ColumnAccessLevel>(), "\"autheris_target\".\"id\" = 1", hasUnconstrainedColumnAllow: true);
        var access = SecuredTreeHarness.Resolved(SecuredTreeHarness.Meta(SecuredTreeHarness.OrdersId), text, "t1");

        await Should.ThrowAsync<GatewaySecurityException>(() => _h.Builder.BuildAsync(access, SecuredTreeHarness.DataSource, DatabaseDialect.Sqlite, CancellationToken.None));
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task SQLA06F_BuilderException_MapsToFixedCode_NoInnerMessage()
    {
        var engine = Substitute.For<ISqlEngine>();
        engine.Compile(Arg.Any<ReadOnlyMemory<char>>(), Arg.Any<CompileRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw new InvalidOperationException("secret table consents predicate text"));

        var ex = await Should.ThrowAsync<GatewaySecurityException>(() => Build(new TreeSecuredSourceBuilder(_h.Factory, engine)));

        ex.ErrorCode.ShouldBe("TREE_ROW_POLICY_UNSUPPORTED");
        ex.Message.ShouldBe("The row policy of this table is not supported in tree queries.");
        ex.InnerException.ShouldBeNull();
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task SQLA06F_BuilderCancellation_PropagatesCallerToken()
    {
        using var cts = new CancellationTokenSource();
        var engine = Substitute.For<ISqlEngine>();
        engine.Compile(Arg.Any<ReadOnlyMemory<char>>(), Arg.Any<CompileRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            });

        await Should.ThrowAsync<OperationCanceledException>(() => Build(new TreeSecuredSourceBuilder(_h.Factory, engine), ct: cts.Token));

        // a cancellation that is not the caller's is a denial, not a propagation
        var other = Substitute.For<ISqlEngine>();
        other.Compile(Arg.Any<ReadOnlyMemory<char>>(), Arg.Any<CompileRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw new OperationCanceledException());
        await Should.ThrowAsync<GatewaySecurityException>(() => Build(new TreeSecuredSourceBuilder(_h.Factory, other)));
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task SQLA06F_TwoTenants_SameTable_NoSharedSecuredSource()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 40).Select(i => Build(_h.Builder, tenant: i % 2 == 0 ? "t1" : "t2")));

        for (var i = 0; i < results.Length; i++)
        {
            var tenant = i % 2 == 0 ? "t1" : "t2";
            results[i].Parameters.Where(p => p.Origin == ParameterOrigin.Tenant).ShouldAllBe(p => (string?)p.Value == tenant);
        }

        results.Distinct().Count().ShouldBe(results.Length);
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task Builder_MasksNotAppliedInSource()
    {
        var meta = SecuredTreeHarness.Meta(SecuredTreeHarness.OrdersId, maskEmail: true);
        var decision = SecuredTreeHarness.Decision(SecuredTreeHarness.OrdersId, SecuredTreeHarness.ExistsPolicy()) with
        {
            ColumnMasks = new ColumnMaskPolicy(new Dictionary<string, MaskSpec>
            {
                ["email"] = SqlDataMaskingProvider.BuildUnkeyedMaskSpec(null, "email", DatabaseDialect.Sqlite)
            })
        };
        var access = SecuredTreeHarness.Resolved(meta, decision, "t1");

        var source = await _h.Builder.BuildAsync(access, SecuredTreeHarness.DataSource, DatabaseDialect.Sqlite, CancellationToken.None);

        source.Parameters.ShouldNotContain(p => p.Origin == ParameterOrigin.Mask);
        source.Sql.ShouldNotContain("***");
    }
}
