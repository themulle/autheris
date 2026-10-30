namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Microsoft.Data.SqlClient;
using Shouldly;
using Xunit;

/// <summary>
/// Issue #390 (acceptance): on a real SQL Server a table whose key is declared <c>(device_id ASC, ts DESC)</c> is read newest-first per device
/// by the GraphQL tree compiler without any <c>orderBy</c>, and the same table without a declared direction reads ascending as before.
/// </summary>
public sealed class Issue390DescKeyReadTests
{
    private static readonly TableIdentifier Readings = new("dev", "dbo", "readings");

    private static TableMetadata Meta(IReadOnlyList<KeyColumnDirection> directions) => new()
    {
        Identifier = Readings,
        Table = new Table { SourceName = "dev", SchemaName = "dbo", TableName = "readings", SourceType = "SqlServer" },
        PrimaryKeyColumns = ["device_id", "ts"],
        PrimaryKeyDirections = directions,
        Columns =
        [
            new TableColumn { ColumnName = "device_id", DataType = "int" },
            new TableColumn { ColumnName = "ts", DataType = "datetime2" },
            new TableColumn { ColumnName = "value", DataType = "int" }
        ]
    };

    private static async Task<List<(int Device, int Seq)>> ReadAsync(string connectionString, TableMetadata meta, TreeQueryNode node)
    {
        var access = new TreeTableAccess(meta, TableAccessDecision.Allowed(
            meta.Identifier,
            meta.Columns.ToDictionary(c => c.ColumnName, _ => ColumnAccessLevel.Clear, StringComparer.OrdinalIgnoreCase),
            hasUnconstrainedColumnAllow: true));
        var compiled = TreeSqlCompiler.Compile(node, new Dictionary<TableIdentifier, TreeTableAccess> { [meta.Identifier] = access }, DatabaseDialect.SqlServer);

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = compiled.Sql;
        foreach (var (name, value) in compiled.Parameters)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        // The tree compiler returns one value: the finished JSON array (FOR JSON PATH on SQL Server).
        var json = Convert.ToString(await cmd.ExecuteScalarAsync());
        using var doc = System.Text.Json.JsonDocument.Parse(json!);
        return doc.RootElement.EnumerateArray()
            .Select(e => (e.GetProperty("device_id").GetInt32(), e.GetProperty("value").GetInt32()))
            .ToList();
    }

    [Fact]
    public async Task GraphQlRead_DescKey_ReturnsNewestFirstPerDevice()
    {
        await using var db = await SqlServerTestDatabase.CreateAsync();
        if (!db.IsAvailable)
        {
            return;
        }

        await using (var conn = new SqlConnection(db.ConnectionString))
        {
            await conn.OpenAsync();
            await using var create = conn.CreateCommand();
            create.CommandText = """
                CREATE TABLE dbo.readings (device_id int NOT NULL, ts datetime2 NOT NULL, value int NOT NULL, CONSTRAINT PK_readings PRIMARY KEY (device_id, ts DESC));
                INSERT dbo.readings VALUES
                    (2, '2026-01-01T00:00:00', 21), (1, '2026-01-02T00:00:00', 12), (1, '2026-01-01T00:00:00', 11),
                    (2, '2026-01-03T00:00:00', 23), (1, '2026-01-03T00:00:00', 13), (2, '2026-01-02T00:00:00', 22);
                """;
            await create.ExecuteNonQueryAsync();
        }

        var node = new TreeQueryNode(Readings, ["device_id", "ts", "value"]) { Limit = 100 };

        var newestFirst = await ReadAsync(db.ConnectionString, Meta([KeyColumnDirection.Ascending, KeyColumnDirection.Descending]), node);
        newestFirst.Select(r => r.Seq).ShouldBe([13, 12, 11, 23, 22, 21], "device_id ascending, ts descending, without any orderBy");

        var ascending = await ReadAsync(db.ConnectionString, Meta([]), node);
        ascending.Select(r => r.Seq).ShouldBe([11, 12, 13, 21, 22, 23], "an undeclared direction reads ascending as before");
    }
}
