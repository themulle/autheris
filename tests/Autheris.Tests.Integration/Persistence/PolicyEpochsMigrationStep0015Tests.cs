using Autheris.Infrastructure.Persistence.Migrations;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Integration.Persistence;

public sealed class PolicyEpochsMigrationStep0015Tests
{
    [Fact]
    public void Migration_0015_Backfills_Existing_PolicyEpochs_Rows_To_CatalogState_Sum()
    {
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();

        Exec(conn, """
            CREATE TABLE CATALOG_STATE (
                id INTEGER PRIMARY KEY,
                structure_version INTEGER NOT NULL,
                content_version INTEGER NOT NULL,
                updated_at TEXT NULL
            );
            INSERT INTO CATALOG_STATE (id, structure_version, content_version) VALUES (1, 3, 5);
            CREATE TABLE POLICY_EPOCHS (
                table_id TEXT PRIMARY KEY,
                domain TEXT NOT NULL,
                schema_name TEXT NOT NULL,
                table_name TEXT NOT NULL,
                epoch INTEGER NOT NULL,
                updated_at TEXT NULL
            );
            INSERT INTO POLICY_EPOCHS (table_id, domain, schema_name, table_name, epoch, updated_at)
            VALUES ('t-1', 'sales', 'dbo', 'orders', 2, '2026-11-15T00:00:00Z');
            """);

        Exec(conn, "ALTER TABLE POLICY_EPOCHS ADD COLUMN change_version BIGINT NOT NULL DEFAULT 0;");
        Exec(conn, """
            UPDATE POLICY_EPOCHS
            SET change_version = COALESCE((SELECT structure_version + content_version FROM CATALOG_STATE WHERE id = 1), 1)
            WHERE change_version <= 0;
            """);
        Exec(conn, "CREATE INDEX IF NOT EXISTS ix_policy_epochs_change_version ON POLICY_EPOCHS (change_version);");

        using var check = conn.CreateCommand();
        check.CommandText = "SELECT change_version FROM POLICY_EPOCHS WHERE table_id = 't-1';";
        var backfilled = Convert.ToInt64(check.ExecuteScalar());
        backfilled.ShouldBe(8);

        var step = SqlMigrationStep.LoadEmbedded("postgresql")
            .Single(s => s.Version == 15 && s.Name == "policy_epochs_change_version");
        step.Sql.ShouldContain("change_version");
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
