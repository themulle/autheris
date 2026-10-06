using System;
using System.Security;
using System.Threading.Tasks;
using Autheris.Application.Olap;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Autheris.Tests.Unit.Security;

public sealed class DuckDbOlapValidatorG1Tests
{
    private static DuckDbOlapEngine CreateEngine() => new(
        new DuckDbOlapOptions { Enabled = true, MaxMemory = "256MB", QueryTimeoutSeconds = 10, MaxThreads = 1 },
        NullLogger<DuckDbOlapEngine>.Instance);

    [Theory]
    [InlineData("SELECT * FROM range (1000000000)")]
    [InlineData("SELECT * FROM range\t(1000000000)")]
    [InlineData("SELECT * FROM generate_series  (1, 100000000)")]
    public async Task GeneratorWithWhitespaceBeforeParenthesis_IsRejected(string sql)
    {
        await Assert.ThrowsAsync<SecurityException>(async () =>
            await CreateEngine().ExecuteOlapQueryAsync(new OlapQueryRequest(Sql: sql, Sources: [])));
    }

    [Fact]
    public async Task BackslashIsNoQuoteEscape_SecondStatementIsDetected()
    {
        // In DuckDB 'a\' is a complete literal, so the ';' is outside quotes.
        await Assert.ThrowsAsync<SecurityException>(async () =>
            await CreateEngine().ExecuteOlapQueryAsync(new OlapQueryRequest(Sql: "SELECT 'a\\' ; SELECT 2", Sources: [])));
    }
}
