namespace Autheris.Tests.Integration.Persistence;

using System;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Infrastructure.Persistence.Migrations;
using Autheris.Infrastructure.Persistence.Migrations.Shape;
using Autheris.Tests.Integration.Governance;
using Microsoft.Data.SqlClient;
using Npgsql;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class DiscoveryArtifactProvenanceMigrationStep0040Tests
{
    private const string StepName = "discovery_artifact_provenance";

    private const string PostgreSqlPreShape = """
        CREATE SCHEMA pre_step_0040;
        SET search_path TO pre_step_0040;
        CREATE TABLE catalog_discovery_artifacts (
            table_id TEXT PRIMARY KEY,
            tenant_id TEXT NOT NULL,
            samples_json TEXT NULL,
            statistics_json TEXT NULL,
            content_sha256 CHARACTER(64) NOT NULL,
            uploaded_by TEXT NOT NULL,
            uploaded_at TEXT NOT NULL,
            schema_version INTEGER NOT NULL
        );
        """;

    private const string SqlServerPreShape = """
        CREATE TABLE dbo.CATALOG_DISCOVERY_ARTIFACTS (
            table_id NVARCHAR(128) NOT NULL PRIMARY KEY,
            tenant_id NVARCHAR(128) NOT NULL,
            samples_json NVARCHAR(MAX) NULL,
            statistics_json NVARCHAR(MAX) NULL,
            content_sha256 CHAR(64) NOT NULL,
            uploaded_by NVARCHAR(256) NOT NULL,
            uploaded_at NVARCHAR(64) NOT NULL,
            schema_version INT NOT NULL
        );
        """;

    [Theory]
    [InlineData("postgresql")]
    [InlineData("sqlserver")]
    public async Task Existing_rows_unknown(string engine)
    {
        await using var db = await OpenPreStepAsync(engine);
        if (db is null) return;

        var table = engine == "postgresql" ? "catalog_discovery_artifacts" : "dbo.CATALOG_DISCOVERY_ARTIFACTS";
        var existingId = Guid.NewGuid().ToString("D");
        var sha = new string('a', 64);

        await ExecAsync(db.Connection, $$"""
            INSERT INTO {{table}} (table_id, tenant_id, samples_json, statistics_json, content_sha256, uploaded_by, uploaded_at, schema_version)
            VALUES ('{{existingId}}', 't1', '[{"id":1}]', NULL, '{{sha}}', 'legacy', '2026-11-01T00:00:00Z', 1);
            """);

        await RunStepAsync(db);
        await RunStepAsync(db);

        var prov = await ScalarStringAsync(db.Connection, $"SELECT provenance FROM {table} WHERE table_id = '{existingId}'");
        prov.ShouldBe("UNKNOWN");

        await ExecAsync(db.Connection, $$"""
            INSERT INTO {{table}} (table_id, tenant_id, samples_json, statistics_json, content_sha256, uploaded_by, uploaded_at, schema_version, provenance)
            VALUES ('{{existingId}}', 't1', '[{"id":2}]', NULL, '{{sha}}', 'dbt-ingestion', '2026-11-02T00:00:00Z', 1, 'IMPORT');
            """);

        const string statsJson = """{"table":{"approxRowCount":5}}""";
        await Should.ThrowAsync<DbException>(async () =>
            await ExecAsync(db.Connection, $$"""
                INSERT INTO {{table}} (table_id, tenant_id, samples_json, statistics_json, content_sha256, uploaded_by, uploaded_at, schema_version, provenance)
                VALUES ('{{Guid.NewGuid():D}}', 't1', '[{"id":3}]', '{{statsJson}}', '{{sha}}', 'dbt', '2026-11-02T00:00:00Z', 1, 'IMPORT');
                """));
    }

    private static async Task<Db?> OpenPreStepAsync(string engine)
    {
        var step = SqlMigrationStep.LoadEmbedded(engine).Single(s => s.Version == 40 && s.Name == StepName);
        if (engine == "postgresql")
        {
            var conn = new NpgsqlConnection(await GovernanceTestDatabase.CreateAsync("prov_0040"));
            await conn.OpenAsync();
            await ExecAsync(conn, PostgreSqlPreShape);
            return new Db(engine, conn, step, conn.DisposeAsync);
        }

        var database = await SqlServerTestDatabase.CreateAsync();
        if (!database.IsAvailable)
        {
            return null;
        }

        var sqlConn = new SqlConnection(database.ConnectionString);
        await sqlConn.OpenAsync();
        await ExecAsync(sqlConn, SqlServerPreShape);

        return new Db(engine, sqlConn, step, async () =>
        {
            await sqlConn.DisposeAsync();
            await database.DisposeAsync();
        });
    }

    private static async Task RunStepAsync(Db db)
    {
        await using var tx = await db.Connection.BeginTransactionAsync();
        try
        {
            foreach (var batch in db.Step.Batches)
            {
                await ExecAsync(db.Connection, batch, tx);
            }

            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    private static async Task ExecAsync(DbConnection conn, string sql, DbTransaction? tx = null)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<string?> ScalarStringAsync(DbConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var result = await cmd.ExecuteScalarAsync();
        return result is null or DBNull ? null : Convert.ToString(result);
    }

    private sealed record Db(string Engine, DbConnection Connection, SqlMigrationStep Step, Func<ValueTask> Dispose) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Dispose();
    }
}
