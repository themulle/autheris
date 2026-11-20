namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Tests.Common;
using Microsoft.Data.SqlClient;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// Issue #443 (WP-C): Benchmark comparing set-based vs correlated nested "latest per parent"
/// on SQL Server with >= 1,000,000 child rows and 140 parents. Excluded from default runs.
/// </summary>
[Trait("Category", "Benchmark")]
public sealed class Issue443LatestPerParentBenchmarkTests
{
    private static readonly TableIdentifier Devices = new("bench", "dbo", "devices");
    private static readonly TableIdentifier Readings = new("bench", "dbo", "readings");

    private readonly ITestOutputHelper _output;

    public Issue443LatestPerParentBenchmarkTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [RequiresContainerRuntimeFact]
    public async Task SqlServer_OneMillionChildRows_140Parents_SetBasedIsAtLeastTenTimesFasterThanCorrelated()
    {
        await using var db = await SqlServerTestDatabase.CreateAsync();
        if (!db.IsAvailable)
        {
            return;
        }

        await SeedOneMillionRowsAsync(db.ConnectionString);

        var access = CreateAccess();
        var root = new TreeQueryNode(Devices, ["device_id", "name"])
        {
            Relations =
            [
                new TreeRelationNode("readings", ["device_id"], ["device_id"], IsList: true, new TreeQueryNode(Readings, ["ts", "val"])
                {
                    OrderBy = [new TreeOrder("ts", Descending: true)],
                    Limit = 1
                })
            ],
            OrderBy = [new TreeOrder("device_id")],
            Limit = 140
        };

        var setBased = TreeSqlCompiler.Compile(root, access, DatabaseDialect.SqlServer);
        var correlated = TreeSqlCompiler.Compile(
            root,
            access,
            DatabaseDialect.SqlServer,
            maskingDisabled: false,
            options: new TreeCompileOptions { FailSetBasedFor = "readings" });

        setBased.Lists.ShouldHaveSingleItem().Strategy.ShouldBe(TreeListStrategy.SetBased);
        correlated.Lists.ShouldHaveSingleItem().Strategy.ShouldBe(TreeListStrategy.CorrelatedUnindexed);

        // Warmup once
        _ = await ExecuteScalarJsonAsync(db.ConnectionString, setBased);

        var swSetBased = Stopwatch.StartNew();
        var setBasedJson = await ExecuteScalarJsonAsync(db.ConnectionString, setBased);
        swSetBased.Stop();

        var swCorrelated = Stopwatch.StartNew();
        var correlatedJson = await ExecuteScalarJsonAsync(db.ConnectionString, correlated);
        swCorrelated.Stop();

        _output.WriteLine($"Set-based: {swSetBased.ElapsedMilliseconds} ms; Correlated: {swCorrelated.ElapsedMilliseconds} ms");

        using var setDoc = JsonDocument.Parse(setBasedJson);
        using var corrDoc = JsonDocument.Parse(correlatedJson);
        setDoc.RootElement.GetArrayLength().ShouldBe(140);
        corrDoc.RootElement.GetArrayLength().ShouldBe(140);

        swSetBased.ElapsedMilliseconds.ShouldBeLessThanOrEqualTo(2000);
        swCorrelated.Elapsed.TotalMilliseconds.ShouldBeGreaterThanOrEqualTo(swSetBased.Elapsed.TotalMilliseconds * 10.0);
    }

    private static Dictionary<TableIdentifier, TreeTableAccess> CreateAccess()
    {
        var devicesMeta = new TableMetadata
        {
            Identifier = Devices,
            Table = new Table { SourceName = "bench", SchemaName = "dbo", TableName = "devices", SourceType = "SqlServer" },
            PrimaryKeyColumns = ["device_id"],
            Columns =
            [
                new TableColumn { ColumnName = "device_id", DataType = "int" },
                new TableColumn { ColumnName = "name", DataType = "nvarchar" }
            ]
        };

        var readingsMeta = new TableMetadata
        {
            Identifier = Readings,
            Table = new Table { SourceName = "bench", SchemaName = "dbo", TableName = "readings", SourceType = "SqlServer" },
            PrimaryKeyColumns = ["ts", "device_id"],
            Columns =
            [
                new TableColumn { ColumnName = "ts", DataType = "int" },
                new TableColumn { ColumnName = "device_id", DataType = "int" },
                new TableColumn { ColumnName = "val", DataType = "int" }
            ]
        };

        return new Dictionary<TableIdentifier, TreeTableAccess>
        {
            [Devices] = new(devicesMeta, TableAccessDecision.Allowed(Devices, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true)),
            [Readings] = new(readingsMeta, TableAccessDecision.Allowed(Readings, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true))
        };
    }

    private static async Task SeedOneMillionRowsAsync(string connectionString)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 120;
        cmd.CommandText = """
            CREATE TABLE dbo.devices (
                device_id int NOT NULL PRIMARY KEY,
                name nvarchar(50) NOT NULL
            );
            CREATE TABLE dbo.readings (
                ts int NOT NULL,
                device_id int NOT NULL,
                val int NOT NULL,
                CONSTRAINT PK_readings PRIMARY KEY (ts, device_id)
            );

            WITH nums AS (
                SELECT TOP (140) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n
                FROM sys.all_objects a CROSS JOIN sys.all_objects b
            )
            INSERT INTO dbo.devices (device_id, name)
            SELECT n, CONCAT(N'device-', n) FROM nums;

            WITH ts_nums AS (
                SELECT TOP (1000000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS ts
                FROM sys.all_objects a CROSS JOIN sys.all_objects b
            )
            INSERT INTO dbo.readings (ts, device_id, val)
            SELECT
                t.ts,
                CASE
                    WHEN t.ts <= 50000 THEN ((t.ts - 1) % 140) + 1
                    ELSE 71 + ((t.ts - 1) % 70)
                END,
                t.ts
            FROM ts_nums t;
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<string> ExecuteScalarJsonAsync(string connectionString, CompiledTreeQuery compiled)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 120;
        cmd.CommandText = compiled.Sql;
        foreach (var (name, value) in compiled.Parameters)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        return Convert.ToString(await cmd.ExecuteScalarAsync())!;
    }
}
