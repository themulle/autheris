using Autheris.Infrastructure.Persistence.Migrations;
using Autheris.Tests.Integration.Governance;
using Npgsql;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Integration.Persistence;

public sealed class PolicyEpochsMigrationStep0015Tests
{
    [Fact]
    public async Task Migration_0015_Backfills_Existing_PolicyEpochs_Rows_To_CatalogState_Sum()
    {
        var step = SqlMigrationStep.LoadEmbedded("postgresql")
            .Single(s => s.Version == 16 && s.Name == "policy_epochs_change_version");

        // The pre-step shape lives in a dedicated schema of a throwaway clone; the unqualified step SQL resolves there only.
        await using var conn = new NpgsqlConnection(await GovernanceTestDatabase.CreateAsync("policy_epochs_step"));
        await conn.OpenAsync();

        await Exec(conn, """
            CREATE SCHEMA pre_step;
            SET search_path TO pre_step;
            CREATE TABLE CATALOG_STATE (
                id INTEGER PRIMARY KEY,
                structure_version BIGINT NOT NULL,
                content_version BIGINT NOT NULL,
                updated_at TEXT NULL
            );
            INSERT INTO CATALOG_STATE (id, structure_version, content_version) VALUES (1, 3, 5);
            CREATE TABLE POLICY_EPOCHS (
                table_id TEXT PRIMARY KEY,
                domain TEXT NOT NULL,
                schema_name TEXT NOT NULL,
                table_name TEXT NOT NULL,
                epoch BIGINT NOT NULL,
                updated_at TEXT NULL
            );
            INSERT INTO POLICY_EPOCHS (table_id, domain, schema_name, table_name, epoch, updated_at)
            VALUES ('t-1', 'sales', 'dbo', 'orders', 2, '2026-11-15T00:00:00Z');
            """);

        foreach (var batch in step.Batches)
        {
            await Exec(conn, batch);
        }

        await using var check = new NpgsqlCommand("SELECT change_version FROM POLICY_EPOCHS WHERE table_id = 't-1'", conn);
        Convert.ToInt64(await check.ExecuteScalarAsync()).ShouldBe(8);
    }

    private static async Task Exec(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
