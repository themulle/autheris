using System;
using System.Security;
using System.Threading.Tasks;
using Autheris.Application.Olap;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Security;

/// <summary>SR-M-19: quoted or schema-qualified function names must not bypass the OLAP SQL validator.</summary>
public sealed class SrM19DuckDbQuotedFunctionTests
{
    private static DuckDbOlapEngine CreateEngine() => new(
        new DuckDbOlapOptions { Enabled = true, MaxMemory = "256MB", QueryTimeoutSeconds = 10, MaxThreads = 1 },
        NullLogger<DuckDbOlapEngine>.Instance);

    [Theory]
    [InlineData("SELECT * FROM \"range\"(1000000000)")]
    [InlineData("SELECT * FROM \"RANGE\" (1000000000)")]
    [InlineData("SELECT * FROM \"generate_series\"(1, 1000000000)")]
    [InlineData("SELECT * FROM main.range(1000000000)")]
    [InlineData("SELECT * FROM \"main\".\"range\"(1000000000)")]
    [InlineData("SELECT * FROM system.main.range(1000000000)")]
    [InlineData("SELECT * FROM \"system\".\"main\".\"generate_series\"(1, 10)")]
    [InlineData("SELECT * FROM \"duckdb_settings\"()")]
    [InlineData("SELECT \"current_setting\"('threads')")]
    [InlineData("SELECT \"repeat\"('a', 1000000000)")]
    [InlineData("SELECT \"getenv\"('HOME')")]
    [InlineData("SELECT * FROM \"read_csv\"('/etc/passwd')")]
    [InlineData("SELECT * FROM \"iceberg_scan\"('x')")]
    [InlineData("SELECT * FROM \"delta_scan\"('x')")]
    [InlineData("SELECT * FROM \"sqlite_scan\"('x', 'y')")]
    [InlineData("SELECT * FROM \"sqlite_attach\"('x')")]
    [InlineData("SELECT * FROM sqlite_attach('x')")]
    [InlineData("SELECT * FROM \"postgres_scan\"('x', 'y', 'z')")]
    [InlineData("SELECT * FROM \"mysql_scan\"('x', 'y', 'z')")]
    public async Task SR_M_19_QuotedOrQualifiedBlockedFunction_IsRejected(string sql)
    {
        await Should.ThrowAsync<SecurityException>(() =>
            CreateEngine().ExecuteOlapQueryAsync(new OlapQueryRequest(sql, Array.Empty<OlapTableSource>())));
    }

    [Fact]
    public void SR_M_19_Normalize_UnquotesAndUnescapesIdentifiers()
    {
        DuckDbOlapEngine.NormalizeQuotedIdentifiers("SELECT * FROM \"Main\".\"Range\"(1)").ShouldBe("SELECT * FROM main.range(1)");
        DuckDbOlapEngine.NormalizeQuotedIdentifiers("SELECT 'a\"b' FROM \"x\"\"y\"").ShouldBe("SELECT 'a\"b' FROM x_y");
    }
}
