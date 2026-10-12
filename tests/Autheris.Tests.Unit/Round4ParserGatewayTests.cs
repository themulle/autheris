namespace Autheris.Tests.Unit;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// Round 4 (Nachprüfung 2026-10-02): WebSQL gateway side of P-01, P-02/SQ-06, P-05, P-06/SQ-11/SQ-13 and SQ-15.
/// Parser-level tests live in gql_sqlparser/SecurityRemediationTests.cs (R4_*).
/// </summary>
public sealed class Round4ParserGatewayTests
{
    private const string Tenant = "tenant_a";

    // =========================================================================
    // Helpers
    // =========================================================================

    private static ClaimsPrincipal CreateUser() =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.PrimarySid, "S-1-5-21-R4-USER"),
                new Claim("tenant_id", Tenant)
            ],
            "Test"));

    private static TableMetadata CreateTable(string tableName, string sourceType) => new()
    {
        Identifier = new TableIdentifier("default", "public", tableName),
        Table = new Table { TableName = tableName, SchemaName = "public", SourceName = string.Empty, SourceType = sourceType },
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "int" },
            new TableColumn { ColumnName = "name", DataType = "varchar" },
            new TableColumn { ColumnName = "region", DataType = "varchar" },
            new TableColumn { ColumnName = "tenant_id", DataType = "varchar" }
        ]
    };

    private static ITableMetadataRepository CreateRepository(params TableMetadata[] tables)
    {
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var id = ci.Arg<TableIdentifier>();
                foreach (var t in tables)
                {
                    if (t.Identifier.Equals(id))
                    {
                        return Task.FromResult<TableMetadata?>(t);
                    }
                }

                return Task.FromResult<TableMetadata?>(null);
            });
        return repo;
    }

    private static GatewayOptions CreateOptions(
        List<string>? additionalAllowedFunctions = null,
        DataSourceConnectionOptions? defaultConnection = null)
    {
        var connections = new Dictionary<string, DataSourceConnectionOptions>(StringComparer.OrdinalIgnoreCase);
        if (defaultConnection != null)
        {
            connections["default"] = defaultConnection;
        }

        return new GatewayOptions
        {
            WebSql = new WebSqlOptions
            {
                Enabled = true,
                DefaultMaxRows = 100,
                MaxAllowedRows = 500,
                AdditionalAllowedFunctions = additionalAllowedFunctions ?? []
            },
            DataSources = new SqlDataSourceOptions { Connections = connections }
        };
    }

    private static GovernedSqlExecutionService CreateService(
        GatewayOptions options,
        ITableMetadataRepository repository,
        ISqlConnectionFactory? connectionFactory = null)
    {
        var consentRepository = Substitute.For<IConsentRepository>();
        consentRepository.GetActiveConsentsForSubjectsAsync(
                Arg.Any<IEnumerable<Sid>>(),
                Arg.Any<TableIdentifier>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<TenantId?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>(Array.Empty<Consent>()));

        var consentResolution = Substitute.For<IConsentResolutionService>();
        consentResolution.ResolveAccess(
                Arg.Any<Sid>(),
                Arg.Any<IReadOnlySet<Sid>>(),
                Arg.Any<IReadOnlySet<string>>(),
                Arg.Any<TableIdentifier>(),
                Arg.Any<IReadOnlyList<Consent>>(),
                Arg.Any<DatabaseDialect>())
            .Returns(ci => TableAccessDecision.Allowed(
                ci.ArgAt<TableIdentifier>(3),
                new Dictionary<string, ColumnAccessLevel>(),
                null,
                hasUnconstrainedColumnAllow: true));

        return new GovernedSqlExecutionService(
            Options.Create(options),
            policyEnforcement: null,
            consentResolution: consentResolution,
            tableRepository: repository,
            connectionFactory: connectionFactory,
            clientIpResolver: null,
            environment: null,
            logger: NullLogger<GovernedSqlExecutionService>.Instance,
            consentRepository: consentRepository,
            secretProvider: null);
    }

    private static GovernedSqlExecutionService CreatePostgreSqlService(List<string>? additionalAllowedFunctions = null) =>
        CreateService(CreateOptions(additionalAllowedFunctions), CreateRepository(CreateTable("employees", "PostgreSQL")));

    private static Task<string> RewriteAsync(GovernedSqlExecutionService service, string sql) =>
        service.RewriteSqlAsync(sql, CreateUser(), new TenantId(Tenant));

    // =========================================================================
    // P-01: Method call syntax
    // =========================================================================

    [Theory]
    [InlineData("SELECT (name).f(1) FROM employees")]
    [InlineData("SELECT name[1].f() FROM employees")]
    [InlineData("SELECT mytype::f(1) FROM employees")]
    public async Task P01_MethodCallSyntax_IsRejected(string sql)
    {
        var service = CreatePostgreSqlService();

        await Should.ThrowAsync<WebSqlPolicyException>(() => RewriteAsync(service, sql));
    }

    // =========================================================================
    // SQ-06 / P-02: Function allowlist per dialect
    // =========================================================================

    [Fact]
    public async Task SQ06_AnalyticsQuery_WithAllowlistedFunctions_IsRewritten()
    {
        var service = CreatePostgreSqlService();

        var secured = await RewriteAsync(
            service,
            "SELECT region, count(*), max(id), round(avg(id), 2), lower(name), coalesce(region, 'n/a'), " +
            "row_number() OVER (ORDER BY id) FROM employees GROUP BY region, name, id");

        secured.ShouldContain("tenant_id = 'tenant_a'");
        secured.ShouldContain("count(*)");
    }

    [Theory]
    [InlineData("ts_stat")]
    [InlineData("setval")]
    [InlineData("load_extension")]
    [InlineData("SESSION_CONTEXT")]
    [InlineData("pg_logical_slot_get_changes")]
    [InlineData("has_table_privilege")]
    public async Task SQ06_SensitiveFunctions_AreRejected(string functionName)
    {
        var service = CreatePostgreSqlService();

        await Should.ThrowAsync<WebSqlPolicyException>(() => RewriteAsync(service, $"SELECT {functionName}('x'), id FROM employees"));
    }

    [Fact]
    public async Task SQ06_FunctionOutsideDialectAllowlist_IsRejected_UnlessAdditionallyAllowed()
    {
        var strict = CreatePostgreSqlService();
        var ex = await Should.ThrowAsync<WebSqlPolicyException>(() => RewriteAsync(strict, "SELECT md5(name) FROM employees"));
        ex.Message.ShouldContain("md5");

        var extended = CreatePostgreSqlService(["md5"]);
        var secured = await RewriteAsync(extended, "SELECT md5(name) FROM employees");
        secured.ShouldContain("md5(name)");
    }

    [Fact]
    public async Task SQ06_DenylistBeatsAdditionalAllowedFunctions()
    {
        var service = CreatePostgreSqlService(["ts_stat", "setval", "load_extension"]);

        await Should.ThrowAsync<WebSqlPolicyException>(() => RewriteAsync(service, "SELECT ts_stat('SELECT 1'), id FROM employees"));
        await Should.ThrowAsync<WebSqlPolicyException>(() => RewriteAsync(service, "SELECT setval('s', 1), id FROM employees"));
        await Should.ThrowAsync<WebSqlPolicyException>(() => RewriteAsync(service, "SELECT load_extension('x'), id FROM employees"));
    }

    [Fact]
    public async Task SQ06_AllowlistDependsOnTargetDialect()
    {
        var sqlServer = CreateService(CreateOptions(), CreateRepository(CreateTable("orders", "SqlServer")));
        var secured = await RewriteAsync(sqlServer, "SELECT len(name), isnull(region, 'n/a') FROM orders");
        secured.ShouldContain("len(name)");

        var postgres = CreateService(CreateOptions(), CreateRepository(CreateTable("orders", "PostgreSQL")));
        await Should.ThrowAsync<WebSqlPolicyException>(() => RewriteAsync(postgres, "SELECT len(name) FROM orders"));
    }

    // =========================================================================
    // P-05: Dialect from the data source configuration
    // =========================================================================

    [Fact]
    public async Task P05_TablesWithDifferentDialects_AreRejected()
    {
        var service = CreateService(
            CreateOptions(),
            CreateRepository(CreateTable("employees", "PostgreSQL"), CreateTable("orders", "SqlServer")));

        await Should.ThrowAsync<WebSqlPolicyException>(() =>
            RewriteAsync(service, "SELECT e.id, o.id FROM employees e JOIN orders o ON e.id = o.id"));
    }

    [Theory]
    [InlineData("Oracle")]
    [InlineData("Databricks")]
    public async Task P05_UnsupportedTableDialect_IsRejected(string sourceType)
    {
        var service = CreateService(CreateOptions(), CreateRepository(CreateTable("orders", sourceType)));

        var ex = await Should.ThrowAsync<WebSqlPolicyException>(() => RewriteAsync(service, "SELECT id FROM orders"));
        ex.Message.ShouldContain("only supports");
    }

    [Fact]
    public async Task P05_TableDialectMustMatchConnectionProvider()
    {
        var options = CreateOptions(defaultConnection: new DataSourceConnectionOptions { Provider = "SqlServer", ConnectionString = "Server=unused" });
        var service = CreateService(options, CreateRepository(CreateTable("orders", "PostgreSQL")));

        await Should.ThrowAsync<WebSqlPolicyException>(() => RewriteAsync(service, "SELECT id FROM orders"));
    }

    [Fact]
    public async Task P05_UnknownConnectionProvider_IsRejected()
    {
        var options = CreateOptions(defaultConnection: new DataSourceConnectionOptions { Provider = "Oracle", ConnectionString = "Data Source=unused" });
        var service = CreateService(options, CreateRepository(CreateTable("orders", "PostgreSQL")));

        var ex = await Should.ThrowAsync<WebSqlPolicyException>(() => RewriteAsync(service, "SELECT id FROM orders"));
        ex.Message.ShouldContain("provider");
    }

    [Fact]
    public async Task P05_ConnectionProviderDeterminesRewriteDialect()
    {
        var options = CreateOptions(defaultConnection: new DataSourceConnectionOptions { Provider = "SqlServer", ConnectionString = "Server=unused" });
        var service = CreateService(options, CreateRepository(CreateTable("orders", "SqlServer")));

        var secured = await RewriteAsync(service, "SELECT id FROM orders LIMIT 5");

        secured.ShouldContain("OFFSET 0 ROWS FETCH NEXT 5 ROWS ONLY");
        secured.ShouldNotContain("LIMIT");
    }

    [Fact]
    public void P05_ProviderMapping_AndSessionInitialization()
    {
        GovernedSqlExecutionService.TryMapProviderToDialect("PostgreSql", out var pg).ShouldBeTrue();
        pg.ShouldBe(DatabaseDialect.PostgreSql);
        GovernedSqlExecutionService.TryMapProviderToDialect("npgsql", out _).ShouldBeTrue();
        GovernedSqlExecutionService.TryMapProviderToDialect("mssql", out var mssql).ShouldBeTrue();
        mssql.ShouldBe(DatabaseDialect.SqlServer);
        GovernedSqlExecutionService.TryMapProviderToDialect("Sqlite", out var sqlite).ShouldBeTrue();
        sqlite.ShouldBe(DatabaseDialect.Sqlite);
        GovernedSqlExecutionService.TryMapProviderToDialect("Oracle", out _).ShouldBeFalse();

    }

    [Fact]
    public async Task P05_PostgreSqlConnection_ExecutesStandardConformingStringsBeforeTheQuery()
    {
        // The factory hands out a SQLite connection although the data source is configured as PostgreSQL: the first
        // statement sent must be the session initialization, which SQLite rejects with a syntax error near "SET".
        var factory = Substitute.For<ISqlConnectionFactory>();
        factory.CreateOpenConnectionAsync(Arg.Any<DataSourceConnectionOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ => OpenSqliteAsync("Data Source=:memory:"));
        var options = CreateOptions(defaultConnection: new DataSourceConnectionOptions { Provider = "PostgreSql", ConnectionString = "Host=unused" });
        var service = CreateService(options, CreateRepository(CreateTable("employees", "PostgreSQL")), factory);

        var ex = await Should.ThrowAsync<SqliteException>(() =>
            service.ExecuteQueryBufferedAsync(new GovernedSqlQueryRequest("SELECT id FROM employees"), CreateUser(), new TenantId(Tenant)));

        ex.Message.ShouldContain("SET");
    }

    [Fact]
    public async Task P05_SqliteConnection_ExecutesWithoutSessionInitialization()
    {
        var connectionString = $"Data Source=r4_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        await using var keepAlive = new SqliteConnection(connectionString);
        await keepAlive.OpenAsync();
        await using (var setup = keepAlive.CreateCommand())
        {
            setup.CommandText =
                "CREATE TABLE orders (id INTEGER, name TEXT, region TEXT, tenant_id TEXT);" +
                "INSERT INTO orders VALUES (1, 'a', 'eu', 'tenant_a'), (2, 'b', 'us', 'tenant_b');";
            await setup.ExecuteNonQueryAsync();
        }

        var factory = Substitute.For<ISqlConnectionFactory>();
        factory.CreateOpenConnectionAsync(Arg.Any<DataSourceConnectionOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ => OpenSqliteAsync(connectionString));
        var options = CreateOptions(defaultConnection: new DataSourceConnectionOptions { Provider = "Sqlite", ConnectionString = connectionString });
        var service = CreateService(options, CreateRepository(CreateTable("orders", "Sqlite")), factory);

        var result = await service.ExecuteQueryBufferedAsync(new GovernedSqlQueryRequest("SELECT id FROM orders"), CreateUser(), new TenantId(Tenant));

        result.RowCount.ShouldBe(1);
    }

    private static async Task<DbConnection> OpenSqliteAsync(string connectionString)
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        return connection;
    }

    // =========================================================================
    // P-06 / SQ-11 / SQ-13: strict token switches in the gateway
    // =========================================================================

    [Theory]
    [InlineData("SELECT * FROM \"public.employees\"")]
    [InlineData("SELECT id FROM employees FOR VERSION AS OF 1")]
    [InlineData("SELECT id FROM employees FOR TIMESTAMP AS OF TIMESTAMP '2026-01-01 00:00:00'")]
    [InlineData("SELECT id FROM employees -- comment")]
    public async Task P06_DottedIdentifiersTimeTravelAndComments_AreRejected(string sql)
    {
        var service = CreatePostgreSqlService();

        await Should.ThrowAsync<ArgumentException>(() => RewriteAsync(service, sql));
    }

    [Fact]
    public async Task P06_DollarQuoting_IsRejectedForSqliteTargets()
    {
        var service = CreateService(CreateOptions(), CreateRepository(CreateTable("orders", "Sqlite")));

        await Should.ThrowAsync<ArgumentException>(() => RewriteAsync(service, "SELECT $$x$$, id FROM orders"));
    }

    // =========================================================================
    // SQ-15: unlimited DML affected rows is a DANGER entry
    // =========================================================================

    [Fact]
    public void SQ15_UnlimitedAffectedRowsWithDml_IsReportedAsDanger()
    {
        var unlimited = new GatewayOptions { WebSql = new WebSqlOptions { AllowDml = true, MaxAffectedRows = 0 } };

        var entry = unlimited.GetActiveDangerBypasses().ShouldHaveSingleItem();
        entry.ShouldStartWith(GatewayOptions.DangerPrefix);
        entry.ShouldContain("MaxAffectedRows");
        unlimited.HasAnyDangerBypassActive.ShouldBeTrue();
    }

    [Fact]
    public void SQ15_LimitedAffectedRowsOrNoDml_IsNotReported()
    {
        new GatewayOptions { WebSql = new WebSqlOptions { AllowDml = true, MaxAffectedRows = 1000 } }
            .GetActiveDangerBypasses().ShouldBeEmpty();
        new GatewayOptions { WebSql = new WebSqlOptions { AllowDml = false, MaxAffectedRows = 0 } }
            .GetActiveDangerBypasses().ShouldBeEmpty();
    }
}
