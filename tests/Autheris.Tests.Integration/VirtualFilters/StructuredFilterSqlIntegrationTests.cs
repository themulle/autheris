namespace Autheris.Tests.Integration.VirtualFilters;

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Common;
using Autheris.Tests.Unit.VirtualFilters;
using Microsoft.Extensions.Options;
using Npgsql;
using Shouldly;

/// <summary>PostgreSQL execution of structured virtual filter predicates against schema-qualified tables.</summary>
public sealed class StructuredFilterSqlIntegrationTests
{
    private static readonly VirtualFilter David = VirtualFilterTestData.DavidFilter();
    private static readonly FilterBinding Binding = new() { FilterName = David.Name };

    private static TableMetadata Air1(params string[] columns) => new()
    {
        Identifier = new TableIdentifier("lwetem_prod", "fms", "air1"),
        Table = new Table { SourceName = "lwetem_prod", SchemaName = "fms", TableName = "air1", SourceType = DatabaseDialect.PostgreSql.ToString() },
        Columns = (columns.Length == 0 ? ["id", "client_id", "ts"] : columns).Select(c => new TableColumn { ColumnName = c }).ToList()
    };

    private static string Build(VirtualFilter? filter = null, FilterBinding? binding = null) =>
        new StructuredFilterSqlBuilder(Options.Create(new GatewayOptions()))
            .Build(filter ?? David, binding ?? Binding, Air1(), DatabaseDialect.PostgreSql);

    private static async Task<NpgsqlConnection> SeedAsync(PostgreSqlTestDatabase db)
    {
        var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE SCHEMA conf;
            CREATE SCHEMA md;
            CREATE SCHEMA fms;
            CREATE TABLE conf.client (client_id BIGINT PRIMARY KEY, crane_serial_number TEXT);
            CREATE TABLE md.crane (serial_number TEXT PRIMARY KEY, is_delivered INTEGER, date_of_delivery TIMESTAMP);
            CREATE TABLE fms.air1 (id BIGINT PRIMARY KEY, client_id BIGINT, ts TIMESTAMP);
            INSERT INTO md.crane VALUES ('S1', 1, '2026-05-01 00:00:00'), ('S2', NULL, NULL), ('S3', NULL, '2026-06-01 00:00:00');
            INSERT INTO conf.client VALUES (1, 'S1'), (2, 'S2'), (3, 'S3'), (4, NULL);
            INSERT INTO fms.air1 VALUES
              (10, 1, '2026-04-01 00:00:00'), (11, 2, '2026-04-01 00:00:00'), (12, 3, '2026-05-31 23:59:00'),
              (13, 3, '2026-06-01 00:01:00'), (14, 4, '2026-04-01 00:00:00'), (15, NULL, '2026-04-01 00:00:00');
            """;
        await cmd.ExecuteNonQueryAsync();
        return conn;
    }

    private static async Task<IReadOnlyList<long>> ReadAir1IdsAsync(NpgsqlConnection conn, string predicate)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT id FROM fms.air1 AS autheris_target WHERE ({predicate}) ORDER BY id";
        var ids = new List<long>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetInt64(0));
        }

        return ids;
    }

    [RequiresContainerRuntimeFact]
    public async Task PostgreSql_OnlyRowsOfUndeliveredCranesAreVisible()
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable) return;

        await using var conn = await SeedAsync(db);
        var ids = await ReadAir1IdsAsync(conn, Build());

        ids.ShouldBe([11L, 12L, 13L]);
    }

    [RequiresContainerRuntimeFact]
    public async Task PostgreSql_ValidityWindow_CutsAtTheDeliveryDate()
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable) return;

        await using var conn = await SeedAsync(db);
        var window = David with { ValidToColumn = "crane.date_of_delivery" };
        var ids = await ReadAir1IdsAsync(conn, Build(window, Binding with { TimeColumn = "ts" }));

        ids.ShouldBe([11L, 12L]);
    }
}
