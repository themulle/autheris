namespace Autheris.Tests.Integration;

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Infrastructure.Persistence.Migrations;
using Autheris.Infrastructure.Persistence.Migrations.Shape;
using Microsoft.Data.SqlClient;
using Npgsql;
using Xunit;

/// <summary>
/// Explicit opt-in generator for the per-engine v0001 shape snapshots (issue #388). It applies the frozen v1.5.0 baseline DDL directly
/// to a fresh database (never through the runner, so the comparer and the existing snapshot are not involved and nothing is weakened)
/// and writes the shape that the catalog reader sees. It does nothing unless <c>AUTHERIS_GENERATE_GOVERNANCE_SNAPSHOTS</c> is set to
/// an output directory; copy the files to <c>src/Autheris.Infrastructure/Persistence/Migrations/Shape/</c>.
/// </summary>
public sealed class GovernanceShapeSnapshotGenerator
{
    private const string OutputVariable = "AUTHERIS_GENERATE_GOVERNANCE_SNAPSHOTS";

    private static string? OutputDirectory() => Environment.GetEnvironmentVariable(OutputVariable) is { Length: > 0 } dir ? dir : null;

    [Fact]
    public async Task GeneratePostgreSqlSnapshot()
    {
        if (OutputDirectory() is not { } dir)
        {
            return;
        }

        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        PostgreSqlBaselineSchema.Apply(conn, null);
        var reader = new PostgreSqlSchemaCatalogReader();
        var tables = await reader.ListTablesAsync(conn, CancellationToken.None);
        var shape = await reader.ReadAsync(conn, tables, CancellationToken.None);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "governance-shape.postgresql.v0001.json"), GovernanceSchemaShapeLoader.Serialize(shape));
    }

    [Fact]
    public async Task GenerateSqlServerSnapshot()
    {
        if (OutputDirectory() is not { } dir)
        {
            return;
        }

        await using var db = await SqlServerTestDatabase.CreateAsync();
        await using var conn = new SqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        SqlServerBaselineSchema.Apply(conn);
        var reader = new SqlServerSchemaCatalogReader();
        var tables = await reader.ListTablesAsync(conn, CancellationToken.None);
        var shape = await reader.ReadAsync(conn, tables, CancellationToken.None);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "governance-shape.sqlserver.v0001.json"), GovernanceSchemaShapeLoader.Serialize(shape));
    }
}
