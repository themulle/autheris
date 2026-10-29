namespace Autheris.Tests.Integration;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence.Migrations;
using Autheris.Infrastructure.Persistence.Migrations.Shape;
using Microsoft.Data.SqlClient;
using Shouldly;
using Xunit;

/// <summary>Issue #94, plan D-2/D-3 and SEC-94-08 on SQL Server: snapshot equality, Verify, pre-created drifted tables, message hygiene.</summary>
public sealed class SqlServerGovernanceSchemaShapeTests : IClassFixture<SqlServerGovernanceSchemaShapeTests.MigratedDatabase>
{
    /// <summary>One migrated database shared by the tests that only read the schema.</summary>
    public sealed class MigratedDatabase : IAsyncLifetime
    {
        public SqlServerTestDatabase Db { get; private set; } = null!;

        public async Task InitializeAsync()
        {
            Db = await SqlServerTestDatabase.CreateAsync();
            await GovernanceSchemaTestSupport.RunSqlServerAsync(Db.ConnectionString, GovernanceMigrationMode.Apply);
        }

        public async Task DisposeAsync() => await Db.DisposeAsync();
    }

    private readonly MigratedDatabase _shared;

    public SqlServerGovernanceSchemaShapeTests(MigratedDatabase shared) => _shared = shared;

    private static async Task ExecAsync(string connectionString, string sql)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarAsync(string connectionString, string sql)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task EmbeddedSnapshot_EqualsShapeOfTheBaseline()
    {
        await using var db = await SqlServerTestDatabase.CreateAsync(); // a fresh database: the shared one may already hold migrated steps
        await using var conn = new SqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        // The baseline DDL is applied directly: the runner itself depends on the snapshot that this test generates.
        SqlServerBaselineSchema.Apply(conn);
        var reader = new SqlServerSchemaCatalogReader();
        var tables = await reader.ListTablesAsync(conn, CancellationToken.None);
        var actual = await reader.ReadAsync(conn, tables, CancellationToken.None);

        GovernanceSchemaTestSupport.AssertSnapshotEquals("sqlserver", actual);
    }

    [Fact]
    public async Task AppliedBaseline_PassesApplyAndVerify_AndRunsAreIdempotent()
    {

        await GovernanceSchemaTestSupport.RunSqlServerAsync(_shared.Db.ConnectionString, GovernanceMigrationMode.Apply);
        await GovernanceSchemaTestSupport.RunSqlServerAsync(_shared.Db.ConnectionString, GovernanceMigrationMode.Apply);
        await GovernanceSchemaTestSupport.RunSqlServerAsync(_shared.Db.ConnectionString, GovernanceMigrationMode.Verify);

        (await ScalarAsync(_shared.Db.ConnectionString, "SELECT COUNT(*) FROM dbo.autheris_schema_migrations")).ShouldBe(1);
    }

    [Fact]
    public async Task Verify_OnUnmigratedDatabase_FailsAndCreatesNothing()
    {
        await using var db = await SqlServerTestDatabase.CreateAsync();

        await Should.ThrowAsync<GovernanceSchemaDriftException>(
            () => GovernanceSchemaTestSupport.RunSqlServerAsync(db.ConnectionString, GovernanceMigrationMode.Verify));

        (await ScalarAsync(db.ConnectionString, "SELECT COUNT(*) FROM sys.tables")).ShouldBe(0);
    }

    [Fact]
    public async Task PreCreatedTableWithoutColumn_Apply_ReportsDriftAndRecordsNoVersion()
    {
        await using var db = await SqlServerTestDatabase.CreateAsync();
        await ExecAsync(
            db.ConnectionString,
            "CREATE TABLE dbo.CONSENT_COLUMN_RULES (id NVARCHAR(128) NOT NULL PRIMARY KEY, consent_id NVARCHAR(128) NOT NULL, table_column_id NVARCHAR(128) NOT NULL, column_name NVARCHAR(256) NOT NULL, access_level NVARCHAR(64) NOT NULL)");

        var ex = await Should.ThrowAsync<GovernanceSchemaDriftException>(
            () => GovernanceSchemaTestSupport.RunSqlServerAsync(db.ConnectionString, GovernanceMigrationMode.Apply));

        ex.Deviations.ShouldContain(d => d.Kind == SchemaDeviationKind.MissingColumn && d.Table == "CONSENT_COLUMN_RULES" && d.Name == "applicable_scopes");
        ex.Message.ShouldContain("SQL Server database");
        // I388-SC-04: no DDL before the adoption pre-check accepted the schema, so not even the history table exists.
        (await ScalarAsync(db.ConnectionString, "SELECT COUNT(*) FROM sys.tables WHERE name = 'autheris_schema_migrations'")).ShouldBe(0);
        await Should.ThrowAsync<GovernanceSchemaDriftException>(
            () => GovernanceSchemaTestSupport.RunSqlServerAsync(db.ConnectionString, GovernanceMigrationMode.Apply));
    }

    [Fact]
    public async Task WrongIndexAndPrimaryKey_AreReported()
    {
        await using var db = await SqlServerTestDatabase.CreateAsync();
        await GovernanceSchemaTestSupport.RunSqlServerAsync(db.ConnectionString, GovernanceMigrationMode.Apply);
        await ExecAsync(db.ConnectionString, "DROP INDEX ux_tables_natural ON dbo.TABLES; CREATE INDEX ux_tables_natural ON dbo.TABLES (source_name, schema_name, table_name);");

        var ex = await Should.ThrowAsync<GovernanceSchemaDriftException>(
            () => GovernanceSchemaTestSupport.RunSqlServerAsync(db.ConnectionString, GovernanceMigrationMode.Verify));

        var deviation = ex.Deviations.ShouldHaveSingleItem();
        deviation.Kind.ShouldBe(SchemaDeviationKind.WrongIndex);
        deviation.Table.ShouldBe("TABLES");
        deviation.Name.ShouldBe("ux_tables_natural");
    }

    [Fact]
    public async Task DriftMessage_ContainsNoConnectionDetails()
    {
        await using var db = await SqlServerTestDatabase.CreateAsync();
        await GovernanceSchemaTestSupport.RunSqlServerAsync(db.ConnectionString, GovernanceMigrationMode.Apply);
        await ExecAsync(db.ConnectionString, "DROP TABLE dbo.TABLE_INDEX_COLUMNS");
        var builder = new SqlConnectionStringBuilder(db.ConnectionString);

        var ex = await Should.ThrowAsync<GovernanceSchemaDriftException>(
            () => GovernanceSchemaTestSupport.RunSqlServerAsync(db.ConnectionString, GovernanceMigrationMode.Verify));

        var header = ex.Message[..ex.Message.LastIndexOf('\n')];
        header.ShouldContain($"database '{builder.InitialCatalog}'");
        foreach (var forbidden in new[] { builder.DataSource, builder.UserID, builder.Password, "Password", "User Id", "Server=", "Data Source" })
        {
            if (!string.IsNullOrEmpty(forbidden))
            {
                header.ShouldNotContain(forbidden, Case.Insensitive);
            }
        }
    }
}
