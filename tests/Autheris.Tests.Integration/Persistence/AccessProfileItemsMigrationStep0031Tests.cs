using Autheris.Infrastructure.Persistence.Migrations;
using Autheris.Tests.Integration.Governance;
using Npgsql;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Integration.Persistence;

/// <summary>Step 0031 (PostgreSQL): the profile masking mode and table entries move into item rows; an unmappable row aborts the whole step.</summary>
[Trait("Category", "Security")]
public sealed class AccessProfileItemsMigrationStep0031Tests
{
    private const string PreShape = """
        CREATE SCHEMA pre_step;
        SET search_path TO pre_step;
        CREATE TABLE access_profiles (
            profile_id VARCHAR(128) NOT NULL, tenant_id VARCHAR(64) NOT NULL, name VARCHAR(256) NOT NULL,
            masking_mode VARCHAR(32) NOT NULL DEFAULT 'Default', target_tables TEXT NOT NULL DEFAULT '["*.*"]',
            PRIMARY KEY (tenant_id, profile_id));
        """;

    private static SqlMigrationStep Step() =>
        SqlMigrationStep.LoadEmbedded("postgresql").Single(s => s.Version == 31 && s.Name == "access_profile_items");

    private static async Task<NpgsqlConnection> OpenPreStepAsync(string name, string profilesSql)
    {
        var conn = new NpgsqlConnection(await GovernanceTestDatabase.CreateAsync(name));
        await conn.OpenAsync();
        await ExecAsync(conn, PreShape);
        await ExecAsync(conn, profilesSql);
        return conn;
    }

    [Fact]
    public async Task Step0031_Backfill_MapsModesAndDedupes()
    {
        await using var conn = await OpenPreStepAsync("profile_items_backfill", """
            INSERT INTO access_profiles (profile_id, tenant_id, name, masking_mode, target_tables) VALUES
              ('p-unmasked', 't1', 'a', 'unmasked', '["tem.*","md.x","tem.*"]'),
              ('p-default', 't1', 'b', 'Default', '["z.b","a.b"]'),
              ('p-strict', 't2', 'c', 'STRICT', '[]'),
              ('p-default', 't2', 'd', 'Strict', '["Case.X","case.x"]');
            """);

        foreach (var batch in Step().Batches)
        {
            await ExecAsync(conn, batch);
        }

        (await ScalarAsync(conn, "SELECT access_level FROM access_profile_classification_access WHERE tenant_id = 't1' AND profile_id = 'p-unmasked' AND code = 'PII'")).ShouldBe(2);
        (await ScalarAsync(conn, "SELECT access_level FROM access_profile_classification_access WHERE tenant_id = 't1' AND profile_id = 'p-default' AND code = 'PII'")).ShouldBe(1);
        (await ScalarAsync(conn, "SELECT access_level FROM access_profile_classification_access WHERE tenant_id = 't2' AND profile_id = 'p-strict' AND code = 'PII'")).ShouldBe(0);
        (await ScalarAsync(conn, "SELECT access_level FROM access_profile_classification_access WHERE tenant_id = 't2' AND profile_id = 'p-default' AND code = 'PII'")).ShouldBe(0);
        (await ScalarAsync(conn, "SELECT COUNT(*) FROM access_profile_items WHERE profile_id = 'p-unmasked'")).ShouldBe(2);
        (await ScalarAsync(conn, "SELECT item_no FROM access_profile_items WHERE profile_id = 'p-unmasked' AND object_ref = 'md.x'")).ShouldBe(2);
        (await ScalarAsync(conn, "SELECT item_no FROM access_profile_items WHERE tenant_id = 't1' AND profile_id = 'p-default' AND object_ref = 'z.b'")).ShouldBe(1);
        (await ScalarAsync(conn, "SELECT COUNT(*) FROM access_profile_items WHERE profile_id = 'p-strict'")).ShouldBe(0);
        (await ScalarAsync(conn, "SELECT COUNT(*) FROM access_profile_items WHERE tenant_id = 't2' AND profile_id = 'p-default'")).ShouldBe(2);
        (await ScalarAsync(conn, "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = 'pre_step' AND table_name = 'access_profiles' AND column_name IN ('masking_mode', 'target_tables')")).ShouldBe(0);
    }

    [Theory]
    [InlineData("'Bogus'", "'[\"a.b\"]'")]
    [InlineData("'Default'", "'not json'")]
    [InlineData("'Default'", "'{\"a\":\"b\"}'")]
    [InlineData("'Default'", "'[\"a.b\", 5]'")]
    [InlineData("'Default'", "'[\"a.b\", null]'")]
    [InlineData("'Default'", "'[\"a.b\", \"   \"]'")]
    public async Task Step0031_BadRow_AbortsAndLeavesColumnsIntact(string mode, string tables)
    {
        await using var conn = await OpenPreStepAsync("profile_items_abort", $"""
            INSERT INTO access_profiles (profile_id, tenant_id, name, masking_mode, target_tables) VALUES
              ('p-good', 't1', 'a', 'Default', '["a.b"]'),
              ('p-bad', 't1', 'b', {mode}, {tables});
            """);

        var failure = await Should.ThrowAsync<PostgresException>(async () =>
        {
            foreach (var batch in Step().Batches)
            {
                await ExecAsync(conn, batch);
            }
        });

        failure.MessageText.ShouldContain("p-bad");
        failure.MessageText.ShouldNotContain("not json");
        (await ScalarAsync(conn, "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = 'pre_step' AND table_name = 'access_profiles' AND column_name IN ('masking_mode', 'target_tables')")).ShouldBe(2);
        (await ScalarAsync(conn, "SELECT COUNT(*) FROM access_profile_items")).ShouldBe(0);
    }

    [Fact]
    public async Task Step0031_FreshInstallMatchesShape()
    {
        await using var conn = new NpgsqlConnection(await GovernanceTestDatabase.CreateAsync("profile_items_fresh"));
        await conn.OpenAsync();

        (await ScalarAsync(conn, "SELECT COUNT(*) FROM information_schema.columns WHERE table_name = 'access_profiles' AND column_name IN ('masking_mode', 'target_tables')")).ShouldBe(0);
        (await ScalarAsync(conn, "SELECT COUNT(*) FROM information_schema.columns WHERE table_name = 'access_profile_items' AND column_name IN ('tenant_id', 'profile_id', 'item_no', 'object_ref')")).ShouldBe(4);
        (await ScalarAsync(conn, "SELECT COUNT(*) FROM information_schema.columns WHERE table_name = 'access_profile_classification_access' AND column_name IN ('tenant_id', 'profile_id', 'code', 'access_level')")).ShouldBe(4);
    }

    private static async Task ExecAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }
}
