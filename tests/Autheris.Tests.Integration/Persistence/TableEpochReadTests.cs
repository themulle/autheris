using Autheris.Infrastructure.Persistence;
using Autheris.Tests.Common;
using Npgsql;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Integration.Persistence;

/// <summary>PERS-17: the epoch that authorises metadata-cache hits is found by the natural key, because a write inserts the policy_epochs row with its own id (not tables.id).</summary>
public sealed class TableEpochReadTests
{
    private static async Task<NpgsqlConnection> SeedAsync(PostgreSqlTestDatabase db, Guid tableId, long epoch)
    {
        var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE public.tables (id TEXT PRIMARY KEY, source_name TEXT, schema_name TEXT, table_name TEXT);
            CREATE TABLE public.policy_epochs (table_id TEXT PRIMARY KEY, domain TEXT, schema_name TEXT, table_name TEXT, epoch BIGINT);
            INSERT INTO public.tables VALUES (@id, 'ReplicaDomain', 'dbo', 'T1');
            INSERT INTO public.policy_epochs VALUES (@other, 'replicadomain', 'DBO', 't1', @epoch);
            """;
        cmd.Parameters.AddWithValue("id", tableId.ToString());
        cmd.Parameters.AddWithValue("other", Guid.NewGuid().ToString());
        cmd.Parameters.AddWithValue("epoch", epoch);
        await cmd.ExecuteNonQueryAsync();
        return conn;
    }

    [RequiresContainerRuntimeFact]
    public async Task EpochRowWithOwnId_IsFoundByNaturalKey_AndChangesOnBump()
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable)
        {
            return;
        }

        var id = Guid.NewGuid();
        await using var conn = await SeedAsync(db, id, 1);

        (await GovernanceRepository.ReadTableEpochAsync(conn, id, default)).ShouldBe(1);
        (await GovernanceRepository.ReadAllTableEpochsAsync(conn, default))[id].ShouldBe(1);

        await using var bump = conn.CreateCommand();
        bump.CommandText = "UPDATE public.policy_epochs SET epoch = 2";
        await bump.ExecuteNonQueryAsync();

        (await GovernanceRepository.ReadTableEpochAsync(conn, id, default)).ShouldBe(2);
        (await GovernanceRepository.ReadTableEpochAsync(conn, Guid.NewGuid(), default)).ShouldBe(-1);
    }
}
