using System.Data;
using Microsoft.Data.Sqlite;
using Npgsql;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Persistence.DialectSql;

public sealed class RecordingDbConnectionTests
{
    [Fact]
    public async Task RecordingDbConnection_RecordsCommandsAndParameters()
    {
        using var conn = new RecordingDbConnection(() => new NpgsqlCommand());
        conn.NonQueryResult = 3;
        await using var tx = await conn.BeginTransactionAsync();

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE T SET a = @a";
        cmd.Parameters.Add(new NpgsqlParameter("@a", NpgsqlTypes.NpgsqlDbType.Integer) { Value = 5 });
        var affected = await cmd.ExecuteNonQueryAsync();

        affected.ShouldBe(3);
        conn.Commands.Count.ShouldBe(1);
        conn.Commands[0].CommandText.ShouldBe("UPDATE T SET a = @a");
        conn.Commands[0].TransactionSet.ShouldBeTrue();
        conn.Commands[0].Parameters.Single().ShouldBe(new RecordedParameter("@a", 5, DbType.Int32, "NpgsqlParameter:Integer"));
    }

    [Fact]
    public async Task RecordingDbConnection_ServesQueuedReadersAndHandsOutProviderParameters()
    {
        using var conn = new RecordingDbConnection(() => new SqliteCommand());
        var table = new DataTable();
        table.Columns.Add("id", typeof(string));
        table.Rows.Add("x");
        conn.EnqueueReader(table);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id";
        cmd.CreateParameter().ShouldBeOfType<SqliteParameter>();
        await using var reader = await cmd.ExecuteReaderAsync();

        (await reader.ReadAsync()).ShouldBeTrue();
        reader.GetString(0).ShouldBe("x");
        conn.Commands.Single().TransactionSet.ShouldBeFalse();
    }
}
