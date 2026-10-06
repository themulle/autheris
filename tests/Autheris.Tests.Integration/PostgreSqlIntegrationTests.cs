using Npgsql;
using Shouldly;
using Testcontainers.PostgreSql;
using TrinoSqlEngine;
using Xunit;

namespace Autheris.Tests.Integration;

public sealed class PostgreSqlContainerFixture : IAsyncLifetime
{
    public PostgreSqlContainer Container { get; } = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("autheris_test")
        .WithUsername("postgres")
        .WithPassword("postgres_password_2026")
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

public class PostgreSqlIntegrationTests : IClassFixture<PostgreSqlContainerFixture>
{
    private readonly string _connectionString;

    public PostgreSqlIntegrationTests(PostgreSqlContainerFixture fixture)
    {
        _connectionString = fixture.ConnectionString;
    }

    [Fact]
    public async Task CanConnect_CreateTable_AndQueryData_OnPostgreSqlContainer()
    {
        await using var dataSource = NpgsqlDataSource.Create(_connectionString);
        await using var connection = await dataSource.OpenConnectionAsync();

        // 1. Tabelle anlegen
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                DROP TABLE IF EXISTS integration_tenants;
                CREATE TABLE integration_tenants (
                    id SERIAL PRIMARY KEY,
                    tenant_key VARCHAR(50) NOT NULL,
                    display_name VARCHAR(100) NOT NULL,
                    is_active BOOLEAN NOT NULL DEFAULT TRUE,
                    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
                );
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        // 2. Datensatz einfügen
        int insertedId;
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO integration_tenants (tenant_key, display_name, is_active)
                VALUES ($1, $2, $3)
                RETURNING id;
                """;
            cmd.Parameters.AddWithValue("tenant-pg-01");
            cmd.Parameters.AddWithValue("PostgreSQL Tenant Alpha");
            cmd.Parameters.AddWithValue(true);

            insertedId = (int)(await cmd.ExecuteScalarAsync())!;
        }

        insertedId.ShouldBeGreaterThan(0);

        // 3. Abfragen und verifizieren
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT tenant_key, display_name, is_active FROM integration_tenants WHERE id = $1;";
            cmd.Parameters.AddWithValue(insertedId);

            await using var reader = await cmd.ExecuteReaderAsync();
            (await reader.ReadAsync()).ShouldBeTrue();

            reader.GetString(0).ShouldBe("tenant-pg-01");
            reader.GetString(1).ShouldBe("PostgreSQL Tenant Alpha");
            reader.GetBoolean(2).ShouldBeTrue();
        }
    }

    [Fact]
    public async Task AstCompiler_GeneratedPostgreSqlQuery_ExecutesSuccessfully_WithRlsAndPagination()
    {
        await using var dataSource = NpgsqlDataSource.Create(_connectionString);
        await using var connection = await dataSource.OpenConnectionAsync();

        // 1. Tabelle vorbereiten
        await using (var setupCmd = connection.CreateCommand())
        {
            setupCmd.CommandText = """
                DROP TABLE IF EXISTS orders;
                CREATE TABLE orders (
                    id SERIAL PRIMARY KEY,
                    tenant_id VARCHAR(50) NOT NULL,
                    customer_name VARCHAR(100) NOT NULL,
                    total_amount NUMERIC(18,2) NOT NULL
                );

                -- 3 Datensätze für tenant-alpha
                INSERT INTO orders (tenant_id, customer_name, total_amount) VALUES
                    ('tenant-alpha', 'PG Customer 1', 120.00),
                    ('tenant-alpha', 'PG Customer 2', 240.50),
                    ('tenant-alpha', 'PG Customer 3', 360.75);

                -- 2 Datensätze für tenant-beta (sollen durch RLS ausgeblendet werden)
                INSERT INTO orders (tenant_id, customer_name, total_amount) VALUES
                    ('tenant-beta', 'PG Beta Customer', 999.99),
                    ('tenant-beta', 'PG Beta Customer 2', 888.88);
                """;
            await setupCmd.ExecuteNonQueryAsync();
        }

        // 2. RLS & AST Compiler konfigurieren
        var policyProvider = new DefaultRlsPolicyProvider("tenant_id = 'tenant-alpha'");
        var rlsOptions = new RlsOptions
        {
            TargetDialect = TargetSqlDialect.PostgreSql,
            PolicyProvider = policyProvider
        };

        var engine = new FastSqlEngine();

        // Quellabfrage in ANSI/Trino-Syntax mit Paginierung
        string sourceSql = "SELECT id, customer_name, total_amount FROM orders ORDER BY id OFFSET 1 LIMIT 2";

        // 3. Durch den neuen AST-Compiler für PostgreSQL kompilieren
        string compiledPgSql = engine.GenerateGovernedSql(sourceSql, rlsOptions);

        // Verifizieren der PostgreSQL-Spezifika (doppelte Anführungszeichen, LIMIT/OFFSET)
        compiledPgSql.ShouldContain("\"orders\"");
        compiledPgSql.ShouldContain("\"tenant_id\" = 'tenant-alpha'");
        compiledPgSql.ShouldContain("LIMIT 2 OFFSET 1");

        // 4. Den generierten PostgreSQL-SQL-String direkt auf dem echten Container ausführen
        await using (var queryCmd = connection.CreateCommand())
        {
            queryCmd.CommandText = compiledPgSql;

            await using var reader = await queryCmd.ExecuteReaderAsync();
            var results = new List<(int Id, string Customer, decimal Amount)>();

            while (await reader.ReadAsync())
            {
                results.Add((
                    reader.GetInt32(0),
                    reader.GetString(1),
                    reader.GetDecimal(2)
                ));
            }

            // Erwartung:
            // - Genau 2 Datensätze (LIMIT 2)
            // - Erster Datensatz übersprungen (OFFSET 1)
            // - Nur Datensätze von tenant-alpha
            results.Count.ShouldBe(2);
            results[0].Customer.ShouldBe("PG Customer 2");
            results[1].Customer.ShouldBe("PG Customer 3");
            results[0].Amount.ShouldBe(240.50m);
            results[1].Amount.ShouldBe(360.75m);
        }
    }
}
