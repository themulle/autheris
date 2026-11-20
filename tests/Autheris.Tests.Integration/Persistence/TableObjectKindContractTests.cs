namespace Autheris.Tests.Integration.Persistence;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Migrations;
using Autheris.Tests.Common;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;
using Shouldly;
using Xunit;

/// <summary>Issue #896: TABLES.object_kind (step 0030) defaults to table, round-trips a view and rejects a value outside the closed set.</summary>
public abstract class TableObjectKindContractBase
{
    protected abstract Task<string?> CreateDatabaseAsync();
    protected abstract Task DisposeDatabaseAsync();
    protected abstract IGovernanceRepository CreateRepository(string connectionString);
    protected abstract DbConnection OpenRaw(string connectionString);
    protected abstract Task MigrateAsync(string connectionString, int? stepVersionCap);
    protected abstract void AssertObjectKindCheckConstraint(DbException ex);

    private static TableMetadata Metadata(Guid id, DiscoveryObjectKind kind) => new()
    {
        Table = new Table { Id = id, DisplayName = "orders", TableName = "ord_896", SchemaName = "sales", SourceType = "Test", SourceName = "kind_896", Description = "object kind contract", ObjectKind = kind },
        Identifier = new TableIdentifier("kind_896", "sales", "ord_896"),
        Columns = [new TableColumn { ColumnName = "id", DataType = "varchar" }]
    };

    private static async Task<DiscoveryObjectKind> StoredAsync(IGovernanceRepository repo) =>
        (await repo.GetTableMetadataAsync(new TableIdentifier("kind_896", "sales", "ord_896")))!.Table.ObjectKind;

