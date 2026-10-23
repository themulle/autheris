using Autheris.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Persistence;

/// <summary>PERS-17: the epoch that authorises metadata-cache hits is found by the natural key, because a write inserts the POLICY_EPOCHS row with its own id (not TABLES.id).</summary>
public sealed class TableEpochReadTests
{
    private static SqliteConnection Open(Guid tableId, long epoch)
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE TABLES (id TEXT PRIMARY KEY, source_name TEXT, schema_name TEXT, table_name TEXT);
            CREATE TABLE POLICY_EPOCHS (table_id TEXT PRIMARY KEY, domain TEXT, schema_name TEXT, table_name TEXT, epoch INTEGER);
            """;
        cmd.ExecuteNonQuery();
        cmd.CommandText = "INSERT INTO TABLES VALUES (@id, 'ReplicaDomain', 'dbo', 'T1'); " +
                          "INSERT INTO POLICY_EPOCHS VALUES (@other, 'replicadomain', 'DBO', 't1', @epoch);";
        cmd.Parameters.AddWithValue("@id", tableId.ToString());
        cmd.Parameters.AddWithValue("@other", Guid.NewGuid().ToString());
        cmd.Parameters.AddWithValue("@epoch", epoch);
        cmd.ExecuteNonQuery();
        return conn;
    }

    [Fact]
    public async Task PostgreSql_EpochRowWithOwnId_IsFoundByNaturalKey_AndChangesOnBump()
    {
        var id = Guid.NewGuid();
        await using var conn = Open(id, 1);
        (await PostgreSqlGovernanceRepository.ReadTableEpochAsync(conn, id, default)).ShouldBe(1);
        (await PostgreSqlGovernanceRepository.ReadAllTableEpochsAsync(conn, default))[id].ShouldBe(1);

        await using var bump = conn.CreateCommand();
        bump.CommandText = "UPDATE POLICY_EPOCHS SET epoch = 2";
        bump.ExecuteNonQuery();
        (await PostgreSqlGovernanceRepository.ReadTableEpochAsync(conn, id, default)).ShouldBe(2);
        (await PostgreSqlGovernanceRepository.ReadTableEpochAsync(conn, Guid.NewGuid(), default)).ShouldBe(-1);
    }

    [Fact]
    public async Task SqlServer_EpochRowWithOwnId_IsFoundByNaturalKey_AndChangesOnBump()
    {
        var id = Guid.NewGuid();
        await using var conn = Open(id, 1);
        (await SqlServerGovernanceRepository.ReadTableEpochAsync(conn, id, default)).ShouldBe(1);
        (await SqlServerGovernanceRepository.ReadAllTableEpochsAsync(conn, default))[id].ShouldBe(1);

        await using var bump = conn.CreateCommand();
        bump.CommandText = "UPDATE POLICY_EPOCHS SET epoch = 2";
        bump.ExecuteNonQuery();
        (await SqlServerGovernanceRepository.ReadTableEpochAsync(conn, id, default)).ShouldBe(2);
    }
}
