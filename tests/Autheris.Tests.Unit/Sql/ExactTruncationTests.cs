namespace Autheris.Tests.Unit.Sql;

using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Common;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// WebSQL findings 2.4: "truncated" means that more rows exist than were returned. The gateway reads one probe row
/// beyond its row limit and drops it; a table with exactly the limit is not truncated, and a client's own LIMIT up to
/// the maximum never is.
/// </summary>
public sealed class ExactTruncationTests
{
    private static DataTableReader Rows(int count)
    {
        var table = new DataTable();
        table.Columns.Add("id", typeof(long));
        for (int i = 1; i <= count; i++)
        {
            table.Rows.Add((long)i);
        }

        return table.CreateDataReader();
    }

    private static async Task<int> CountAsync(DbDataReader reader)
    {
        int count = 0;
        while (await reader.ReadAsync())
        {
            count++;
        }

        return count;
    }

    [Theory]
    [InlineData(101, 100, 100, true)]
    [InlineData(100, 100, 100, false)]
    [InlineData(5, 100, 5, false)]
    public async Task RowLimitedReader_DeliversAtMostTheLimitAndDetectsMoreRows(int available, long limit, int delivered, bool hasMore)
    {
        await using var reader = new RowLimitedDataReader(Rows(available), limit);

        (await CountAsync(reader)).ShouldBe(delivered);
        reader.HasMoreRows.ShouldBe(hasMore);
        (await reader.ReadAsync()).ShouldBeFalse();
    }

    [Theory]
    [InlineData("SELECT id FROM lwetem_prod.md.crane", 1001, "LIMIT 1001", 1000, true)]
    [InlineData("SELECT id FROM lwetem_prod.md.crane", 1000, "LIMIT 1001", 1000, false)]
    [InlineData("SELECT id FROM lwetem_prod.md.crane LIMIT 60000", 10001, "LIMIT 10001", 10000, true)]
    [InlineData("SELECT id FROM lwetem_prod.md.crane LIMIT 5", 5, "LIMIT 5", 5, false)]
    public async Task GovernedExecution_ProbesOneRowBeyondTheLimit(string sql, int rowsInDatabase, string executedLimit, int returned, bool truncated)
    {
        var (service, _, command) = WebSqlTwoPartNameDataSourceTests.CreateServiceWithCommand(WebSqlTwoPartNameDataSourceTests.CraneId);
        command.ExecuteReaderAsync(Arg.Any<CommandBehavior>(), Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult<DbDataReader>(Rows(rowsInDatabase)));

        var result = await service.ExecuteQueryBufferedAsync(
            new GovernedSqlQueryRequest(sql), WebSqlTwoPartNameDataSourceTests.CreateUser(), new TenantId(WebSqlTwoPartNameDataSourceTests.Tenant));

        command.CommandText.ShouldContain(executedLimit);
        result.RowCount.ShouldBe(returned);
        result.Truncated.ShouldBe(truncated);
    }
}
