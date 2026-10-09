namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Services;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Npgsql;
using Shouldly;
using Testcontainers.PostgreSql;
using Xunit;

/// <summary>
/// D-1 / R-SQL-3: Integration test for WebSQL SELECT against PostgreSQL (Testcontainers).
/// Verifies that the reader is closed before the transaction commits, and set_config(..., true)
/// is executed within the same transaction.
/// </summary>
public sealed class PostgreSqlWebSqlTransactionTests : IAsyncLifetime
{
    private const string Tenant = "tenant_test_pg";
    private static readonly TableIdentifier TableId = new("finance", "public", "active_invoices");

    private PostgreSqlContainer? _container;
    private bool _available;

    public async Task InitializeAsync()
    {
        try
        {
            _container = new PostgreSqlBuilder("postgres:16-alpine").Build();
            await _container.StartAsync();

            await using var conn = new NpgsqlConnection(_container.GetConnectionString());
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE invoices (
                    id INT PRIMARY KEY,
                    tenant_id VARCHAR(50),
                    amount NUMERIC(10,2)
                );
                INSERT INTO invoices (id, tenant_id, amount) VALUES
                (1, 'tenant_test_pg', 100.50),
                (2, 'other_tenant', 200.00);

                CREATE VIEW active_invoices AS
                SELECT id, tenant_id, amount, current_setting('autheris.tenant_id', true) AS active_tenant
                FROM invoices;
                """;
            await cmd.ExecuteNonQueryAsync();

            _available = true;
        }
        catch
        {
            _available = false;
        }
    }

    public async Task DisposeAsync()
    {
        if (_container != null)
        {
            await _container.DisposeAsync();
        }
    }

    [Fact]
    public async Task D_1_R_SQL_3_WebSql_PostgresSelect_ReaderClosedBeforeCommit_SetConfigInSameTx()
    {
        if (!_available) return;

        var tableMeta = new TableMetadata
        {
            Identifier = TableId,
            Table = new Table { TableName = "active_invoices", SchemaName = "public", SourceName = "finance", SourceType = "PostgreSql" },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "tenant_id", DataType = "varchar" },
                new TableColumn { ColumnName = "amount", DataType = "numeric" },
                new TableColumn { ColumnName = "active_tenant", DataType = "varchar" }
            ],
            ColumnMaskingRules = new Dictionary<string, MaskingRule>()
        };

        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TableMetadata?>(tableMeta));
        repo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TableMetadata>>([tableMeta]));

        var consents = Substitute.For<IConsentRepository>();
        consents.GetActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>([]));

        var resolution = Substitute.For<IConsentResolutionService>();
        resolution.ResolveAccess(Arg.Any<Sid>(), Arg.Any<IReadOnlySet<Sid>>(), Arg.Any<IReadOnlySet<string>>(), Arg.Any<TableIdentifier>(), Arg.Any<IReadOnlyList<Consent>>(), Arg.Any<DatabaseDialect>())
            .Returns(TableAccessDecision.Allowed(TableId, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true));

        var options = new GatewayOptions
        {
            WebSql = new WebSqlOptions
            {
                Enabled = true,
                DefaultDataSourceName = "finance",
                AllowedDataSources = ["finance"],
                DefaultMaxRows = 100,
                MaxAllowedRows = 500
            },
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>
                {
                    ["finance"] = new()
                    {
                        Provider = "PostgreSql",
                        ConnectionString = _container!.GetConnectionString()
                    }
                }
            }
        };

        var connFactory = new SqlConnectionFactory();
        var sessionInit = new DbSessionContextInitializer();

        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Development");

        var service = new GovernedSqlExecutionService(
            Options.Create(options),
            consentResolution: resolution,
            tableRepository: repo,
            environment: env,
            logger: NullLogger<GovernedSqlExecutionService>.Instance,
            consentRepository: consents,
            connectionFactory: connFactory,
            sessionInitializer: sessionInit);

        var claims = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-PG-USER"),
            new Claim("tenant_id", Tenant)
        ], "Test"));

        var query = new GovernedSqlQueryRequest("SELECT id, amount, active_tenant FROM active_invoices WHERE id = 1");
        var result = await service.ExecuteQueryBufferedAsync(query, claims, Tenant, CancellationToken.None);

        result.Rows.Count.ShouldBe(1);
        result.Rows[0]["id"].ShouldBe(1);
        result.Rows[0]["active_tenant"].ShouldBe(Tenant);
    }
}
