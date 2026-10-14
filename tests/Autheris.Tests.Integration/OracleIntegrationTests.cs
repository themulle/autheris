using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Data.Common;
using Autheris.Application.Interfaces;
using Autheris.Domain.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using TrinoSqlEngine;
using Xunit;

namespace Autheris.Tests.Integration;

/// <summary>
/// WP-X0z: the connection comes from the real host container (pinned, BindByName wrapper); tables are created in the application schema
/// under names that no other Oracle test uses, because the Oracle container is shared.
/// </summary>
[Collection(OracleCollection.Name)]
public class OracleIntegrationTests : IDisposable
{
    private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _host;
    private readonly ISqlConnectionFactory _connections;
    private readonly DataSourceConnectionOptions _options;

    public OracleIntegrationTests(AstCompilerOracleFixture fixture)
    {
        _host = OracleGatewayHost.Create(fixture, nameof(OracleIntegrationTests));
        _connections = _host.Services.GetRequiredService<ISqlConnectionFactory>();
        _options = _host.Services.GetRequiredService<IOptions<GatewayOptions>>().Value.DataSources!.Connections![OracleGatewayHost.Source];
    }

    public void Dispose() => _host.Dispose();

    private Task<DbConnection> OpenAsync() => _connections.CreateOpenConnectionAsync(_options);

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var p = command.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        command.Parameters.Add(p);
    }

    [Fact]
    public async Task CanConnect_CreateTable_AndQueryData_OnOracleContainer()
    {
        await using var connection = await OpenAsync();

        // 1. Tabelle anlegen
        await using (var dropCmd = connection.CreateCommand())
        {
            dropCmd.CommandText = "DROP TABLE IF EXISTS x0z_integration_tenants";
            await dropCmd.ExecuteNonQueryAsync();
        }

        await using (var createCmd = connection.CreateCommand())
        {
            createCmd.CommandText = """
                CREATE TABLE x0z_integration_tenants (
                    id NUMBER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                    tenant_key VARCHAR2(50) NOT NULL,
                    display_name VARCHAR2(100) NOT NULL,
                    is_active NUMBER(1) DEFAULT 1 NOT NULL
                )
                """;
            await createCmd.ExecuteNonQueryAsync();
        }

        // 2. Datensatz einfügen
        await using (var insertCmd = connection.CreateCommand())
        {
            insertCmd.CommandText = """
                INSERT INTO x0z_integration_tenants (tenant_key, display_name, is_active)
                VALUES (:p1, :p2, :p3)
                """;
            AddParameter(insertCmd, "p1", "tenant-ora-01");
            AddParameter(insertCmd, "p2", "Oracle Tenant Alpha");
            AddParameter(insertCmd, "p3", 1);

            await insertCmd.ExecuteNonQueryAsync();
        }

        // 3. Abfragen und verifizieren
        await using (var queryCmd = connection.CreateCommand())
        {
            queryCmd.CommandText = "SELECT tenant_key, display_name, is_active FROM x0z_integration_tenants WHERE tenant_key = :p1";
            AddParameter(queryCmd, "p1", "tenant-ora-01");

            await using var reader = await queryCmd.ExecuteReaderAsync();
            (await reader.ReadAsync()).ShouldBeTrue();

            reader.GetString(0).ShouldBe("tenant-ora-01");
            reader.GetString(1).ShouldBe("Oracle Tenant Alpha");
            Convert.ToInt32(reader.GetValue(2)).ShouldBe(1);
        }
    }

    [Fact]
    public async Task AstCompiler_GeneratedOracleQuery_ExecutesSuccessfully_WithRlsAndPagination()
    {
        await using var connection = await OpenAsync();

        // 1. Tabelle vorbereiten
        await using (var dropCmd = connection.CreateCommand())
        {
            dropCmd.CommandText = "DROP TABLE IF EXISTS x0z_legacy_orders";
            await dropCmd.ExecuteNonQueryAsync();
        }

        await using (var createCmd = connection.CreateCommand())
        {
            createCmd.CommandText = """
                CREATE TABLE x0z_legacy_orders (
                    id NUMBER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                    tenant_id VARCHAR2(50) NOT NULL,
                    customer_name VARCHAR2(100) NOT NULL,
                    total_amount NUMBER(18,2) NOT NULL
                )
                """;
            await createCmd.ExecuteNonQueryAsync();
        }

        // 3 Datensätze für tenant-alpha
        string[] seedSqls = new[]
        {
            "INSERT INTO x0z_legacy_orders (tenant_id, customer_name, total_amount) VALUES ('tenant-alpha', 'Oracle Customer 1', 120.00)",
            "INSERT INTO x0z_legacy_orders (tenant_id, customer_name, total_amount) VALUES ('tenant-alpha', 'Oracle Customer 2', 240.50)",
            "INSERT INTO x0z_legacy_orders (tenant_id, customer_name, total_amount) VALUES ('tenant-alpha', 'Oracle Customer 3', 360.75)",
            // 2 Datensätze für tenant-beta (sollen durch RLS ausgeblendet werden)
            "INSERT INTO x0z_legacy_orders (tenant_id, customer_name, total_amount) VALUES ('tenant-beta', 'Oracle Beta Customer', 999.99)",
            "INSERT INTO x0z_legacy_orders (tenant_id, customer_name, total_amount) VALUES ('tenant-beta', 'Oracle Beta Customer 2', 888.88)"
        };

        foreach (var sql in seedSqls)
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync();
        }

        // 2. Typed governed compile (tenant binding over a one-table catalog) for Oracle
        string sourceSql = "SELECT id, customer_name, total_amount FROM x0z_legacy_orders ORDER BY id OFFSET 1 LIMIT 2";
        var compiled = SingleTableGovernedCompile.Compile(
            TargetSqlDialect.Oracle, AstCompilerOracleFixture.AppUser, "X0Z_LEGACY_ORDERS",
            [("ID", "NUMBER(10)"), ("TENANT_ID", "VARCHAR2(50)"), ("CUSTOMER_NAME", "VARCHAR2(100)"), ("TOTAL_AMOUNT", "NUMBER(18,2)")],
            "TENANT_ID", "tenant-alpha", sourceSql);

        // Oracle specifics: quoted upper-case names, no AS before a subquery alias, 12c pagination, the tenant value bound
        compiled.Sql.ShouldContain("\"X0Z_LEGACY_ORDERS\"");
        compiled.Sql.ShouldNotContain(") AS \"X0Z_LEGACY_ORDERS\"");
        compiled.Sql.ShouldContain("FETCH NEXT");
        compiled.Sql.ShouldNotContain("tenant-alpha");
        compiled.Parameters.ShouldContain(p => p.Origin == TrinoSqlEngine.Ast.Emit.ParameterOrigin.Tenant && Equals(p.Value, "tenant-alpha"));

        // 4. Den generierten Oracle-SQL-String direkt auf dem echten Container ausführen
        await using (var queryCmd = SingleTableGovernedCompile.BoundCommand(connection, compiled))
        {
            await using var reader = await queryCmd.ExecuteReaderAsync();
            var results = new List<(int Id, string Customer, decimal Amount)>();

            while (await reader.ReadAsync())
            {
                results.Add((
                    Convert.ToInt32(reader.GetValue(0)),
                    reader.GetString(1),
                    Convert.ToDecimal(reader.GetValue(2))
                ));
            }

            // Erwartung:
            // - Genau 2 Datensätze (LIMIT 2)
            // - Erster Datensatz übersprungen (OFFSET 1)
            // - Nur Datensätze von tenant-alpha
            results.Count.ShouldBe(2);
            results[0].Customer.ShouldBe("Oracle Customer 2");
            results[1].Customer.ShouldBe("Oracle Customer 3");
            results[0].Amount.ShouldBe(240.50m);
            results[1].Amount.ShouldBe(360.75m);
        }
    }
}
