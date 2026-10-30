namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
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
using Microsoft.Data.SqlClient;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Governance;
using Xunit;

/// <summary>
/// Issue #107 WP-C on a real SQL Server (shared Testcontainers server, fresh database): the key query session carries no user identity (SEC-RF107-01 b),
/// runs READ COMMITTED regardless of <c>ReadUncommitted</c> (SEC-RF107-12), fails closed (SEC-RF107-08), works with a SELECT-only login, and the compiled
/// key query returns the tenant's keys only.
/// </summary>
[Trait("Category", "Security")]
public sealed class RowFilterKeySetSqlServerTests : IClassFixture<RowFilterKeySetSqlServerTests.Fixture>
{
    private readonly Fixture _f;

    public RowFilterKeySetSqlServerTests(Fixture fixture) => _f = fixture;

    public sealed class Fixture : IAsyncLifetime
    {
        public SqlServerTestDatabase? Db { get; private set; }

        public string ReaderConnectionString { get; private set; } = string.Empty;

        public async Task InitializeAsync()
        {
            Db = await SqlServerTestDatabase.CreateAsync();
            if (!Db.IsAvailable) return;
            await using var conn = new SqlConnection(Db.ConnectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            string login = "ks_reader_" + Guid.NewGuid().ToString("N")[..8];
            cmd.CommandText = $"""
                CREATE TABLE dbo.customers (id int NOT NULL PRIMARY KEY, country nvarchar(20) NOT NULL, tenant_id nvarchar(64) NOT NULL);
                CREATE TABLE dbo.orders (id int NOT NULL PRIMARY KEY, client_id int NULL, tenant_id nvarchar(64) NOT NULL);
                INSERT dbo.customers VALUES (1, N'CH', N't1'), (2, N'DE', N't1'), (3, N'CH', N't1'), (4, N'CH', N't2');
                INSERT dbo.orders VALUES (10, 1, N't1'), (11, 2, N't1'), (12, NULL, N't1');
                CREATE LOGIN [{login}] WITH PASSWORD = 'Ks!Reader#2026x', CHECK_POLICY = OFF;
                CREATE USER [{login}] FOR LOGIN [{login}];
                ALTER ROLE db_datareader ADD MEMBER [{login}];
                """;
            await cmd.ExecuteNonQueryAsync();
            ReaderConnectionString = new SqlConnectionStringBuilder(Db.ConnectionString) { UserID = login, Password = "Ks!Reader#2026x" }.ConnectionString;
        }

        public async Task DisposeAsync()
        {
            if (Db is not null) await Db.DisposeAsync();
        }
    }

    private static RowFilterKeySetLoader Loader() => new(new SqlConnectionFactory(new DevelopmentEnvironment()));

    private static CompiledKeyQuery Raw(string sql) => new(sql, [], "nvarchar(200)", "nvarchar(200)", "fk", "test");

    private static RowFilterKeySetLoadRequest Request(string connectionString, CompiledKeyQuery query, bool readUncommitted = false, string tenant = "t1") => new(
        new DataSourceConnectionOptions { Provider = "SqlServer", ConnectionString = connectionString, ReadUncommitted = readUncommitted },
        TargetSqlDialect.SqlServer, new TenantId(tenant), "ds", query,
        KeySetValueConverter.Create(TargetSqlDialect.SqlServer, query.KeyColumnType, query.ForeignKeyColumnType)!, 100, TimeSpan.FromSeconds(30));

    private async Task<string[]> TextRowsAsync(RowFilterKeySetLoadRequest request) =>
        (await Loader().LoadAsync(request, CancellationToken.None)).Keys.Cast<string>().ToArray();

    [Fact]
    public async Task KeyQuerySession_HasNoUserIdentity_OnPooledConnection()
    {
        if (!_f.Db!.IsAvailable) return;

        // A user statement runs on a pooled connection with its identity in the session context, then the connection goes back to the pool.
        var factory = new SqlConnectionFactory(new DevelopmentEnvironment());
        var options = new DataSourceConnectionOptions { Provider = "SqlServer", ConnectionString = _f.Db.ConnectionString };
        await using (var user = await factory.CreateOpenConnectionAsync(options, CancellationToken.None))
        {
            await new DbSessionContextInitializer().InitializeSessionAsync(
                user, null, DatabaseDialect.SqlServer, new TenantId("t1"), "S-1-5-21-ALICE", "analysis", CancellationToken.None);
        }

        var rows = await TextRowsAsync(Request(_f.Db.ConnectionString, Raw(
            "SELECT CONCAT(N'sid=', ISNULL(CAST(SESSION_CONTEXT(N'autheris.user_sid') AS nvarchar(100)), N''), N';purpose=', " +
            "ISNULL(CAST(SESSION_CONTEXT(N'autheris.purpose') AS nvarchar(100)), N''), N';tenant=', CAST(SESSION_CONTEXT(N'autheris.tenant_id') AS nvarchar(100)))")));

        rows.ShouldBe(["sid=;purpose=;tenant=t1"]);   // SEC-RF107-01 b: tenant only, no user, no purpose
    }

