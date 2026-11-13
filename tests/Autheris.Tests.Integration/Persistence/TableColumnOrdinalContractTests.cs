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

/// <summary>Issue #610 (WP-OI-CAT-6): TABLE_COLUMNS.ordinal gives catalog columns a stable registration order on both engines (step 0010).</summary>
public abstract class TableColumnOrdinalContractBase
{
    private static readonly string[] Registered = ["zeta", "alpha", "mid", "beta2"];

    protected abstract Task<string?> CreateDatabaseAsync();
    protected abstract Task DisposeDatabaseAsync();
    protected abstract IGovernanceRepository CreateRepository(string connectionString);
    protected abstract DbConnection OpenRaw(string connectionString);
    protected abstract Task MigrateAsync(string connectionString, int? stepVersionCap);

    /// <summary>Rewrites the physical row placement of TABLE_COLUMNS (heap reorder) after an update.</summary>
    protected abstract string ReorganizeSql { get; }

    private static TableMetadata Metadata(string tableName, IEnumerable<string> columns) => new()
    {
        Table = new Table { Id = Guid.NewGuid(), DisplayName = tableName, TableName = tableName, SchemaName = "main", SourceType = "Test", SourceName = "ordinal_610", Description = "ordinal contract" },
        Identifier = new TableIdentifier("ordinal_610", "main", tableName),
        Columns = columns.Select(c => new TableColumn { ColumnName = c, DataType = "varchar" }).ToList()
    };

    private static async Task ExecAsync(DbConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<List<string>> NamesAsync(IGovernanceRepository repo, string tableName) =>
        (await repo.GetTableMetadataAsync(new TableIdentifier("ordinal_610", "main", tableName)))!.Columns.Select(c => c.ColumnName).ToList();

    [Fact]
    public async Task Columns_ReturnedInRegistrationOrder_AfterUpdateAndVacuumFull()
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
            await ((IGovernanceRepository)repo).UpsertTableMetadataAsync(Metadata("t_order", Registered));

            await using (var raw = OpenRaw(cs))
            {
                await ExecAsync(raw, "UPDATE TABLE_COLUMNS SET description = 'touched'");
                await ExecAsync(raw, ReorganizeSql);
            }

            await using var reader = (IAsyncDisposable)CreateRepository(cs);
            (await NamesAsync((IGovernanceRepository)reader, "t_order")).ShouldBe(Registered);
        }
        finally
        {
            await DisposeDatabaseAsync();
        }
    }

    [Fact]
    public async Task NewColumn_AppendedAfterExisting()
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
            var governance = (IGovernanceRepository)repo;
            await governance.UpsertTableMetadataAsync(Metadata("t_append", ["zeta", "alpha"]));
            await governance.UpsertTableMetadataAsync(Metadata("t_append", ["zeta", "alpha", "aaa_new"]));

            await using var reader = (IAsyncDisposable)CreateRepository(cs);
            (await NamesAsync((IGovernanceRepository)reader, "t_append")).ShouldBe(["zeta", "alpha", "aaa_new"]);
        }
        finally
        {
            await DisposeDatabaseAsync();
        }
    }

    [Fact]
    public async Task Migration_BackfillsOrdinal_ForExistingRows()
    {
        var cs = await CreateDatabaseAsync();
        if (cs is null)
        {
            return;
        }

        try
        {
            await MigrateAsync(cs, 9);
            await using (var raw = OpenRaw(cs))
            {
                await ExecAsync(raw, "INSERT INTO TABLES (id, source_type, source_name, schema_name, table_name, display_name, sensitivity, requires_four_eyes, is_active) VALUES ('t610', 'Test', 'ordinal_610', 'main', 't_old', 't_old', 'Public', 0, 1)");
                foreach (var name in new[] { "c", "a", "b" })
                {
                    await ExecAsync(raw, $"INSERT INTO TABLE_COLUMNS (id, table_id, column_name, data_type, is_sensitive) VALUES ('col_{name}', 't610', '{name}', 'varchar', 0)");
                }
            }

            await MigrateAsync(cs, null);

            await using var after = OpenRaw(cs);
            await using var cmd = after.CreateCommand();
            cmd.CommandText = "SELECT column_name, ordinal FROM TABLE_COLUMNS WHERE table_id = 't610' ORDER BY ordinal";
            var rows = new List<(string Name, int Ordinal)>();
            await using (var r = await cmd.ExecuteReaderAsync())
            {
                while (await r.ReadAsync())
                {
                    rows.Add((r.GetString(0), Convert.ToInt32(r.GetValue(1))));
                }
            }

            rows.Select(x => x.Ordinal).ShouldBe([1, 2, 3], "every existing row is numbered per table");
            rows.Count.ShouldBe(3);
        }
        finally
        {
            await DisposeDatabaseAsync();
        }
    }

    [Fact]
    public async Task BatchRead_OrdersByTableThenOrdinal()
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
            var governance = (IGovernanceRepository)repo;
            await governance.UpsertTableMetadataAsync(Metadata("t_batch_1", ["zeta", "alpha", "mid"]));
            await governance.UpsertTableMetadataAsync(Metadata("t_batch_2", ["yankee", "bravo"]));

            await using var reader = (IAsyncDisposable)CreateRepository(cs);
            var all = await ((IGovernanceRepository)reader).GetAllTablesAsync();
            all.Single(t => t.Table.TableName == "t_batch_1").Columns.Select(c => c.ColumnName).ShouldBe(["zeta", "alpha", "mid"]);
            all.Single(t => t.Table.TableName == "t_batch_2").Columns.Select(c => c.ColumnName).ShouldBe(["yankee", "bravo"]);
        }
        finally
        {
            await DisposeDatabaseAsync();
        }
    }
}

public sealed class PostgreSqlTableColumnOrdinalContractTests : TableColumnOrdinalContractBase
{
    private PostgreSqlTestDatabase? _db;

    protected override string ReorganizeSql => "VACUUM FULL TABLE_COLUMNS";

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

public sealed class SqlServerTableColumnOrdinalContractTests : TableColumnOrdinalContractBase
{
    private SqlServerTestDatabase? _db;

    protected override string ReorganizeSql => "ALTER INDEX ALL ON dbo.TABLE_COLUMNS REBUILD";

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
