namespace Autheris.Tests.Integration;

using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Policy;
using Autheris.Application.Services;
using Autheris.Application.Sql.Policy;
using Autheris.Application.Sql.Policy.RowFilterKeySets;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Npgsql;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Governance;
using Xunit;

/// <summary>
/// Issue #107 WP-C on a real PostgreSQL (shared Testcontainers server, fresh database): the key query session carries no user identity (SEC-RF107-01 b),
/// runs in a read-only transaction (SEC-RF107-12), fails closed (SEC-RF107-08), works with a SELECT-only role, and the compiled key query returns the
/// tenant's keys only.
/// </summary>
[Trait("Category", "Security")]
public sealed class RowFilterKeySetPostgreSqlTests : IClassFixture<RowFilterKeySetPostgreSqlTests.Fixture>
{
    private readonly Fixture _f;

    public RowFilterKeySetPostgreSqlTests(Fixture fixture) => _f = fixture;

    public sealed class Fixture : IAsyncLifetime
    {
        public PostgreSqlTestDatabase? Db { get; private set; }

        public string ReaderConnectionString { get; private set; } = string.Empty;

        public async Task InitializeAsync()
        {
            Db = await PostgreSqlTestDatabase.CreateAsync();
            if (!Db.IsAvailable) return;
            await using var conn = new NpgsqlConnection(Db.ConnectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            string role = "ks_reader_" + Guid.NewGuid().ToString("N")[..8];
            cmd.CommandText = $"""
                CREATE TABLE public.customers (id integer PRIMARY KEY, country varchar(20) NOT NULL, tenant_id varchar(64) NOT NULL);
                CREATE TABLE public.orders (id integer PRIMARY KEY, client_id integer NULL, tenant_id varchar(64) NOT NULL);
                INSERT INTO public.customers VALUES (1, 'CH', 't1'), (2, 'DE', 't1'), (3, 'CH', 't1'), (4, 'CH', 't2');
                INSERT INTO public.orders VALUES (10, 1, 't1'), (11, 2, 't1'), (12, NULL, 't1');
                CREATE ROLE {role} LOGIN PASSWORD 'KsReader2026x';
                GRANT SELECT ON ALL TABLES IN SCHEMA public TO {role};
                """;
            await cmd.ExecuteNonQueryAsync();
            ReaderConnectionString = new NpgsqlConnectionStringBuilder(Db.ConnectionString) { Username = role, Password = "KsReader2026x" }.ConnectionString;
        }

        public async Task DisposeAsync()
        {
            if (Db is not null) await Db.DisposeAsync();
        }
    }

    private static RowFilterKeySetLoader Loader() => new(new SqlConnectionFactory(new DevelopmentEnvironment()));

    private static CompiledKeyQuery Raw(string sql) => new(sql, [], "varchar(200)", "varchar(200)", "fk", "test");

    private static RowFilterKeySetLoadRequest Request(string connectionString, CompiledKeyQuery query) => new(
        new DataSourceConnectionOptions { Provider = "PostgreSql", ConnectionString = connectionString, DefaultSchema = "public" },
        TargetSqlDialect.PostgreSql, new TenantId("t1"), "ds", query,
        KeySetValueConverter.Create(TargetSqlDialect.PostgreSql, query.KeyColumnType, query.ForeignKeyColumnType)!, 100, TimeSpan.FromSeconds(30));

    private static async Task<string[]> TextRowsAsync(RowFilterKeySetLoadRequest request) =>
        (await Loader().LoadAsync(request, CancellationToken.None)).Keys.Cast<string>().ToArray();

    [Fact]
    public async Task KeyQuerySession_HasNoUserIdentity_OnPooledConnection()
    {
        if (!_f.Db!.IsAvailable) return;

        // A user statement sets its identity as a session setting on a pooled connection (the gateway's non-transactional form), then returns it.
        var factory = new SqlConnectionFactory(new DevelopmentEnvironment());
        var options = new DataSourceConnectionOptions { Provider = "PostgreSql", ConnectionString = _f.Db.ConnectionString, DefaultSchema = "public" };
        await using (var user = await factory.CreateOpenConnectionAsync(options, CancellationToken.None))
        {
            await new DbSessionContextInitializer().InitializeSessionAsync(
                user, null, DatabaseDialect.PostgreSql, new TenantId("t1"), "S-1-5-21-ALICE", "analysis", CancellationToken.None);
        }

        var rows = await TextRowsAsync(Request(_f.Db.ConnectionString, Raw(
            "SELECT 'sid=' || coalesce(current_setting('autheris.user_sid', true), '') || ';purpose=' || coalesce(current_setting('autheris.purpose', true), '') " +
            "|| ';tenant=' || coalesce(current_setting('autheris.tenant_id', true), '')")));

        rows.ShouldBe(["sid=;purpose=;tenant=t1"]);   // SEC-RF107-01 b: tenant only, no user, no purpose
    }

