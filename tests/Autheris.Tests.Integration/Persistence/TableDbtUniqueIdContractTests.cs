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
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>Issue #666: TABLES.dbt_unique_id (step 0012) is nullable, never backfilled, written by the dbt sync and kept when an update carries none.</summary>
public abstract class TableDbtUniqueIdContractBase
{
    protected abstract Task<string?> CreateDatabaseAsync();
    protected abstract Task DisposeDatabaseAsync();
    protected abstract IGovernanceRepository CreateRepository(string connectionString);
    protected abstract DbConnection OpenRaw(string connectionString);
    protected abstract Task MigrateAsync(string connectionString, int? stepVersionCap);

    private static TableMetadata Metadata(Guid id, string? uniqueId) => new()
    {
        Table = new Table { Id = id, DisplayName = "orders", TableName = "ord_2024", SchemaName = "sales", SourceType = "Test", SourceName = "uid_666", Description = "dbt unique id contract", DbtUniqueId = uniqueId },
        Identifier = new TableIdentifier("uid_666", "sales", "ord_2024"),
        Columns = [new TableColumn { ColumnName = "id", DataType = "varchar" }]
    };

    private static async Task<string?> StoredAsync(IGovernanceRepository repo) =>
        (await repo.GetTableMetadataAsync(new TableIdentifier("uid_666", "sales", "ord_2024")))!.Table.DbtUniqueId;

    private static async Task ExecAsync(DbConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Migration_AddsNullableColumn_WithoutBackfill()
    {
        var cs = await CreateDatabaseAsync();
        if (cs is null)
        {
            return;
        }

        try
        {
            await MigrateAsync(cs, 11);
            await using (var raw = OpenRaw(cs))
            {
                await ExecAsync(raw, "INSERT INTO TABLES (id, source_type, source_name, schema_name, table_name, display_name, sensitivity, requires_four_eyes, is_active) VALUES ('t666', 'Test', 'uid_666', 'sales', 't_old', 't_old', 'Public', 0, 1)");
            }

            await MigrateAsync(cs, null);

            await using var after = OpenRaw(cs);
            await using var cmd = after.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM TABLES WHERE id = 't666' AND dbt_unique_id IS NULL";
            Convert.ToInt32(await cmd.ExecuteScalarAsync()).ShouldBe(1, "existing entries carry no unique id after the step");
        }
        finally
        {
            await DisposeDatabaseAsync();
        }
    }

    [Fact]
    public async Task Upsert_RecordsUniqueId_AndReadsItBack()
    {
        var cs = await CreateDatabaseAsync();
        if (cs is null)
        {
            return;
        }

        try
        {
            await MigrateAsync(cs, null);
            await using var repo = (IAsyncDisposable)CreateRepository(cs);
            await ((IGovernanceRepository)repo).UpsertTableMetadataAsync(Metadata(Guid.NewGuid(), "model.proj.orders"));

            await using var reader = (IAsyncDisposable)CreateRepository(cs);
            (await StoredAsync((IGovernanceRepository)reader)).ShouldBe("model.proj.orders");
        }
        finally
        {
            await DisposeDatabaseAsync();
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task AdminUpdate_KeepsRecordedUniqueId()
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
            await governance.UpsertTableMetadataAsync(Metadata(id, "model.proj.orders"));

            // an administrator edit builds the entry without a unique id; it must not clear the recorded one.
            await governance.UpsertTableMetadataAsync(Metadata(id, null));

            await using var reader = (IAsyncDisposable)CreateRepository(cs);
            (await StoredAsync((IGovernanceRepository)reader)).ShouldBe("model.proj.orders");
        }
        finally
        {
            await DisposeDatabaseAsync();
        }
    }
}

public sealed class PostgreSqlTableDbtUniqueIdContractTests : TableDbtUniqueIdContractBase
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
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Development");
        var options = Options.Create(new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions { Provider = "PostgreSql", ConnectionString = connectionString, SeedDemoData = false }
        });
        return GovernanceStoreFactory.CreatePostgreSql(Substitute.For<IEpochValidationService>(), options, env);
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
}

public sealed class SqlServerTableDbtUniqueIdContractTests : TableDbtUniqueIdContractBase
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
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Development");
        var options = Options.Create(new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions { Provider = "SqlServer", ConnectionString = connectionString, SeedDemoData = false }
        });
        return GovernanceStoreFactory.CreateSqlServer(Substitute.For<IEpochValidationService>(), options, env);
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
}
