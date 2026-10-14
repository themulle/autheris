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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Npgsql;
using Shouldly;
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

    private PostgreSqlTestDatabase? _db;
    private bool _available;

    public async Task InitializeAsync()
    {
        try
        {
            _db = await PostgreSqlTestDatabase.CreateAsync();
            if (!_db.IsAvailable)
            {
                return;
            }


            await using var conn = new NpgsqlConnection(_db.ConnectionString);
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
        catch (Exception ex)
        {
            _available = false;
            ContainerStartup.FailIfRequired(ex, "The PostgreSQL test container");
        }
    }

    public async Task DisposeAsync()
    {
        if (_db != null)
        {
            await _db.DisposeAsync();
        }
    }

    [Fact]
    public async Task D_1_R_SQL_3_WebSql_PostgresSelect_ReaderClosedBeforeCommit_SetConfigInSameTx()
    {
        if (!_available) return;

        // X1: through the real host (catalog reader, session pin, compiler, binder). The view reads the transaction-local tenant setting
        // that the session initializer sets with set_config(..., true), so the row only shows the tenant when the statement runs in the
        // session transaction.
        using var host = new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Gateway:Insecure:danger_allow_anonymous_access", "true");
            builder.UseSetting("Gateway:Insecure:danger_bypass_consent_checks", "true");
            builder.UseSetting("Gateway:GovernanceDb:Provider", "Sqlite");
            builder.UseSetting("Gateway:GovernanceDb:ConnectionString", $"Data Source=gov-pgtx-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
            builder.UseSetting("Gateway:WebSql:Enabled", "true");
            builder.UseSetting("Gateway:WebSql:DefaultDataSourceName", "finance");
            builder.UseSetting("Gateway:DataSources:Connections:finance:Provider", "PostgreSql");
            builder.UseSetting("Gateway:DataSources:Connections:finance:ConnectionString", _db!.ConnectionString);
            builder.UseSetting("Gateway:DataSources:Connections:finance:DefaultSchema", "public");
        });

        using (var scope = host.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<ITableMetadataRepository>();
            await repo.UpsertTableMetadataAsync(new TableMetadata
            {
                Table = new Table { Id = Guid.NewGuid(), DisplayName = "active_invoices", TableName = "active_invoices", SchemaName = "public", SourceName = "finance", SourceType = "PostgreSql" },
                Identifier = TableId,
                Columns =
                [
                    new TableColumn { ColumnName = "id", DataType = "int" },
                    new TableColumn { ColumnName = "tenant_id", DataType = "varchar" },
                    new TableColumn { ColumnName = "amount", DataType = "numeric" },
                    new TableColumn { ColumnName = "active_tenant", DataType = "varchar" }
                ]
            });
        }

        var claims = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-PG-USER"),
            new Claim("tenant_id", Tenant)
        ], "Test"));

        using var executionScope = host.Services.CreateScope();
        var service = executionScope.ServiceProvider.GetRequiredService<IGovernedSqlExecutionService>();
        var result = await service.ExecuteQueryBufferedAsync(
            new GovernedSqlQueryRequest("SELECT id, amount, active_tenant FROM public.active_invoices WHERE id = 1"), claims, Tenant, CancellationToken.None);

        result.Rows.Count.ShouldBe(1);
        result.Rows[0]["id"].ShouldBe(1);
        result.Rows[0]["active_tenant"].ShouldBe(Tenant);
    }
}
