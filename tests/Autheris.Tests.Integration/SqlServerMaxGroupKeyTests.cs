namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Analytics;
using Autheris.Application.Sql;
using Microsoft.Data.SqlClient;
using Shouldly;
using Xunit;

/// <summary>#398 (I398-T-10) on a real SQL Server: <c>GROUP BY [nvarchar(max)]</c> equals the nvarchar(200) grouping, and an oversized key stops at the byte budget.</summary>
public sealed class SqlServerMaxGroupKeyTests : IAsyncLifetime
{
    private SqlServerTestDatabase _db = null!;

    public async Task InitializeAsync() => _db = await SqlServerTestDatabase.CreateAsync();

    public async Task DisposeAsync() => await _db.DisposeAsync();

    private async Task<Dictionary<string, long>> GroupAsync(string column)
    {
        await using var conn = new SqlConnection(_db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT [{column}], COUNT(*) FROM dbo.max_keys GROUP BY [{column}]";
        await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess);
        var result = new Dictionary<string, long>();
        long total = 0;
        while (await reader.ReadAsync())
        {
            var key = (string)ResultByteBudget.GroupKeys.ReadValue(reader, 0, ref total)!;
            result[key] = reader.GetInt32(1);
        }

        return result;
    }

    private async Task ExecAsync(string sql)
    {
        await using var conn = new SqlConnection(_db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task I398_T_10_NvarcharMaxGroupKey_EqualsBoundedEquivalent_AndOversizedKeyHitsByteBudget()
    {
        if (!_db.IsAvailable)
        {
            return;
        }

        await ExecAsync("""
            CREATE TABLE dbo.max_keys (id int NOT NULL PRIMARY KEY, k_max nvarchar(max) NULL, k_200 nvarchar(200) NULL);
            INSERT dbo.max_keys VALUES (1, N'north', N'north'), (2, N'north', N'north'), (3, N'south', N'south'), (4, N'', N'');
            """);
        var asMax = await GroupAsync("k_max");
        var bounded = await GroupAsync("k_200");
        asMax.Count.ShouldBe(3);
        asMax.OrderBy(p => p.Key).ToList().ShouldBe(bounded.OrderBy(p => p.Key).ToList());
        asMax["north"].ShouldBe(2);

        await ExecAsync("INSERT dbo.max_keys (id, k_max) VALUES (6, REPLICATE(CAST(N'z' AS nvarchar(max)), 2000000))");
        await Should.ThrowAsync<AggregationRowCapExceededException>(() => GroupAsync("k_max"));
    }
}