    [Fact]
    public async Task KeyQuery_RunsInAReadOnlyTransaction_SEC_RF107_12()
    {
        if (!_f.Db!.IsAvailable) return;

        var rows = await TextRowsAsync(Request(_f.Db.ConnectionString, Raw("SELECT current_setting('transaction_read_only')")));

        rows.ShouldBe(["on"]);
        // and a write inside the key query is refused by the database
        await Should.ThrowAsync<GatewaySecurityException>(() => TextRowsAsync(Request(_f.Db.ConnectionString, Raw(
            "WITH w AS (INSERT INTO public.customers VALUES (99, 'XX', 't1') RETURNING id::varchar) SELECT * FROM w"))));
    }

    [Fact]
    public async Task KeyQueryFailure_DeniesWithStableCode_FailClosed()
    {
        if (!_f.Db!.IsAvailable) return;
        var request = Request(_f.Db.ConnectionString, Raw("SELECT id::varchar FROM public.does_not_exist"));

        var denied = await Should.ThrowAsync<GatewaySecurityException>(() => Loader().LoadAsync(request, CancellationToken.None));

        denied.ErrorCode.ShouldBe("ROW_FILTER_KEY_SET_UNAVAILABLE");
        denied.Message.ShouldBe(RowFilterKeySetErrorCodes.UnavailableMessage);
        denied.Message.ShouldNotContain("does_not_exist");
    }

    [Fact]
    public async Task KeyQuery_WorksWithASelectOnlyRole()
    {
        if (!_f.Db!.IsAvailable) return;

        var rows = await TextRowsAsync(Request(_f.ReaderConnectionString, Raw("SELECT id::varchar FROM public.customers WHERE tenant_id = 't1' ORDER BY id")));

        rows.ShouldBe(["1", "2", "3"]);
    }

    [Fact]
    public async Task CompiledKeyQuery_ReturnsTheTenantsKeysOnly()
    {
        if (!_f.Db!.IsAvailable) return;
        var orders = new TableIdentity("public", "orders");
        var catalog = new InMemoryTableCatalog(
        [
            new TableCatalogEntry(orders, ImmutableArray.Create(new CatalogColumn("id", "integer"), new CatalogColumn("client_id", "integer"), new CatalogColumn("tenant_id", "character varying(64)")), "tenant_id", 1),
            new TableCatalogEntry(new TableIdentity("public", "customers"), ImmutableArray.Create(new CatalogColumn("id", "integer"), new CatalogColumn("country", "character varying(20)"), new CatalogColumn("tenant_id", "character varying(64)")), "tenant_id", 1)
        ], "public");
        var filter = new ConsentRowFilter
        {
            Id = Guid.NewGuid(), FilterType = RowFilterType.SubqueryCorrelated,
            DependentTable = new TableIdentifier("default", "public", "customers"), DependentTableAlias = "c",
            ForeignKeyColumn = "client_id", PrimaryKeyColumn = "id", SubqueryFilterPredicateJson = "{\"c.country\": \"CH\"}"
        };
        var predicate = new TypedRowFilterBuilder().BuildCombined([new Consent { Effect = ConsentEffect.Allow, RowFilters = [filter] }], [], orders)!;
        var node = predicate.Expression switch
        {
            TrinoSqlEngine.Ast.Nodes.ExistsExpression e => e,
            TrinoSqlEngine.Ast.Nodes.ParenthesizedExpression { Expression: TrinoSqlEngine.Ast.Nodes.ExistsExpression e } => e,
            _ => throw new InvalidOperationException("expected a single correlated site")
        };
        var query = new PolicyKeyQueryCompiler().CompileKeyQuery(
            new PolicyKeyQueryRequest(node, predicate.Parameters, catalog, catalog.Resolve(orders.ToQualifiedName())!,
                new TenantBinding(RowPolicyFactory.TenantParameterName, "t1", SqlParameterType.String), TargetSqlDialect.PostgreSql, 101), CancellationToken.None);
        var converter = KeySetValueConverter.Create(TargetSqlDialect.PostgreSql, query.KeyColumnType, query.ForeignKeyColumnType)!;

        var entry = await Loader().LoadAsync(new RowFilterKeySetLoadRequest(
            new DataSourceConnectionOptions { Provider = "PostgreSql", ConnectionString = _f.Db.ConnectionString, DefaultSchema = "public" }, TargetSqlDialect.PostgreSql,
            new TenantId("t1"), "ds", query, converter, 100, TimeSpan.FromSeconds(30)), CancellationToken.None);

        entry.Keys.Select(k => Convert.ToInt64(k)).OrderBy(k => k).ToArray().ShouldBe([1L, 3L]);
    }
}
