using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Oracle.ManagedDataAccess.Client;
using Shouldly;
using Testcontainers.Oracle;
using TrinoSqlEngine;
using Xunit;

namespace Autheris.Tests.Integration;

public sealed class OracleContainerFixture : IAsyncLifetime
{
    public OracleContainer Container { get; } = new OracleBuilder("gvenzl/oracle-free:23-slim-faststart@sha256:d86d09794ae138a8951e97d7ee010778dcd43088e8d0f83a51002abab8c6d7fd")
        .WithPassword("Oracle_Password_2026")
        .Build();

    public string ConnectionString => Container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await Container.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await Container.DisposeAsync();
    }
}

public class OracleIntegrationTests : IClassFixture<OracleContainerFixture>
{
    private readonly string _connectionString;

    public OracleIntegrationTests(OracleContainerFixture fixture)
    {
        _connectionString = fixture.ConnectionString;
    }

    [Fact]
    public async Task CanConnect_CreateTable_AndQueryData_OnOracleContainer()
    {
        await using var connection = new OracleConnection(_connectionString);
        await connection.OpenAsync();

        // 1. Tabelle anlegen
        await using (var dropCmd = connection.CreateCommand())
        {
            dropCmd.CommandText = "DROP TABLE IF EXISTS integration_tenants";
            await dropCmd.ExecuteNonQueryAsync();
        }

        await using (var createCmd = connection.CreateCommand())
        {
            createCmd.CommandText = """
                CREATE TABLE integration_tenants (
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
                INSERT INTO integration_tenants (tenant_key, display_name, is_active)
                VALUES (:p1, :p2, :p3)
                """;
            insertCmd.Parameters.Add(new OracleParameter("p1", "tenant-ora-01"));
            insertCmd.Parameters.Add(new OracleParameter("p2", "Oracle Tenant Alpha"));
            insertCmd.Parameters.Add(new OracleParameter("p3", 1));

            await insertCmd.ExecuteNonQueryAsync();
        }

        // 3. Abfragen und verifizieren
        await using (var queryCmd = connection.CreateCommand())
        {
            queryCmd.CommandText = "SELECT tenant_key, display_name, is_active FROM integration_tenants WHERE tenant_key = :p1";
            queryCmd.Parameters.Add(new OracleParameter("p1", "tenant-ora-01"));

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
        await using var connection = new OracleConnection(_connectionString);
        await connection.OpenAsync();

        // 1. Tabelle vorbereiten
        await using (var dropCmd = connection.CreateCommand())
        {
            dropCmd.CommandText = "DROP TABLE IF EXISTS orders";
            await dropCmd.ExecuteNonQueryAsync();
        }

        await using (var createCmd = connection.CreateCommand())
        {
            createCmd.CommandText = """
                CREATE TABLE orders (
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
            "INSERT INTO orders (tenant_id, customer_name, total_amount) VALUES ('tenant-alpha', 'Oracle Customer 1', 120.00)",
            "INSERT INTO orders (tenant_id, customer_name, total_amount) VALUES ('tenant-alpha', 'Oracle Customer 2', 240.50)",
            "INSERT INTO orders (tenant_id, customer_name, total_amount) VALUES ('tenant-alpha', 'Oracle Customer 3', 360.75)",
            // 2 Datensätze für tenant-beta (sollen durch RLS ausgeblendet werden)
            "INSERT INTO orders (tenant_id, customer_name, total_amount) VALUES ('tenant-beta', 'Oracle Beta Customer', 999.99)",
            "INSERT INTO orders (tenant_id, customer_name, total_amount) VALUES ('tenant-beta', 'Oracle Beta Customer 2', 888.88)"
        };

        foreach (var sql in seedSqls)
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync();
        }

        // 2. RLS & AST Compiler konfigurieren
        var policyProvider = new DefaultRlsPolicyProvider("tenant_id = 'tenant-alpha'");
        var rlsOptions = new RlsOptions
        {
            TargetDialect = TargetSqlDialect.Oracle,
            PolicyProvider = policyProvider
        };

        var engine = new FastSqlEngine();

        // Quellabfrage in ANSI/Trino-Syntax mit Paginierung
        string sourceSql = "SELECT id, customer_name, total_amount FROM orders ORDER BY id OFFSET 1 LIMIT 2";

        // 3. Durch den neuen AST-Compiler für Oracle kompilieren
        string compiledOracleSql = engine.GenerateGovernedSql(sourceSql, rlsOptions);

        // Verifizieren der Oracle-Spezifika:
        // - Doppelte Anführungszeichen mit Großschreibung ("ORDERS", "ID")
        // - RLS-Prädikat ("TENANT_ID" = 'tenant-alpha')
        // - Subquery-Alias OHNE 'AS'-Schlüsselwort: ) "ORDERS"
        // - Oracle 12c+ Paginierung (OFFSET 1 ROWS FETCH NEXT 2 ROWS ONLY)
        compiledOracleSql.ShouldContain("\"ORDERS\"");
        compiledOracleSql.ShouldContain("\"TENANT_ID\" = 'tenant-alpha'");
        compiledOracleSql.ShouldNotContain(") AS \"ORDERS\"");
        compiledOracleSql.ShouldContain(") \"ORDERS\"");
        compiledOracleSql.ShouldContain("OFFSET 1 ROWS FETCH NEXT 2 ROWS ONLY");

        // 4. Den generierten Oracle-SQL-String direkt auf dem echten Container ausführen
        await using (var queryCmd = connection.CreateCommand())
        {
            queryCmd.CommandText = compiledOracleSql;

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
