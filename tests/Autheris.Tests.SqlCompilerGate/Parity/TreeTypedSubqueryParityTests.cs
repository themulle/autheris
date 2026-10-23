namespace Autheris.Tests.SqlCompilerGate.Parity;

using System.Globalization;
using Autheris.Application.Sql.Policy;
using Autheris.Tests.SqlCompilerGate.Databases;
using Autheris.Tests.SqlCompilerGate.Harness;
using Autheris.Tests.SqlCompilerGate.Model;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;
using Xunit.Abstractions;

/// <summary>
/// SQLA-06 follow-up: a typed row policy with a correlated subquery runs in the tree compiler through the secured source (the engine pipeline)
/// and exposes exactly the rows of the WebSQL compile path on the same database. The tenant wrapper of the engine applies inside the subquery.
/// </summary>
public sealed class TreeTypedSubqueryParityTests(ParityFixture<SqliteGateDatabase> fixture, ITestOutputHelper output)
    : IClassFixture<ParityFixture<SqliteGateDatabase>>
{
    private static SqlIdentifier Id(string name) => new(name, true);

    private static ColumnReference Col(string alias, string column) => new(new SqlQualifiedName([Id(alias), Id(column)]));

    private static FilterText CustomerExists(ParityContext ctx, Func<PolicyParameterNamer, Expression> condition)
    {
        var namer = new PolicyParameterNamer("__rf_");
        var where = condition(namer);
        var subquery = new SelectStatement(
            null,
            new QuerySpecification(
                false,
                [new ColumnSelectItem(Col("c", "id"), null)],
                new NamedTableSource(new SqlQualifiedName([Id(ctx.Schema), Id("customers")]), Id("c")),
                where,
                null,
                null),
            null,
            null);
        var typed = PolicyPredicate.Create(new ExistsExpression(subquery), namer.Values);
        return new FilterText("(unused)", FilterText.NoParameters) { Typed = typed };
    }

    private static long Whole(RowDef row, string table, string column) =>
        Convert.ToInt64(row.Values[GateModel.Table(table).Columns.ToList().FindIndex(c => c.Name == column)], CultureInfo.InvariantCulture);

    private static string Text(RowDef row, string table, string column) =>
        Convert.ToString(row.Values[GateModel.Table(table).Columns.ToList().FindIndex(c => c.Name == column)], CultureInfo.InvariantCulture)!;

    private async Task<(IReadOnlyList<long> Tree, IReadOnlyList<long> WebSql)> RunBothAsync(FilterText filter)
    {
        var env = fixture.Env;
        var outcome = await new TreeSqlCompilerConsumer().RunAsync(env, filter);
        output.WriteLine(GateProfile.Describe(outcome));
        var rows = outcome.ShouldBeOfType<EngineOutcome.Rows>();
        var tree = rows.Data.Select(r => Convert.ToInt64(r[0], CultureInfo.InvariantCulture)).ToList();

        var metadata = ParityData.OrdersMetadata(env.Context);
        var webSql = await new GateTreeSources(env.Context).WebSqlOrderIdsAsync(
            env.Database, metadata, ParityData.TreeDecision(env.Context, filter), GateModel.TenantA);
        return (tree, webSql);
    }

    [Fact]
    public async Task SQLA_06_TreeSqlCompiler_TypedSubqueryFilter_MatchesWebSqlRows()
    {
        var filter = CustomerExists(fixture.Env.Context, namer =>
        {
            var name = namer.Add(new PolicyValue("customers-A-1", SqlParameterType.String));
            return new BinaryExpression(
                new BinaryExpression(Col("c", "id"), BinaryOperator.Equal, PolicyParameterNamer.Column($"{RowFilterAliases.Target}.customer_id")),
                BinaryOperator.And,
                new BinaryExpression(Col("c", "name"), BinaryOperator.Equal, name));
        });

        var (tree, webSql) = await RunBothAsync(filter);

        var customersA1 = GateModel.Rows.Where(r => r.Table == "customers" && r.Tenant == GateModel.TenantA && Text(r, "customers", "name") == "customers-A-1")
            .Select(r => Whole(r, "customers", "id")).ToHashSet();
        var expected = GateModel.Rows
            .Where(r => r.Table == "orders" && r.Tenant == GateModel.TenantA && customersA1.Contains(Whole(r, "orders", "customer_id")))
            .Select(r => r.Id).Order().ToList();
        expected.ShouldNotBeEmpty();
        tree.ShouldBe(expected);
        tree.ShouldBe(webSql);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task SQLA_06_TreeSqlCompiler_TypedSubqueryFilter_NeverReturnsForeignTenantRows()
    {
        // SQLA06F-C1/C3: tenant B owns customers 1001..1004. Without the engine's tenant wrapper inside the subquery this uncorrelated EXISTS
        // would hold for every tenant-A order; with it, tenant A sees no such customer and no row is admitted. Neither path returns a B row.
        // (tenant-A customers above that id are region US; the tenant-B EU customers 1001 and 1003 are the only match)
        var filter = CustomerExists(fixture.Env.Context, namer => new BinaryExpression(
            new BinaryExpression(Col("c", "id"), BinaryOperator.GreaterThanOrEqual, namer.Add(new PolicyValue(1001L, SqlParameterType.Int64))),
            BinaryOperator.And,
            new BinaryExpression(Col("c", "region"), BinaryOperator.Equal, namer.Add(new PolicyValue("EU", SqlParameterType.String)))));

        var (tree, webSql) = await RunBothAsync(filter);

        tree.ShouldBeEmpty();
        webSql.ShouldBeEmpty();
        tree.ShouldAllBe(id => GateModel.Rows.Any(r => r.Table == "orders" && r.Id == id && r.Tenant == GateModel.TenantA));
    }
}