    [Fact]
    public async Task KeyQuery_RunsReadCommitted_EvenWithReadUncommitted_SEC_RF107_12()
    {
        if (!_f.Db!.IsAvailable) return;
        const string isolation = "SELECT CAST(transaction_isolation_level AS nvarchar(10)) FROM sys.dm_exec_sessions WHERE session_id = @@SPID";

        // Control: the factory applies READ UNCOMMITTED (1) to a user session of this data source.
        var factory = new SqlConnectionFactory(new DevelopmentEnvironment());
        await using (var user = await factory.CreateOpenConnectionAsync(
            new DataSourceConnectionOptions { Provider = "SqlServer", ConnectionString = _f.Db.ConnectionString, ReadUncommitted = true }, CancellationToken.None))
        {
            await using var cmd = user.CreateCommand();
            cmd.CommandText = isolation;
            (await cmd.ExecuteScalarAsync())!.ToString().ShouldBe("1");
        }

        var rows = await TextRowsAsync(Request(_f.Db.ConnectionString, Raw(isolation), readUncommitted: true));

        rows.ShouldBe(["2"]);   // transaction_isolation_level = 2 (READ COMMITTED)
    }

    [Fact]
    public async Task KeyQueryFailure_DeniesWithStableCode_AndNothingIsCached_FailClosed()
    {
        if (!_f.Db!.IsAvailable) return;
        var request = Request(_f.Db.ConnectionString, Raw("SELECT CAST(id AS nvarchar(10)) FROM dbo.does_not_exist"));

        var first = await Should.ThrowAsync<GatewaySecurityException>(() => Loader().LoadAsync(request, CancellationToken.None));
        var second = await Should.ThrowAsync<GatewaySecurityException>(() => Loader().LoadAsync(request, CancellationToken.None));

        first.ErrorCode.ShouldBe("ROW_FILTER_KEY_SET_UNAVAILABLE");
        first.Message.ShouldBe(RowFilterKeySetErrorCodes.UnavailableMessage);
        first.Message.ShouldNotContain("does_not_exist");
        second.ErrorCode.ShouldBe("ROW_FILTER_KEY_SET_UNAVAILABLE");
    }

    [Fact]
    public async Task KeyQuery_WorksWithASelectOnlyLogin()
    {
        if (!_f.Db!.IsAvailable) return;

        var rows = await TextRowsAsync(Request(_f.ReaderConnectionString, Raw("SELECT CAST(id AS nvarchar(10)) FROM dbo.customers WHERE tenant_id = N't1' ORDER BY id")));

        rows.ShouldBe(["1", "2", "3"]);
    }

    [Fact]
    public async Task CompiledKeyQuery_ReturnsTheTenantsKeysOnly_AndMatchesTheExistsSet()
    {
        if (!_f.Db!.IsAvailable) return;
        var orders = new TableIdentity("dbo", "orders");
        var catalog = new InMemoryTableCatalog(
        [
            new TableCatalogEntry(orders, ImmutableArray.Create(new CatalogColumn("id", "int"), new CatalogColumn("client_id", "int"), new CatalogColumn("tenant_id", "nvarchar(64)")), "tenant_id", 1),
            new TableCatalogEntry(new TableIdentity("dbo", "customers"), ImmutableArray.Create(new CatalogColumn("id", "int"), new CatalogColumn("country", "nvarchar(20)"), new CatalogColumn("tenant_id", "nvarchar(64)")), "tenant_id", 1)
        ], "dbo");
        var filter = new ConsentRowFilter
        {
            Id = Guid.NewGuid(), FilterType = RowFilterType.SubqueryCorrelated,
            DependentTable = new TableIdentifier("default", "dbo", "customers"), DependentTableAlias = "c",
            ForeignKeyColumn = "client_id", PrimaryKeyColumn = "id", SubqueryFilterPredicateJson = "{\"c.country\": \"CH\"}"
        };
        var predicate = new TypedRowFilterBuilder().BuildCombined([new Consent { TenantId = Autheris.Tests.Common.TestTenants.Default, Effect = ConsentEffect.Allow, RowFilters = [filter] }], [], orders)!;
        var node = predicate.Expression switch
        {
            TrinoSqlEngine.Ast.Nodes.ExistsExpression e => e,
            TrinoSqlEngine.Ast.Nodes.ParenthesizedExpression { Expression: TrinoSqlEngine.Ast.Nodes.ExistsExpression e } => e,
            _ => throw new InvalidOperationException("expected a single correlated site")
        };
        var query = new PolicyKeyQueryCompiler().CompileKeyQuery(
            new PolicyKeyQueryRequest(node, predicate.Parameters, catalog, catalog.Resolve(orders.ToQualifiedName())!,
                new TenantBinding(RowPolicyFactory.TenantParameterName, "t1", SqlParameterType.String), TargetSqlDialect.SqlServer, 101), CancellationToken.None);
        var converter = KeySetValueConverter.Create(TargetSqlDialect.SqlServer, query.KeyColumnType, query.ForeignKeyColumnType)!;

        var entry = await Loader().LoadAsync(new RowFilterKeySetLoadRequest(
            new DataSourceConnectionOptions { Provider = "SqlServer", ConnectionString = _f.Db.ConnectionString }, TargetSqlDialect.SqlServer,
            new TenantId("t1"), "ds", query, converter, 100, TimeSpan.FromSeconds(30)), CancellationToken.None);

        entry.Overflow.ShouldBeFalse();
        entry.Keys.Select(k => Convert.ToInt64(k)).OrderBy(k => k).ToArray().ShouldBe([1L, 3L]);   // tenant t2's customer 4 is never read
    }
}