    private static async Task ExecAsync(DbConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Migration_ExistingRowsReadAsTable()
    {
        var cs = await CreateDatabaseAsync();
        if (cs is null)
        {
            return;
        }

        try
        {
            await MigrateAsync(cs, 29);
            await using (var raw = OpenRaw(cs))
            {
                await ExecAsync(raw, "INSERT INTO TABLES (id, source_type, source_name, schema_name, table_name, display_name, sensitivity, requires_four_eyes, is_active) VALUES ('3f2a1c0e-5b7d-4e21-9a64-0d8c1b2e7f90', 'Test', 'kind_896', 'sales', 'ord_896', 'orders', 'NORMAL', 0, 1)");
            }

            await MigrateAsync(cs, null);

            await using var repo = (IAsyncDisposable)CreateRepository(cs);
            (await StoredAsync((IGovernanceRepository)repo)).ShouldBe(DiscoveryObjectKind.Table);
        }
        finally
        {
            await DisposeDatabaseAsync();
        }
    }

    [Fact]
    public async Task Upsert_RoundTripsViewAndBackToTable()
    {
        var cs = await CreateDatabaseAsync();
        if (cs is null)
        {
            return;
        }

        try
        {
            await MigrateAsync(cs, null);
            var id = Guid.NewGuid();
            await using var repo = (IAsyncDisposable)CreateRepository(cs);
            var governance = (IGovernanceRepository)repo;
            await governance.UpsertTableMetadataAsync(Metadata(id, DiscoveryObjectKind.View));
            (await StoredAsync(governance)).ShouldBe(DiscoveryObjectKind.View);

            await governance.UpsertTableMetadataAsync(Metadata(id, DiscoveryObjectKind.Table));
            (await StoredAsync(governance)).ShouldBe(DiscoveryObjectKind.Table);
        }
        finally
        {
            await DisposeDatabaseAsync();
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task CheckConstraint_RejectsAValueOutsideTheClosedSet()
    {
        var cs = await CreateDatabaseAsync();
        if (cs is null)
        {
            return;
        }

        try
        {
            await MigrateAsync(cs, null);
            await using var raw = OpenRaw(cs);
            var badId = Guid.NewGuid().ToString("D");
            var act = () => ExecAsync(raw, $"INSERT INTO TABLES (id, source_type, source_name, schema_name, table_name, display_name, sensitivity, requires_four_eyes, is_active, object_kind) VALUES ('{badId}', 'Test', 'kind_896', 'sales', 'bad', 'bad', 'NORMAL', 0, 1, 'procedure')");
            var ex = await Should.ThrowAsync<DbException>(act);
            AssertObjectKindCheckConstraint(ex);
        }
        finally
        {
            await DisposeDatabaseAsync();
        }
    }
}

public sealed class PostgreSqlTableObjectKindContractTests : TableObjectKindContractBase
{
    private PostgreSqlTestDatabase? _db;

    protected override async Task<string?> CreateDatabaseAsync()
    {
        _db = await PostgreSqlTestDatabase.CreateAsync();
        return _db.IsAvailable ? _db.ConnectionString : null;
    }

    protected override async Task DisposeDatabaseAsync()
    {
        if (_db != null)
        {
            await _db.DisposeAsync();
        }
    }

    protected override IGovernanceRepository CreateRepository(string connectionString)
    {
        var options = Options.Create(new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions { Provider = "PostgreSql", ConnectionString = connectionString, SeedDemoData = false }
        });
        return GovernanceStoreFactory.CreatePostgreSql(new FakeEpochValidationService(), options, new StubHostEnvironment());
    }

    protected override DbConnection OpenRaw(string connectionString)
    {
        var conn = new NpgsqlConnection(connectionString);
        conn.Open();
        return conn;
    }

    protected override async Task MigrateAsync(string connectionString, int? stepVersionCap)
    {
        var options = GovernanceSchemaTestSupport.PostgreSqlOptions(connectionString, GovernanceMigrationMode.Apply);
        using var repository = GovernanceSchemaTestSupport.UnmigratedPostgreSql(options);
        await new PostgreSqlGovernanceMigrationRunner(repository, options) { StepVersionCapOverride = stepVersionCap }.RunAsync();
    }

    protected override void AssertObjectKindCheckConstraint(DbException ex)
    {
        var pg = ex.ShouldBeOfType<PostgresException>();
        pg.ConstraintName.ShouldBe("ck_tables_object_kind");
    }
}

public sealed class SqlServerTableObjectKindContractTests : TableObjectKindContractBase
{
    private SqlServerTestDatabase? _db;

    protected override async Task<string?> CreateDatabaseAsync()
    {
        _db = await SqlServerTestDatabase.CreateAsync();
        return _db.IsAvailable ? _db.ConnectionString : null;
    }

    protected override async Task DisposeDatabaseAsync()
    {
        if (_db != null)
        {
            await _db.DisposeAsync();
        }
    }

    protected override IGovernanceRepository CreateRepository(string connectionString)
    {
        var options = Options.Create(new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions { Provider = "SqlServer", ConnectionString = connectionString, SeedDemoData = false }
        });
        return GovernanceStoreFactory.CreateSqlServer(new FakeEpochValidationService(), options, new StubHostEnvironment());
    }

    protected override DbConnection OpenRaw(string connectionString)
    {
        var conn = new SqlConnection(connectionString);
        conn.Open();
        return conn;
    }

    protected override async Task MigrateAsync(string connectionString, int? stepVersionCap)
    {
        var options = GovernanceSchemaTestSupport.SqlServerOptions(connectionString, GovernanceMigrationMode.Apply);
        using var repository = GovernanceSchemaTestSupport.UnmigratedSqlServer(options);
        await new SqlServerGovernanceMigrationRunner(repository, options) { StepVersionCapOverride = stepVersionCap }.RunAsync();
    }

    protected override void AssertObjectKindCheckConstraint(DbException ex)
    {
        var sqlEx = ex.ShouldBeOfType<SqlException>();
        sqlEx.Message.ShouldContain("CK_TABLES_object_kind");
    }
}

file sealed class StubHostEnvironment : IHostEnvironment
{
    public string EnvironmentName { get; set; } = Environments.Development;
    public string ApplicationName { get; set; } = "Autheris.Tests";
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
