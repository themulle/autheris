namespace Autheris.Tests.Integration.DataApi;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.DataSources.Services;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Tests.Common;
using Npgsql;
using Shouldly;

/// <summary>
/// PostgreSQL integration test for Issue #390 mixed-direction keyset streaming pagination.
/// </summary>
public sealed class Issue390ReadOrderDirectionIntegrationTests
{
    private const KeyColumnDirection Asc = KeyColumnDirection.Ascending;
    private const KeyColumnDirection Desc = KeyColumnDirection.Descending;
    private const string Tenant = "tenant-1";
    private const string Caller = "S-1-5-21-caller";
    private static readonly TableIdentifier Orders = new("sales", "dbo", "orders");

    private static TableMetadata StreamMeta(IReadOnlyList<KeyColumnDirection>? directions) => new()
    {
        Identifier = Orders,
        Table = new Table { TableName = "orders", SchemaName = "dbo", SourceName = "sales", SourceType = "PostgreSQL" },
        PrimaryKeyColumns = ["grp", "ts"],
        PrimaryKeyDirections = directions ?? [],
        Columns =
        [
            new TableColumn { ColumnName = "grp", DataType = "integer" },
            new TableColumn { ColumnName = "ts", DataType = "integer" },
            new TableColumn { ColumnName = "payload", DataType = "varchar" }
        ]
    };

    private static TableAccessDecision AllClear(TableMetadata m) => TableAccessDecision.Allowed(
        m.Identifier,
        m.Columns.ToDictionary(c => c.ColumnName, _ => ColumnAccessLevel.Clear, StringComparer.OrdinalIgnoreCase),
        hasUnconstrainedColumnAllow: true);

    private static string Issue(
        KeysetCursorEncoder encoder,
        TableMetadata meta,
        BulkTableStreamRequest request,
        IReadOnlyDictionary<string, object?> values,
        IReadOnlyList<KeyColumnDirection>? directions) =>
        encoder.EncodeCursor(values, KeysetCursorContextFactory.ForBulkStream(Tenant, meta, BulkStreamKey.TryResolve(meta)!.Columns, Caller, request, directions));

    [Trait("Category", "Security")]
    [RequiresContainerRuntimeTheory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(1, 1)]
    public async Task I390_T_08_PlanKeysetStream_MixedDirections_NoSkipNoDuplicateAcrossPages(int firstDesc, int secondDesc)
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable)
        {
            return;
        }

        var directions = new[] { firstDesc == 1 ? Desc : Asc, secondDesc == 1 ? Desc : Asc };
        var encoder = new KeysetCursorEncoder("signing-key-for-i390-tests-0123456789");
        var planner = new GovernedTableReadPlanner(encoder);
        var meta = StreamMeta(directions);
        var request = new BulkTableStreamRequest(Select: ["grp", "ts", "payload"], Limit: 3);

        await using var connection = new NpgsqlConnection(db.ConnectionString);
        await connection.OpenAsync();
        await using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE SCHEMA IF NOT EXISTS dbo; CREATE TABLE dbo.orders (grp integer NOT NULL, ts integer NOT NULL, payload text, PRIMARY KEY (grp, ts))";
            await create.ExecuteNonQueryAsync();
        }

        // Ties on the first column (4 rows per group), inserted in a scrambled order.
        var rows = (from g in Enumerable.Range(1, 3) from t in Enumerable.Range(1, 4) select (g, t)).OrderBy(r => (r.g * 7 + r.t * 13) % 11).ToList();
        foreach (var (g, t) in rows)
        {
            await using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO dbo.orders VALUES (@g, @t, 'p')";
            insert.Parameters.AddWithValue("@g", g);
            insert.Parameters.AddWithValue("@t", t);
            await insert.ExecuteNonQueryAsync();
        }

        var expected = rows.Order(Comparer<(int g, int t)>.Create((a, b) =>
        {
            var byGroup = firstDesc == 1 ? b.g.CompareTo(a.g) : a.g.CompareTo(b.g);
            return byGroup != 0 ? byGroup : secondDesc == 1 ? b.t.CompareTo(a.t) : a.t.CompareTo(b.t);
        })).ToList();

        var seen = new List<(int g, int t)>();
        string? cursor = null;
        for (var page = 0; page < 20; page++)
        {
            var planned = planner.PlanKeysetStream(request with { Cursor = cursor }, meta, AllClear(meta), Tenant, Caller);
            await using var query = connection.CreateCommand();
            query.CommandText = planned.Sql.Replace("FROM sales.dbo.orders", "FROM dbo.orders", StringComparison.Ordinal);
            foreach (var (name, value) in planned.Parameters)
            {
                query.Parameters.AddWithValue(name, value ?? DBNull.Value);
            }

            var batch = new List<(int g, int t)>();
            await using (var reader = await query.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    batch.Add((reader.GetInt32(0), reader.GetInt32(1)));
                }
            }

            var take = batch.Take(3).ToList();
            seen.AddRange(take);
            if (batch.Count <= 3)
            {
                break;
            }

            var last = take[^1];
            cursor = Issue(encoder, meta, request, new Dictionary<string, object?> { ["grp"] = last.g, ["ts"] = last.t }, directions);
        }

        seen.ShouldBe(expected, "every row exactly once, in the declared order, across all pages");
    }
}
