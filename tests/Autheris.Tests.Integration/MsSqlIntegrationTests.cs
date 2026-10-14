using Microsoft.Data.SqlClient;
using Shouldly;
using TrinoSqlEngine;
using Xunit;

namespace Autheris.Tests.Integration;

public sealed class MsSqlContainerFixture : IAsyncLifetime
{
    private SqlServerTestDatabase? _db;

    public string ConnectionString => _db?.ConnectionString ?? string.Empty;

    public async Task InitializeAsync()
    {
        _db = await SqlServerTestDatabase.CreateAsync();
        if (!_db.IsAvailable)
        {
            throw new InvalidOperationException("The SQL Server test database is not available (Docker missing?).");
        }
    }

    public async Task DisposeAsync()
    {
        if (_db is not null)
        {
            await _db.DisposeAsync();
        }
    }
}

public class MsSqlIntegrationTests : IClassFixture<MsSqlContainerFixture>
{
    private readonly string _connectionString;

    public MsSqlIntegrationTests(MsSqlContainerFixture fixture)
    {
        _connectionString = fixture.ConnectionString;
    }



    [Fact]
    public async Task CanConnect_CreateTable_AndQueryData_OnMsSqlContainer()
    {
        var connectionStringBuilder = new SqlConnectionStringBuilder(_connectionString)
        {
            TrustServerCertificate = true
        };

        await using var connection = new SqlConnection(connectionStringBuilder.ConnectionString);
        await connection.OpenAsync();

        // 1. Tabelle erstellen
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                IF OBJECT_ID('dbo.IntegrationTenants', 'U') IS NOT NULL
                    DROP TABLE dbo.IntegrationTenants;

                CREATE TABLE dbo.IntegrationTenants (
                    Id INT IDENTITY(1,1) PRIMARY KEY,
                    TenantKey NVARCHAR(50) NOT NULL,
                    DisplayName NVARCHAR(100) NOT NULL,
                    IsActive BIT NOT NULL DEFAULT 1,
                    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
                );
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        // 2. Daten einfügen
        int insertedId;
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO dbo.IntegrationTenants (TenantKey, DisplayName, IsActive)
                OUTPUT INSERTED.Id
                VALUES (@tenantKey, @displayName, @isActive);
                """;
            cmd.Parameters.AddWithValue("@tenantKey", "tenant-eu-central");
            cmd.Parameters.AddWithValue("@displayName", "European Central Tenant");
            cmd.Parameters.AddWithValue("@isActive", true);

            insertedId = (int)(await cmd.ExecuteScalarAsync())!;
        }

        // 3. Verifizieren
        insertedId.ShouldBeGreaterThan(0);

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT TenantKey, DisplayName, IsActive FROM dbo.IntegrationTenants WHERE Id = @id;";
            cmd.Parameters.AddWithValue("@id", insertedId);

            await using var reader = await cmd.ExecuteReaderAsync();
            (await reader.ReadAsync()).ShouldBeTrue();

            reader.GetString(0).ShouldBe("tenant-eu-central");
            reader.GetString(1).ShouldBe("European Central Tenant");
            reader.GetBoolean(2).ShouldBeTrue();
        }
    }

    [Fact]
    public async Task AstCompiler_GeneratedSqlServerQuery_ExecutesSuccessfully_WithRlsAndPagination()
    {
        var connectionStringBuilder = new SqlConnectionStringBuilder(_connectionString)
        {
            TrustServerCertificate = true
        };

        await using var connection = new SqlConnection(connectionStringBuilder.ConnectionString);
        await connection.OpenAsync();

        // 1. Tabelle für E2E-Compiler-Test vorbereiten
        await using (var setupCmd = connection.CreateCommand())
        {
            setupCmd.CommandText = """
                IF OBJECT_ID('dbo.Orders', 'U') IS NOT NULL
                    DROP TABLE dbo.Orders;

                CREATE TABLE dbo.Orders (
                    Id INT IDENTITY(1,1) PRIMARY KEY,
                    TenantId NVARCHAR(50) NOT NULL,
                    CustomerName NVARCHAR(100) NOT NULL,
                    TotalAmount DECIMAL(18,2) NOT NULL
                );

                -- 3 Zeilen für tenant-alpha
                INSERT INTO dbo.Orders (TenantId, CustomerName, TotalAmount) VALUES
                    ('tenant-alpha', 'Alpha Customer 1', 100.50),
                    ('tenant-alpha', 'Alpha Customer 2', 200.00),
                    ('tenant-alpha', 'Alpha Customer 3', 350.75);

                -- 2 Zeilen für tenant-beta (sollen durch RLS komplett verborgen werden)
                INSERT INTO dbo.Orders (TenantId, CustomerName, TotalAmount) VALUES
                    ('tenant-beta', 'Beta Customer 1', 500.00),
                    ('tenant-beta', 'Beta Customer 2', 750.25);
                """;
            await setupCmd.ExecuteNonQueryAsync();
        }

        // 2. Typed governed compile (tenant binding over a one-table catalog) for SQL Server
        string sourceSql = "SELECT id, customername, totalamount FROM orders ORDER BY id OFFSET 1 LIMIT 2";
        var compiled = SingleTableGovernedCompile.Compile(
            TargetSqlDialect.SqlServer, "dbo", "Orders",
            [("Id", "int"), ("TenantId", "nvarchar(50)"), ("CustomerName", "nvarchar(100)"), ("TotalAmount", "decimal(18,2)")],
            "TenantId", "tenant-alpha", sourceSql);

        // T-SQL specifics: bracket quoting, OFFSET/FETCH, and the tenant value as a bound parameter
        compiled.Sql.ShouldContain("[Orders]");
        compiled.Sql.ShouldContain("OFFSET");
        compiled.Sql.ShouldContain("FETCH NEXT");
        compiled.Sql.ShouldNotContain("tenant-alpha");
        compiled.Parameters.ShouldContain(p => p.Origin == TrinoSqlEngine.Ast.Emit.ParameterOrigin.Tenant && Equals(p.Value, "tenant-alpha"));

        // 4. Den generierten T-SQL-String direkt auf dem echten SQL Server ausführen!
        await using (var queryCmd = SingleTableGovernedCompile.BoundCommand(connection, compiled))
        {
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
            // - Genau 2 Datensätze (wegen LIMIT 2)
            // - Datensatz 1 übersprungen (wegen OFFSET 1)
            // - Nur Datensätze von 'tenant-alpha' (Alpha Customer 2 & Alpha Customer 3)
            // - Keine Spur von 'tenant-beta'
            results.Count.ShouldBe(2);
            results[0].Customer.ShouldBe("Alpha Customer 2");
            results[1].Customer.ShouldBe("Alpha Customer 3");
            results[0].Amount.ShouldBe(200.00m);
            results[1].Amount.ShouldBe(350.75m);
        }
    }
}
